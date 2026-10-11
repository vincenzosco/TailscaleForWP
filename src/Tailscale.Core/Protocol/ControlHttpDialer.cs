using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
using Tailscale.Core.Net;

namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// Apre la connessione di controllo: richiesta HTTP con upgrade a /ts2021,
    /// handshake Noise differito dentro l'header, e da li' in poi solo frame Noise.
    ///
    /// Il primo messaggio dell'handshake viaggia nella richiesta HTTP
    /// (`X-Tailscale-Handshake`, base64) cosi' l'upgrade e l'handshake costano un
    /// solo giro di rete: e' lo stesso motivo per cui ClientDeferred esiste nel
    /// sorgente Go.
    ///
    /// L'ordine dei tentativi e' quello del client Go: prima HTTP in chiaro,
    /// poi HTTPS sulla 443. Sulla 443 la cifratura e' doppia (TLS piu' Noise), che
    /// e' ridondante ma innocua: Noise e' comunque quello che autentica il server.
    /// </summary>
    public static class ControlHttpDialer
    {
        public const int ConnectTimeoutMs = 15000;
        public const int HandshakeTimeoutMs = 15000;

        private static readonly int[] Ports = new int[] { 80, 443 };

        public static NoiseTransport Dial(
            string hostname, KeyPair machineKey, ControlServerKeys keys, ushort version,
            IRandomSource random, IStreamConnector connector, ILogSink log)
        {
            if (hostname == null) throw new ArgumentNullException("hostname");
            if (machineKey == null) throw new ArgumentNullException("machineKey");
            if (keys == null || keys.NoiseKey == null) throw new ArgumentNullException("keys");
            if (random == null) throw new ArgumentNullException("random");
            if (connector == null) throw new ArgumentNullException("connector");
            if (log == null) log = NullLogSink.Instance;

            NoiseHandshakeStart start = NoiseHandshake.Deferred(machineKey, keys.NoiseKey, version, random);
            string handshake = Convert.ToBase64String(start.InitialMessage, 0, start.InitialMessage.Length);

            List<string> failures = new List<string>();

            foreach (int port in Ports)
            {
                bool useTls = port == 443;
                string scheme = useTls ? "https" : "http";
                Stream stream = null;

                try
                {
                    stream = connector.Connect(hostname, port, useTls, ConnectTimeoutMs);
                    RawHttp.WriteHead(stream, BuildRequestHead(hostname, handshake));

                    HttpResponseHead response = RawHttp.ReadResponseHead(stream);

                    if (response.StatusCode == 101)
                    {
                        log.Log("ts2021: upgrade accettato su " + scheme + "://" + hostname + ":" + port);
                        NoiseSession session = StartWithTimeout(start, stream);

                        log.Log("ts2021: handshake completato, peer=" + session.PeerKey
                                + ", versione=" + session.ProtocolVersion);
                        return new NoiseTransport(stream, session);
                    }

                    string contentType = response.Get("Content-Type");
                    string hint = contentType != null && contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0
                        ? " (risposta HTML: probabile proxy captive o portale Wi-Fi)"
                        : "";
                    failures.Add(scheme + "://" + hostname + ":" + port + " -> " + response.StatusLine + hint);
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add(scheme + "://" + hostname + ":" + port + " -> " + ex.Message);
                    if (stream != null)
                    {
                        try { stream.Dispose(); }
                        catch (Exception) { }
                    }
                }
            }

            throw new ControlDialException(
                "impossibile aprire la connessione di controllo con " + hostname + ": "
                + string.Join("; ", failures.ToArray()));
        }

        /// <summary>
        /// L'handshake differito va completato una sola volta: se fallisce qui non si
        /// puo' ritentare con lo stesso oggetto, quindi l'errore e' definitivo.
        /// </summary>
        private static NoiseSession StartWithTimeout(NoiseHandshakeStart start, Stream stream)
        {
            try
            {
                return start.Continuation.Complete(stream);
            }
            catch (NoiseHandshakeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ControlDialException(
                    "upgrade riuscito ma handshake Noise fallito: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// La richiesta di upgrade. Il nome dell'header e i valori vengono dalle
        /// costanti del protocollo, che il pin check tiene allineate al sorgente.
        /// </summary>
        public static string BuildRequestHead(string hostname, string handshakeBase64)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("POST ").Append(ProtocolConstants.UpgradePath).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(hostname).Append("\r\n");
            sb.Append(ProtocolConstants.UpgradeHeaderName).Append(": ")
              .Append(ProtocolConstants.UpgradeHeaderValue).Append("\r\n");
            sb.Append(ProtocolConstants.ConnectionHeaderName).Append(": ")
              .Append(ProtocolConstants.ConnectionHeaderValue).Append("\r\n");
            sb.Append(ProtocolConstants.HandshakeHeaderName).Append(": ")
              .Append(handshakeBase64).Append("\r\n");
            sb.Append("\r\n");
            return sb.ToString();
        }
    }

    /// <summary>Non e' stato possibile aprire la connessione di controllo.</summary>
    public class ControlDialException : Exception
    {
        public ControlDialException(string message) : base(message) { }
        public ControlDialException(string message, Exception inner) : base(message, inner) { }
    }
}
