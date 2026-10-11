using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Tailscale.Core.Net;

namespace Tailscale.Cli
{
    /// <summary>
    /// Connettore TCP/TLS per il desktop: e' quello che il core non puo' contenere
    /// perche' il profilo WP8.1 non ha System.Net.Sockets.
    /// </summary>
    public sealed class TcpStreamConnector : IStreamConnector
    {
        public Stream Connect(string host, int port, bool useTls, int timeoutMs)
        {
            TcpClient client = new TcpClient();
            try
            {
                // BeginConnect + WaitOne: senza un timeout esplicito un dial verso un
                // host che non risponde blocca finche' il sistema non rinuncia.
                IAsyncResult pending = client.BeginConnect(host, port, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw new IOException("timeout di connessione verso " + host + ":" + port);
                client.EndConnect(pending);
            }
            catch (Exception)
            {
                client.Close();
                throw;
            }

            client.NoDelay = true;
            Stream stream = client.GetStream();
            if (!useTls) return stream;

            SslStream ssl = new SslStream(stream, false);
            try
            {
                // TLS 1.2 esplicito: il valore predefinito di .NET 4.5 e' TLS 1.0,
                // che il control server rifiuta. Sul telefono la versione la sceglie
                // il sistema, quindi questo sta qui e non nel core.
                ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
            }
            catch (Exception ex)
            {
                ssl.Dispose();
                throw new IOException("TLS verso " + host + " fallito: " + ex.Message, ex);
            }
            return ssl;
        }
    }
}
