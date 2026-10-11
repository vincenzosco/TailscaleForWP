using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tailscale.Core.Net
{
    /// <summary>
    /// Apre uno stream TCP, con TLS opzionale: la fornisce chi ospita il core.
    ///
    /// Motivo: il profilo WP8.1 dell'app non espone System.Net.Sockets (i socket
    /// sono in Windows.Networking.Sockets), mentre il desktop usa TcpClient e
    /// SslStream. E' lo stesso vincolo di IRandomSource: nessuna API unica esiste
    /// sui due target senza una direttiva di compilazione condizionale.
    /// </summary>
    public interface IStreamConnector
    {
        Stream Connect(string host, int port, bool useTls, int timeoutMs);
    }

    /// <summary>
    /// Le poche righe di HTTP/1.1 che servono al control plane, scritte a mano su
    /// un socket.
    ///
    /// Perche' non HttpWebRequest: la connessione di controllo comincia con un
    /// `101 Switching Protocols` e da li' in poi lo stream e' un canale Noise.
    /// HttpWebRequest non espone il socket sottostante e non consegna i byte che
    /// seguono la risposta, quindi il primo frame Noise andrebbe perso (il client
    /// Go ha lo stesso problema e per questo legge da resp.Body).
    ///
    /// Il GET delle chiavi, che non fa upgrade, usa invece HttpWebRequest: li' il
    /// supporto TLS della piattaforma e' quello che vogliamo.
    /// </summary>
    public sealed class HttpResponseHead
    {
        private readonly List<string> _names = new List<string>();
        private readonly List<string> _values = new List<string>();

        public int StatusCode;
        public string ReasonPhrase = "";
        public string HttpVersion = "";

        public void Add(string name, string value)
        {
            _names.Add(name);
            _values.Add(value);
        }

        /// <summary>Valore di un header, ignorando le maiuscole. Null se assente.</summary>
        public string Get(string name)
        {
            for (int i = 0; i < _names.Count; i++)
                if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase))
                    return _values[i];
            return null;
        }

        public int HeaderCount { get { return _names.Count; } }

        public string StatusLine
        {
            get { return HttpVersion + " " + StatusCode + " " + ReasonPhrase; }
        }
    }

    public static class RawHttp
    {
        public static void WriteHead(Stream stream, string requestHead)
        {
            byte[] bytes = Ascii.GetBytes(requestHead);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Legge la riga di stato e gli header, fermandosi alla riga vuota.</summary>
        public static HttpResponseHead ReadResponseHead(Stream stream)
        {
            string statusLine = ReadLine(stream);
            if (statusLine == null || statusLine.Length == 0)
                throw new IOException("il server ha chiuso la connessione prima della risposta HTTP");

            HttpResponseHead head = new HttpResponseHead();
            int firstSpace = statusLine.IndexOf(' ');
            int secondSpace = firstSpace < 0 ? -1 : statusLine.IndexOf(' ', firstSpace + 1);
            head.HttpVersion = firstSpace < 0 ? statusLine : statusLine.Substring(0, firstSpace);

            string code = secondSpace < 0
                ? (firstSpace < 0 ? "" : statusLine.Substring(firstSpace + 1))
                : statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1);

            int statusCode;
            if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out statusCode))
                throw new IOException("riga di stato HTTP non valida: " + statusLine);
            head.StatusCode = statusCode;
            head.ReasonPhrase = secondSpace < 0 ? "" : statusLine.Substring(secondSpace + 1);

            while (true)
            {
                string line = ReadLine(stream);
                if (line == null) throw new IOException("header HTTP troncati");
                if (line.Length == 0) break;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue; // header malformato: si ignora
                head.Add(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim());
            }
            return head;
        }

        /// <summary>Una riga terminata da CRLF, o null a fine stream.</summary>
        public static string ReadLine(Stream stream)
        {
            StringBuilder sb = new StringBuilder(64);
            byte[] one = new byte[1];

            while (true)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0) return sb.Length == 0 ? null : sb.ToString();
                if (one[0] == (byte)'\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)one[0]);
            }
        }
    }
}
