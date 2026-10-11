using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Tailscale.Core.Net;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Control
{
    /// <summary>
    /// Il canale reale verso il control server: HTTP/2 sopra i frame Noise.
    ///
    /// La sequenza, come nel client Go (`SetUnencryptedHTTP2` in
    /// control/ts2021/client.go): dopo l'handshake Noise il server puo' mandare un
    /// early payload e poi parla HTTP/2. Il corpo della netmap e' una sequenza di
    /// messaggi prefissati da una lunghezza uint32 *little-endian*
    /// (`readMapResponseMessage` in control/controlclient/direct.go), non JSON
    /// concatenato: senza il prefisso ogni confine di messaggio e' indovinato.
    ///
    /// Il watchdog e' quello del client Go: se non arriva nulla per due minuti (su
    /// uno stream che altrimenti resterebbe aperto per giorni) la connessione
    /// viene chiusa, cosi' il chiamante la rifa'. Senza di esso un server che
    /// smette di rispondere lascerebbe la lettura bloccata per sempre e l'app
    /// "connessa" ma morta.
    /// </summary>
    public sealed class NoiseControlConnection : IControlConnection
    {
        /// <summary>Lo stesso tetto del client Go per un corpo di risposta: 1 MiB.</summary>
        public const int MaxBodyBytes = 1 << 20;

        /// <summary>
        /// Tetto per un singolo messaggio della netmap. Il riferimento ammette
        /// fino a 256 MiB; il traffico reale arriva a 16 MiB, e su un telefono un
        /// messaggio oltre quello e' piu' probabilmente un bug del server che un
        /// dato da leggere, quindi si segnala invece di allocarlo.
        /// </summary>
        public const int MaxMapMessageBytes = 16 << 20;

        private readonly NoiseTransport _transport;
        private readonly NoiseFrameStream _frames;
        private readonly PushbackStream _stream;
        private readonly ILogSink _log;
        private Http2Connection _http2;
        private bool _broken;

        public NoiseControlConnection(NoiseTransport transport, ILogSink log)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _transport = transport;
            _log = log ?? NullLogSink.Instance;
            _frames = new NoiseFrameStream(transport, 0);
            _stream = new PushbackStream(_frames);
        }

        public bool IsBroken
        {
            get { return _broken || _transport == null; }
        }

        /// <summary>
        /// Il client HTTP/2, creato alla prima richiesta: e' il momento in cui si
        /// legge l'eventuale early payload, perche' prima dell'handshake quei byte
        /// non possono ancora essere arrivati.
        /// </summary>
        private Http2Connection Http2()
        {
            if (_http2 == null)
            {
                string json;
                if (EarlyPayload.TryConsume(_stream, _log, out json))
                    _log.Log("canale di controllo: early payload letto (" + (json == null ? 0 : json.Length) + " byte)");

                _http2 = new Http2Connection(_stream, _log);
            }
            return _http2;
        }

        public ControlResponse Post(string path, string jsonBody, List<KeyValuePair<string, string>> headers)
        {
            byte[] body = jsonBody == null ? new byte[0] : Ascii.GetBytes(jsonBody);
            _log.Log("http: POST " + path + " (" + body.Length + " byte, "
                     + (headers == null ? 0 : headers.Count) + " header aggiuntivi)");

            Http2Response response;
            try
            {
                response = Http2().Post(path, body, headers);
            }
            catch (IOException ex)
            {
                _log.Log("http: " + path + " interrotta (" + ex.Message + ")");
                throw new ControlStreamException(
                    "richiesta " + path + " interrotta: il control server ha chiuso la connessione ("
                    + ex.Message + ")", ex);
            }

            string text;
            try
            {
                text = Http2Connection.ReadBody(response.Body, MaxBodyBytes);
            }
            catch (Http2Exception ex)
            {
                _log.Log("http: " + path + " -> " + response.StatusCode + ", corpo non leggibile: " + ex.Message);
                throw new ControlStreamException("corpo della risposta di " + path + " non leggibile: "
                    + ex.Message, ex);
            }

            _log.Log("http: " + path + " -> " + response.StatusCode + ", "
                     + (text == null ? 0 : text.Length) + " byte");
            if (response.StatusCode != 200)
            {
                // Il corpo di un errore e' la spiegazione del server: senza, un 400
                // e' un numero e basta.
                _log.Log("http: " + path + " corpo dell'errore = " + Short(text));
            }

            ControlResponse result = new ControlResponse();
            result.StatusCode = response.StatusCode;
            result.Body = text;
            return result;
        }

        /// <summary>
        /// Apre lo stream della netmap e chiama onJson per ogni MapResponse, finche'
        /// lo stream non si chiude o il watchdog non scatta.
        /// </summary>
        public void PostStream(
            string path, string jsonBody, List<KeyValuePair<string, string>> headers,
            Action<string> onJson, int watchdogSeconds)
        {
            byte[] body = jsonBody == null ? new byte[0] : Ascii.GetBytes(jsonBody);
            _log.Log("map: apro lo stream su " + path + " (watchdog " + watchdogSeconds
                     + "s, " + body.Length + " byte di richiesta)");

            Http2Response response = Http2().Post(path, body, headers);
            _log.Log("map: risposta http " + response.StatusCode + ", content-type="
                     + (response.ContentType ?? "assente"));

            if (response.StatusCode != 200)
            {
                string error = Http2Connection.ReadBody(response.Body, MaxBodyBytes);
                _log.Log("map: rifiutata, corpo = " + Short(error));
                throw new ControlStreamException("map rifiutata: http " + response.StatusCode
                    + " " + Short(error));
            }

            Stream stream = response.Body;
            int messages = 0;

            using (Watchdog watchdog = new Watchdog(this, watchdogSeconds))
            {
                while (true)
                {
                    byte[] size = new byte[4];
                    if (!ReadExactly(stream, size, 4))
                    {
                        _log.Log("map: lo stream si e' chiuso dopo " + messages + " messaggi");
                        break;
                    }

                    // Little-endian: e' il byte order che sceglie il control server.
                    long length = (uint)(size[0] | (size[1] << 8) | (size[2] << 16) | (size[3] << 24));
                    if (length > MaxMapMessageBytes)
                    {
                        _log.Log("map: messaggio di " + length + " byte, oltre il tetto di "
                                 + MaxMapMessageBytes);
                        throw new ControlStreamException("messaggio della netmap di " + length
                            + " byte: oltre il massimo accettato di " + MaxMapMessageBytes);
                    }

                    byte[] message = new byte[(int)length];
                    if (!ReadExactly(stream, message, message.Length))
                        throw new ControlStreamException("messaggio della netmap troncato: attesi "
                            + length + " byte");

                    watchdog.KeepAlive();
                    messages++;

                    onJson(Latin1(message));
                }
            }

            _log.Log("stream della netmap terminato (" + messages + " messaggi)");
        }

        /// <summary>Legge esattamente count byte; false se lo stream finisce prima di iniziare.</summary>
        private bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    if (read == 0) return false;
                    _log.Log("map: lettura interrotta dopo " + read + " di " + count + " byte");
                    return false;
                }
                read += n;
            }
            return true;
        }

        /// <summary>Chiude la connessione quando lo stream tace troppo a lungo.</summary>
        private sealed class Watchdog : IDisposable
        {
            private readonly NoiseControlConnection _owner;
            private readonly Timer _timer;
            private readonly int _timeoutMs;
            private DateTime _lastAliveUtc = DateTime.UtcNow;
            private bool _fired;

            public Watchdog(NoiseControlConnection owner, int timeoutSeconds)
            {
                _owner = owner;
                _timeoutMs = timeoutSeconds * 1000;
                _timer = new Timer(Check, null, _timeoutMs, _timeoutMs);
            }

            public void KeepAlive()
            {
                _lastAliveUtc = DateTime.UtcNow;
            }

            private void Check(object state)
            {
                if (_fired) return;
                if ((DateTime.UtcNow - _lastAliveUtc).TotalMilliseconds < _timeoutMs) return;

                _fired = true;
                _owner._log.Log("nessun dato dal control server per " + (_timeoutMs / 1000)
                                + "s: chiudo lo stream");
                try { _owner.Dispose(); }
                catch (Exception) { }
            }

            public void Dispose()
            {
                _timer.Dispose();
            }
        }

        /// <summary>I byte sono latin-1 (JSON ASCII): non si passa per la codifica di sistema.</summary>
        private static string Latin1(byte[] bytes)
        {
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        private static string Short(string text)
        {
            if (text == null) return "";
            string trimmed = text.Trim();
            return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "...";
        }

        public void Dispose()
        {
            _log.Log("canale di controllo: chiusura");
            _broken = true;
            if (_http2 != null)
            {
                try { _http2.Dispose(); }
                catch (Exception ex) { _log.Log("canale di controllo: errore chiudendo http2: " + ex.Message); }
                _http2 = null;
            }
            try { _transport.Dispose(); }
            catch (Exception ex) { _log.Log("canale di controllo: errore in chiusura: " + ex.Message); }
        }
    }

    /// <summary>Errore nello stream della netmap o nel canale di controllo.</summary>
    public class ControlStreamException : Exception
    {
        public ControlStreamException(string message) : base(message) { }

        public ControlStreamException(string message, Exception inner) : base(message, inner) { }
    }
}
