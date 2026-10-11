using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Tailscale.Core.Net;

namespace Tailscale.Cli
{
    /// <summary>
    /// Fetch HTTP per il desktop: HttpWebRequest sincrona. Sul telefono la stessa
    /// interfaccia e' implementata con BeginGetResponse, perche' il profilo WP8.1
    /// non ha la versione sincrona.
    /// </summary>
    public sealed class DesktopHttpFetcher : IHttpFetcher
    {
        static DesktopHttpFetcher()
        {
            // .NET 4.5 negozia TLS 1.0 per impostazione predefinita e il control server
            // lo rifiuta ("impossibile creare un canale sicuro"): va chiesto TLS 1.2.
            // Sta qui e non nel core perche' sul telefono la negoziazione la fa il
            // sistema, non ServicePointManager.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public string Get(string url, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;

            try
            {
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse failure = ex.Response as HttpWebResponse;
                if (failure != null)
                {
                    // Leggere lo stato prima di chiudere: dopo Close la risposta non
                    // e' piu' interrogabile.
                    int status = (int)failure.StatusCode;
                    string description = failure.StatusDescription;
                    failure.Close();
                    throw new HttpFetchException(
                        "risposta " + status + " " + description, status, ex);
                }

                SocketException socket = ex.InnerException as SocketException;
                string detail = socket != null ? socket.Message : ex.Message;
                throw new HttpFetchException("richiesta fallita: " + detail + " (rete o proxy?)", 0, ex);
            }
        }
    }
}
