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
    /// Il canale reale verso il control server: HTTP/1.1 sopra i frame Noise.
    ///
    /// Il watchdog e' quello del client Go: se non arriva nulla per due minuti (su
    /// uno stream che altrimenti resterebbe aperto per giorni) la connessione viene
    /// chiusa, cosi' il chiamante la rifa'. Senza di esso un server che smette di
    /// rispondere lascerebbe la lettura bloccata per sempre e l'app "connessa" ma
    /// morta.
    /// </summary>
    public sealed class NoiseControlConnection : IControlConnection
    {
        private readonly NoiseTransport _transport;
        private readonly NoiseFrameStream _stream;
        private readonly ILogSink _log;
        private bool _broken;

        public NoiseControlConnection(NoiseTransport transport, ILogSink log)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _transport = transport;
            _log = log ?? NullLogSink.Instance;
            _stream = new NoiseFrameStream(transport, 0);
        }

        public bool IsBroken
        {
            get { return _broken || _transport == null; }
        }

        public ControlResponse Post(string path, string jsonBody, List<KeyValuePair<string, string>> headers)
        {
            _log.Log("http: POST " + path + " (" + (jsonBody == null ? 0 : jsonBody.Length)
                     + " byte, " + (headers == null ? 0 : headers.Count) + " header)");
            HttpOverNoise.WriteRequest(_stream, "POST", path, "controlplane.tailscale.com", jsonBody, headers);

            HttpBodyResponse response;
            try
            {
                response = HttpOverNoise.ReadResponse(_stream);
                _log.Log("http: " + path + " -> " + response.StatusCode + " " + response.ReasonPhrase
                         + ", " + (response.Body == null ? 0 : response.Body.Length) + " byte");
            }
            catch (IOException ex)
            {
                _log.Log("http: " + path + " chiusa senza risposta (" + ex.Message + ")");
                // Il control server parla HTTP/2 dentro Noise (il client Go forza
                // `SetUnencryptedHTTP2`), quindi una richiesta HTTP/1.1 viene chiusa
                // senza risposta. L'errore grezzo sarebbe "stream chiuso dopo 0 di 3
                // byte": qui si dice cosa manca davvero.
                throw new ControlStreamException(
                    "il server ha chiuso la richiesta " + path + " senza risposta: il canale di "
                    + "controllo richiede HTTP/2 dentro il trasporto Noise, non HTTP/1.1 ("
                    + ex.Message + ")", ex);
            }

            ControlResponse result = new ControlResponse();
            result.StatusCode = response.StatusCode;
            result.Body = response.Body;
            return result;
        }

        /// <summary>
        /// Apre lo stream della netmap e chiama onJson per ogni MapResponse, finche'
        /// lo stream non si chiude o il watchdog non scatta.
        ///
        /// Il corpo della risposta sono piu' valori JSON uno dopo l'altro (con
        /// Transfer-Encoding: chunked), quindi non si puo' leggere "il corpo" e
        /// interpretarlo: si accumulano byte e si emette ogni valore completo.
        /// </summary>
        public void PostStream(
            string path, string jsonBody, List<KeyValuePair<string, string>> headers,
            Action<string> onJson, int watchdogSeconds)
        {
            _log.Log("map: apro lo stream su " + path + " (watchdog " + watchdogSeconds
                     + "s, " + (jsonBody == null ? 0 : jsonBody.Length) + " byte)");
            HttpOverNoise.WriteRequest(_stream, "POST", path, "controlplane.tailscale.com", jsonBody, headers);

            HttpResponseHead head = RawHttp.ReadResponseHead(_stream);
            _log.Log("map: risposta \"" + head.StatusLine + "\", "
                     + head.HeaderCount + " header, transfer-encoding="
                     + (head.Get("Transfer-Encoding") ?? "assente")
                     + ", content-length="
                     + (head.Get("Content-Length") ?? "assente"));

            if (head.StatusCode != 200)
            {
                string body = HttpOverNoise.ReadResponse(_stream).Body;
                _log.Log("map: rifiutata, corpo = " + Trim(body));
                throw new ControlStreamException(
                    "map rifiutata: http " + head.StatusCode + " " + Trim(body));
            }

            using (Watchdog watchdog = new Watchdog(this, watchdogSeconds))
            {
                JsonStreamSplitter splitter = new JsonStreamSplitter();
                byte[] chunk = new byte[4096];

                while (true)
                {
                    int read = _stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;
                    watchdog.KeepAlive();

                    for (int i = 0; i < read; i++)
                    {
                        string complete = splitter.Feed((char)chunk[i]);
                        if (complete != null) onJson(complete);
                    }
                }
            }

            _log.Log("stream della netmap terminato");
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

        private static string Trim(string text)
        {
            if (text == null) return "";
            string trimmed = text.Trim();
            return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200);
        }

        public void Dispose()
        {
            _log.Log("canale di controllo: chiusura");
            _broken = true;
            try { _transport.Dispose(); }
            catch (Exception ex) { _log.Log("canale di controllo: errore in chiusura: " + ex.Message); }
        }
    }

    /// <summary>Errore nello stream della netmap.</summary>
    public class ControlStreamException : Exception
    {
        public ControlStreamException(string message) : base(message) { }

        public ControlStreamException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Divide un flusso di byte in valori JSON concatenati.
    ///
    /// Non si puo' usare ParsePrefix qui: il confine fra un valore e il successivo
    /// non e' noto finche' non arriva il byte che chiude il primo, e un tentativo su
    /// un testo troncato non si distingue da un errore di sintassi. Si conta quindi
    /// la profondita' di graffe e quadre fuori dalle stringhe, che e' esatto per
    /// oggetti e array e non richiede di rileggere nulla.
    /// </summary>
    public sealed class JsonStreamSplitter
    {
        private readonly StringBuilder _current = new StringBuilder();
        private int _depth;
        private bool _inString;
        private bool _escaped;

        /// <summary>Consuma un carattere e restituisce il valore JSON completato, se c'e'.</summary>
        public string Feed(char c)
        {
            if (_depth == 0)
            {
                // Fra un valore e il successivo: si ignora tutto tranne l'inizio di un
                // oggetto o array (i MapResponse sono sempre oggetti).
                if (c == '{' || c == '[')
                {
                    _current.Length = 0;
                    _depth = 1;
                    _inString = false;
                    _escaped = false;
                    _current.Append(c);
                }
                return null;
            }

            _current.Append(c);

            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (c == '\\') _escaped = true;
                else if (c == '"') _inString = false;
                return null;
            }

            if (c == '"') { _inString = true; return null; }
            if (c == '{' || c == '[') { _depth++; return null; }
            if (c == '}' || c == ']')
            {
                _depth--;
                if (_depth == 0)
                {
                    string complete = _current.ToString();
                    _current.Length = 0;
                    return complete;
                }
            }
            return null;
        }

        /// <summary>Vero se e' rimasto un valore incompleto: la risposta era troncata.</summary>
        public bool HasPartialValue
        {
            get { return _depth != 0; }
        }
    }
}
