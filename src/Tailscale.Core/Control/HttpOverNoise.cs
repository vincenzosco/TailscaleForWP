using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tailscale.Core.Net;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Control
{
    /// <summary>
    /// Espone i frame del trasporto Noise come uno stream di byte.
    ///
    /// Serve perche' il control plane parla HTTP sopra Noise: il client Go usa
    /// http.Request.Write sulla connessione Noise e il server risponde con una
    /// risposta HTTP, spesso con Transfer-Encoding: chunked (la netmap e' uno
    /// stream senza fine, quindi la lunghezza non e' nota in anticipo).
    ///
    /// In lettura i frame vengono consumati come richiesto dai lettori di byte:
    /// RawHttp.ReadLine legge un byte alla volta, e senza questo buffer ogni byte
    /// costerebbe un frame.
    /// </summary>
    public sealed class NoiseFrameStream : Stream
    {
        private readonly NoiseTransport _transport;
        private byte[] _pending = new byte[0];
        private int _position;
        private readonly int _readTimeoutMs;
        private DateTime _lastActivityUtc;

        public NoiseFrameStream(NoiseTransport transport, int readTimeoutMs)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _transport = transport;
            _readTimeoutMs = readTimeoutMs;
            _lastActivityUtc = DateTime.UtcNow;
        }

        /// <summary>Ultimo istante in cui e' arrivato un frame: lo usa il watchdog.</summary>
        public DateTime LastActivityUtc { get { return _lastActivityUtc; } }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            while (_position >= _pending.Length)
            {
                _pending = _transport.ReadFrame();
                _position = 0;
                _lastActivityUtc = DateTime.UtcNow;
            }

            int available = Math.Min(count, _pending.Length - _position);
            Array.Copy(_pending, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            byte[] chunk = new byte[count];
            Array.Copy(buffer, offset, chunk, 0, count);
            _transport.Write(chunk);
        }
    }

    /// <summary>Una risposta HTTP decodificata, con il corpo gia' letto.</summary>
    public sealed class HttpBodyResponse
    {
        public int StatusCode;
        public string ReasonPhrase = "";
        public string Body = "";
        public string ContentType;
    }

    /// <summary>
    /// Scrive e legge richieste HTTP/1.1 sopra uno stream (che nel caso reale e' un
    /// NoiseFrameStream).
    ///
    /// Non usa HttpWebRequest perche' non si puo' dirottare su uno stream gia'
    /// aperto; e' la stessa ragione per cui il dialer dell'upgrade e' scritto a
    /// mano. Il corpo della risposta viene letto per intero quando la lunghezza e'
    /// nota (Content-Length) e decodificato quando e' chunked.
    /// </summary>
    public static class HttpOverNoise
    {
        public const int MaxBodyBytes = 1 << 20; // lo stesso limite di decode() in direct.go

        public static void WriteRequest(
            Stream stream, string method, string path, string host, string jsonBody,
            List<KeyValuePair<string, string>> headers)
        {
            byte[] body = jsonBody == null ? new byte[0] : Ascii.GetBytes(jsonBody);

            StringBuilder sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            if (jsonBody != null)
                sb.Append("Content-Type: application/json\r\n");
            sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                    sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            sb.Append("\r\n");

            byte[] head = Ascii.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>Legge una risposta completa, decodificando il corpo.</summary>
        public static HttpBodyResponse ReadResponse(Stream stream)
        {
            HttpResponseHead head = RawHttp.ReadResponseHead(stream);

            HttpBodyResponse response = new HttpBodyResponse();
            response.StatusCode = head.StatusCode;
            response.ReasonPhrase = head.ReasonPhrase;
            response.ContentType = head.Get("Content-Type");

            string transferEncoding = head.Get("Transfer-Encoding");
            if (transferEncoding != null &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                response.Body = ReadChunkedBody(stream);
                return response;
            }

            string contentLength = head.Get("Content-Length");
            if (contentLength != null)
            {
                int length;
                if (int.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                {
                    response.Body = Ascii.GetString(ReadExact(stream, length));
                    return response;
                }
            }

            // Nessuna lunghezza: si legge fino alla chiusura (limite di sicurezza).
            response.Body = ReadToEnd(stream);
            return response;
        }

        /// <summary>Legge una sequenza di chunk (RFC 9112 §7.1).</summary>
        public static string ReadChunkedBody(Stream stream)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                string sizeLine = RawHttp.ReadLine(stream);
                if (sizeLine == null) break;

                // Un'estensione di chunk dopo ';' va ignorata.
                int semicolon = sizeLine.IndexOf(';');
                string sizeText = (semicolon < 0 ? sizeLine : sizeLine.Substring(0, semicolon)).Trim();

                int size;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
                    throw new IOException("dimensione di chunk non valida: " + sizeLine);

                if (size == 0)
                {
                    // Terminatore: eventuali trailer fino alla riga vuota.
                    while (true)
                    {
                        string trailer = RawHttp.ReadLine(stream);
                        if (trailer == null || trailer.Length == 0) break;
                    }
                    break;
                }

                sb.Append(Ascii.GetString(ReadExact(stream, size)));

                // CRLF che segue i dati del chunk.
                if (RawHttp.ReadLine(stream) == null) break;

                if (sb.Length > MaxBodyBytes)
                    throw new IOException("corpo oltre " + MaxBodyBytes + " byte");
            }
            return sb.ToString();
        }

        public static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("stream chiuso dopo " + offset + " di " + count + " byte");
                offset += read;
            }
            return buffer;
        }

        private static string ReadToEnd(Stream stream)
        {
            StringBuilder sb = new StringBuilder();
            byte[] chunk = new byte[4096];
            while (sb.Length <= MaxBodyBytes)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                sb.Append(Ascii.GetString(chunk).Substring(0, read));
            }
            return sb.ToString();
        }
    }
}
