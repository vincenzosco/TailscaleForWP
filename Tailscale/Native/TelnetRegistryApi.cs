using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Tailscale.Core;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Tailscale.Native
{
    /// <summary>Un guscio di comandi: una riga dentro, la risposta fuori.</summary>
    public interface ITelnetShell
    {
        string Execute(string command);
    }

    /// <summary>
    /// Un guscio telnet locale (127.0.0.1): sul telefono interop-unlocked CMD
    /// Injector espone i comandi come servizio, e da li' i `reg` girano con i
    /// diritti che servono. E' il canale "via telnet cmdinjector".
    ///
    /// Tre cose che un telnet fa e che qui si aspettano: la negoziazione IAC in
    /// ingresso (byte 0xFF che non sono testo), un banner prima del primo
    /// comando, e risposte che finiscono per silenzio invece che per un prompt
    /// fisso. Il silenzio e' l'unica fine universale: 700 ms senza byte dopo
    /// l'ultimo, con un tetto di 10 secondi per risposta.
    ///
    /// Nessun test automatico raggiunge questa classe: chiama API che esistono
    /// solo sul telefono, come tutti gli adattatori WinRT dell'app. La verifica e'
    /// la build ARM e la corsa sul device.
    /// </summary>
    public sealed class LocalTelnetShell : ITelnetShell
    {
        private const int ConnectTimeoutMs = 1500;
        private const int QuietMs = 700;
        private const int AnswerCeilingMs = 10000;

        private readonly string _host;
        private readonly string _port;
        private readonly object _gate = new object();

        private StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;
        private Task<uint> _pending;

        public LocalTelnetShell(string host, int port)
        {
            if (host == null) throw new ArgumentNullException("host");
            _host = host;
            _port = port.ToString();
        }

        public string Execute(string command)
        {
            if (command == null) throw new ArgumentNullException("command");
            lock (_gate)
            {
                EnsureConnected();

                _writer.WriteString(command + "\r\n");
                if (!_writer.StoreAsync().AsTask().Wait(ConnectTimeoutMs))
                {
                    // Anche una scrittura scaduta lascia la connessione a meta':
                    // si butta via il socket e si dice, invece di leggere a vuoto.
                    Reset();
                    throw new InvalidOperationException("telnet non raggiungibile su " + _host + ":" + _port
                                                        + " (scrittura scaduta)");
                }

                List<byte> collected = new List<byte>();
                DateTime ceiling = DateTime.UtcNow.AddMilliseconds(AnswerCeilingMs);
                while (DateTime.UtcNow < ceiling)
                {
                    byte[] chunk = ReadChunk(QuietMs);
                    if (chunk == null) break;              // silenzio: la risposta e' completa
                    if (chunk.Length == 0) { Reset(); break; }  // stream chiuso dal peer
                    for (int i = 0; i < chunk.Length; i++) collected.Add(chunk[i]);
                }
                return Decode(collected);
            }
        }

        private void EnsureConnected()
        {
            if (_socket != null) return;

            StreamSocket socket = new StreamSocket();
            try
            {
                // Un'attesa scaduta non e' un'eccezione: senza questo controllo il
                // socket resta appeso a meta' e la risposta diventa il vuoto.
                if (!socket.ConnectAsync(new HostName(_host), _port).AsTask().Wait(ConnectTimeoutMs))
                    throw new TimeoutException("connessione scaduta dopo " + ConnectTimeoutMs + " ms");
            }
            catch (Exception ex)
            {
                // Un'attesa scaduta lascia la connessione a meta': si butta via il
                // socket invece di riutilizzarne uno incerto.
                try { socket.Dispose(); }
                catch (Exception) { }
                throw new InvalidOperationException("telnet non raggiungibile su " + _host + ":" + _port
                                                    + " (" + ex.GetType().Name + ")");
            }

            _socket = socket;
            _reader = new DataReader(_socket.InputStream);
            _reader.InputStreamOptions = InputStreamOptions.Partial;
            _writer = new DataWriter(_socket.OutputStream);
            _pending = null;
        }

        private void Reset()
        {
            try { if (_writer != null) _writer.Dispose(); }
            catch (Exception) { }
            try { if (_reader != null) _reader.Dispose(); }
            catch (Exception) { }
            try { if (_socket != null) _socket.Dispose(); }
            catch (Exception) { }
            _writer = null;
            _reader = null;
            _socket = null;
            _pending = null;
        }

        /// <summary>
        /// Un pezzo di risposta, o null se in `timeoutMs` non e' arrivato niente.
        /// La lettura in sospeso non si butta: resta per la chiamata dopo, perche'
        /// due LoadAsync sovrapposti sono un errore.
        /// </summary>
        private byte[] ReadChunk(int timeoutMs)
        {
            if (_pending == null) _pending = _reader.LoadAsync(4096).AsTask();
            if (!_pending.Wait(timeoutMs)) return null;

            Task<uint> done = _pending;
            _pending = null;
            uint loaded = done.Result;
            if (loaded == 0) return new byte[0];

            byte[] bytes = new byte[loaded];
            _reader.ReadBytes(bytes);
            return bytes;
        }

        /// <summary>
        /// Toglie la negoziazione telnet e tiene il testo. I byte 0xFF aprono una
        /// sequenza di comando: WILL/WONT/DO/DONT piu' un'opzione, un comando a
        /// due byte (NOP..GA), oppure una subnegoziazione fino a 0xFF 0xF0. Non
        /// sono testo e non devono finire nella risposta che il parser legge.
        /// </summary>
        internal static string Decode(IList<byte> bytes)
        {
            StringBuilder text = new StringBuilder();
            int i = 0;
            while (i < bytes.Count)
            {
                byte b = bytes[i];
                if (b != 0xFF)
                {
                    text.Append((char)b);
                    i++;
                    continue;
                }

                // Un IAC in coda, senza piu' niente dopo, non e' testo: si butta.
                if (i + 1 >= bytes.Count) break;

                byte command = bytes[i + 1];
                if (command == 0xFA)
                {
                    // Subnegoziazione: fino a IAC SE (0xFF 0xF0).
                    i += 2;
                    while (i + 1 < bytes.Count && !(bytes[i] == 0xFF && bytes[i + 1] == 0xF0)) i++;
                    i += 2;
                    continue;
                }
                if (command == 0xFF)
                {
                    // 0xFF 0xFF: un 0xFF letterale nel testo.
                    text.Append((char)0xFF);
                    i += 2;
                    continue;
                }
                if (command >= 0xFB && command <= 0xFE)
                {
                    // WILL/WONT/DO/DONT piu' l'opzione.
                    i += 3;
                    continue;
                }
                // NOP..GA e compagnia: comandi a due byte, senza opzione. Prima
                // si saltavano tre byte e si inghiottiva un byte di testo vero.
                i += 2;
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// <see cref="IRegistryApi"/> attraverso reg.exe: query, add, delete. E' la
    /// via quando il registro diretto non si puo' aprire (l'app non ha i diritti)
    /// ma il telefono espone il guscio: stessi comandi del payload per CMD
    /// Injector, stesso esito.
    ///
    /// La lettura si fa leggendo il testo che `reg query` stampa, con
    /// <see cref="RegQueryParser"/> (che e' nel core, e ha i suoi test). Una
    /// scrittura che `reg` rifiuta diventa UnauthorizedAccessException: e' cio'
    /// che il probe legge come "telefono bloccato", e un rifiuto non deve mai
    /// passare per un successo silenzioso.
    /// </summary>
    public sealed class TelnetRegistryApi : IRegistryApi
    {
        private readonly ITelnetShell _shell;

        public TelnetRegistryApi(ITelnetShell shell)
        {
            if (shell == null) throw new ArgumentNullException("shell");
            _shell = shell;
        }

        public bool TryRead(string hive, string path, string name, out RegValue value)
        {
            CheckShell(hive, "hive");
            CheckShell(path, "percorso");
            CheckShell(name, "nome del valore");
            string output = _shell.Execute("reg query \"" + Key(hive, path) + "\" /v \"" + name + "\"");
            if (RegQueryParser.TryParseValue(output, name, out value)) return true;

            // Assenza dichiarata: reg.exe ha detto che il valore non c'e'.
            if (IsNotFound(output))
            {
                value = null;
                return false;
            }

            // Ne' valore ne' assenza: la risposta e' vuota o illeggibile (finestra
            // di silenzio, risposta monca, decode corrotto). Un'assenza inventata
            // qui diventerebbe un backup "<assente>" e il ripristino cancellerebbe
            // il valore dell'utente: si fallisce e non si scrive niente.
            value = null;
            throw new InvalidOperationException("reg query illeggibile per "
                                                + hive + "\\" + path + "\\" + name);
        }

        public void Write(string hive, string path, string name, RegValue value)
        {
            if (value == null) throw new ArgumentNullException("value");
            CheckShell(hive, "hive");
            CheckShell(path, "percorso");
            CheckShell(name, "nome del valore");
            CheckShell(value.Text, "testo del valore");
            string type = value.Kind == RegValue.Dword ? "REG_DWORD" : "REG_SZ";
            string data = value.Kind == RegValue.Dword ? value.Text : "\"" + value.Text + "\"";
            string output = _shell.Execute("reg add \"" + Key(hive, path) + "\" /v \"" + name
                                           + "\" /t " + type + " /d " + data + " /f");
            if (IsError(output))
                throw new UnauthorizedAccessException("reg add ha rifiutato " + hive + "\\" + path + "\\" + name
                                                      + ": " + FirstLine(output));
        }

        public void Delete(string hive, string path, string name)
        {
            CheckShell(hive, "hive");
            CheckShell(path, "percorso");
            CheckShell(name, "nome del valore");
            string output = _shell.Execute("reg delete \"" + Key(hive, path) + "\" /v \"" + name + "\" /f");
            // Il valore che non c'e' e' gia' nello stato desiderato: non e' un
            // errore. Un rifiuto vero (accesso negato) lo e'.
            if (IsNotFound(output) || !IsError(output)) return;
            throw new UnauthorizedAccessException("reg delete ha rifiutato " + hive + "\\" + path + "\\" + name
                                                  + ": " + FirstLine(output));
        }

        private static string Key(string hive, string path)
        {
            return hive + "\\" + path;
        }

        /// <summary>
        /// Prima di comporre un comando, ogni testo che ci finisce dentro passa
        /// un vaglio stretto: nel guscio una `"` esce dalle virgolette e una `&`,
        /// una `|` o una `%` comandano, con i diritti di CMD Injector — e nomi e
        /// testi arrivano anche dal registro, nel ripristino. La regola e' una
        /// lista di cio' che e' ammesso (lettere, cifre, spazio e punteggiatura
        /// che non comanda niente): tutto il resto si rifiuta e il comando non
        /// viene composto. Il messaggio nomina il campo, mai il testo: nei log
        /// non vanno i contenuti.
        /// </summary>
        private static void CheckShell(string text, string what)
        {
            if (text == null) throw new ArgumentNullException(what);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = c == ' '
                    || (c >= '0' && c <= '9')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || SafeShellChars.IndexOf(c) >= 0;
                if (!ok)
                    throw new ArgumentException(what
                        + " con caratteri che nel guscio comandano: si rifiuta, nessun comando composto");
            }
        }

        /// <summary>La punteggiatura che nel guscio non comanda niente.</summary>
        private const string SafeShellChars = ".,:;=+-_/\\()[]{}@#!?'*~";

        private static bool IsError(string output)
        {
            return output != null && output.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNotFound(string output)
        {
            if (output == null) return false;
            return output.IndexOf("Impossibile trovare", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("unable to find", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("cannot find", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstLine(string output)
        {
            if (output == null) return "";
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            return lines.Length > 0 ? lines[0].Trim() : "";
        }
    }
}