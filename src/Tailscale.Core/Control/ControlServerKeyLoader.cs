using System;
using System.Globalization;
using Tailscale.Core.Keys;
using Tailscale.Core.Net;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Control
{
    /// <summary>Le due chiavi pubbliche del control server.</summary>
    public sealed class ControlServerKeys
    {
        /// <summary>Chiave per il trasporto Noise (ts2021): il campo `publicKey`.</summary>
        public MachineKey NoiseKey;

        /// <summary>Chiave per il vecchio crypto_box: il campo `legacyPublicKey`. Puo' essere null.</summary>
        public MachineKey LegacyKey;

        public override string ToString()
        {
            return "ts2021=" + (NoiseKey == null ? "<assente>" : NoiseKey.ToString())
                 + " legacy=" + (LegacyKey == null ? "<assente>" : LegacyKey.ToString());
        }
    }

    /// <summary>
    /// Carica le chiavi del control server.
    ///
    /// La forma della risposta viene da tailcfg.OverTLSPublicKeyResponse: i campi
    /// si chiamano `publicKey` e `legacyPublicKey` e sono stringhe nel formato
    /// `mkey:...`. Il campo legacy puo' essere assente o vuoto: un server
    /// abbastanza nuovo non lo manda, e non e' un errore.
    ///
    /// La meccanica HTTP la fornisce chi ospita il core (IHttpFetcher), perche' il
    /// profilo WP8.1 di HttpWebRequest e' asincrono e non ha la GET sincrona.
    /// </summary>
    public static class ControlServerKeyLoader
    {
        public const int DefaultTimeoutMs = 10000;

        public static ControlServerKeys Load(
            IHttpFetcher fetcher, string baseUrl, ushort version, ILogSink log)
        {
            return Load(fetcher, baseUrl, version, log, DefaultTimeoutMs);
        }

        public static ControlServerKeys Load(
            IHttpFetcher fetcher, string baseUrl, ushort version, ILogSink log, int timeoutMs)
        {
            if (fetcher == null) throw new ArgumentNullException("fetcher");
            if (baseUrl == null) throw new ArgumentNullException("baseUrl");
            if (log == null) log = NullLogSink.Instance;

            string url = string.Format(
                CultureInfo.InvariantCulture, ProtocolConstants.ServerKeyPathFormat,
                baseUrl.TrimEnd('/'), version);

            log.Log("chiavi del server: GET " + url + " (timeout " + timeoutMs + " ms)");

            string body;
            try
            {
                body = fetcher.Get(url, timeoutMs);
            }
            catch (HttpFetchException ex)
            {
                string detail = ex.IsTransportFailure
                    ? "errore di trasporto"
                    : "http " + ex.StatusCode;
                log.Log("chiavi del server: GET fallita (" + detail + "): " + ex.Message);
                // Il messaggio del fetcher contiene gia' il contesto di rete: qui si
                // aggiunge solo l'URL che ha fallito.
                throw new ControlServerKeyException("GET " + url + " fallita: " + ex.Message, ex);
            }

            log.Log("chiavi del server: " + (body == null ? 0 : body.Length) + " byte di risposta");

            ControlServerKeys keys;
            try
            {
                keys = Parse(body);
            }
            catch (ControlServerKeyException ex)
            {
                // Un corpo inatteso si vede tutto nei log: senza, un server che
                // cambia formato sembra un errore di rete.
                log.Log("chiavi del server: risposta non interpretabile: " + ex.Message);
                log.Log("chiavi del server: corpo ricevuto = " + Preview(body));
                throw;
            }

            log.Log("chiavi del server da " + url + ": " + keys);
            return keys;
        }

        /// <summary>Un'anteprima breve e ripulita del corpo, per i log.</summary>
        private static string Preview(string body)
        {
            if (string.IsNullOrEmpty(body)) return "<vuoto>";

            char[] buffer = new char[Math.Min(200, body.Length)];
            for (int i = 0; i < buffer.Length; i++)
            {
                char c = body[i];
                buffer[i] = c >= ' ' && c <= '~' ? c : '.';
            }
            string text = new string(buffer);
            if (text.Length > 160) text = text.Substring(0, 160) + "...";
            return "\"" + text + "\" (" + body.Length + " byte totali)";
        }

        /// <summary>Interpreta il corpo JSON della risposta.</summary>
        public static ControlServerKeys Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");

            JsonValue root;
            try
            {
                root = JsonBody.Parse(json);
            }
            catch (JsonParseException ex)
            {
                throw new ControlServerKeyException("la risposta delle chiavi non e' JSON: " + ex.Message, ex);
            }

            if (!root.IsObject)
                throw new ControlServerKeyException("la risposta delle chiavi non e' un oggetto JSON");

            JsonValue publicKey = root["publicKey"];
            if (publicKey == null || !publicKey.IsString || publicKey.AsString("").Length == 0)
                throw new ControlServerKeyException(
                    "la risposta delle chiavi non contiene \"publicKey\": il control server e' troppo vecchio per ts2021");

            ControlServerKeys keys = new ControlServerKeys();
            keys.NoiseKey = MachineKey.Parse(publicKey.AsString(""));

            JsonValue legacy = root["legacyPublicKey"];
            if (legacy != null && legacy.IsString && legacy.AsString("").Length > 0)
                keys.LegacyKey = MachineKey.Parse(legacy.AsString(""));

            return keys;
        }
    }

    /// <summary>Impossibile ottenere o interpretare le chiavi del control server.</summary>
    public class ControlServerKeyException : Exception
    {
        public ControlServerKeyException(string message) : base(message) { }

        public ControlServerKeyException(string message, Exception inner) : base(message, inner) { }
    }
}
