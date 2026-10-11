using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Tailscale.Core.Keys;
using Tailscale.Core.Net;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace Tailscale.Services
{
    /// <summary>
    /// Le tre cuciture che il motore lascia aperte perche' il profilo WP8.1 non ha
    /// le stesse API del desktop: numeri casuali, socket e HTTP.
    ///
    /// Stanno tutte qui e non sparse perche' cambiano insieme: sono l'unico punto
    /// del progetto che tocca WinRT, e il codice condiviso con i test resta senza
    /// direttive di compilazione condizionale.
    ///
    /// Non hanno test automatici: le API che usano esistono solo sul telefono. La
    /// prova e' il build per x86/ARM piu' la verifica sull'emulatore.
    /// </summary>
    public sealed class PhoneRandomSource : IRandomSource
    {
        public void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length == 0) return;

            // CryptographicBuffer e' il generatore del sistema: la stessa fonte che
            // usa il resto della piattaforma per le chiavi.
            //
            // CopyToByteArray ha la firma (IBuffer, out byte[]): assegna un array NUOVO
            // al parametro. Scrivere `out buffer` riassegnerebbe solo la variabile
            // locale e l'array del chiamante resterebbe quello con cui e' stato creato,
            // cioe' tutti zeri: ogni avvio produrrebbe la stessa identita'.
            byte[] generated;
            CryptographicBuffer.CopyToByteArray(
                CryptographicBuffer.GenerateRandom((uint)buffer.Length), out generated);
            if (generated == null || generated.Length != buffer.Length)
                throw new InvalidOperationException("il generatore del sistema ha restituito "
                    + (generated == null ? 0 : generated.Length) + " byte invece di " + buffer.Length);

            Array.Copy(generated, buffer, buffer.Length);
        }
    }

    /// <summary>
    /// Connettore TCP/TLS su `StreamSocket`.
    ///
    /// Le API di rete di WinRT sono asincrone e il motore no: si aspetta il
    /// risultato sul thread di lavoro del client. Il timeout e' esplicito, perche'
    /// un dial verso un host che non risponde non deve durare quanto vuole il
    /// sistema operativo.
    /// </summary>
    public sealed class PhoneStreamConnector : IStreamConnector
    {
        public Stream Connect(string host, int port, bool useTls, int timeoutMs)
        {
            StreamSocket socket = new StreamSocket();
            try
            {
                socket.Control.NoDelay = true;

                // La protezione si sceglie nella chiamata di connessione: il control
                // server risponde in chiaro sulla 80 e in TLS sulla 443, ed e' lo
                // stesso handshake ts2021 nei due casi.
                Task connect = useTls
                    ? socket.ConnectAsync(new HostName(host), port.ToString(), SocketProtectionLevel.Tls12).AsTask()
                    : socket.ConnectAsync(new HostName(host), port.ToString()).AsTask();
                if (!connect.Wait(timeoutMs))
                    throw new IOException("timeout di connessione verso " + host + ":" + port + " dopo "
                                          + timeoutMs + " ms");
            }
            catch (Exception ex)
            {
                socket.Dispose();
                throw new IOException("connessione a " + host + ":" + port + " fallita: " + ex.Message, ex);
            }

            return new SocketStream(socket);
        }
    }

    /// <summary>
    /// Uno `StreamSocket` visto come un solo stream duplex, come lo vede il motore
    /// (che ci scrive la richiesta di upgrade e ci legge la risposta).
    /// </summary>
    internal sealed class SocketStream : Stream
    {
        private readonly StreamSocket _socket;
        private readonly Stream _read;
        private readonly Stream _write;

        public SocketStream(StreamSocket socket)
        {
            _socket = socket;
            _read = socket.InputStream.AsStreamForRead();
            _write = socket.OutputStream.AsStreamForWrite();
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { _write.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _read.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _write.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _read.Dispose(); }
                catch (Exception) { }
                try { _write.Dispose(); }
                catch (Exception) { }
                _socket.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// GET HTTP per il telefono, con `Windows.Web.Http` (l'unica pila HTTP
    /// garantita su WP8.1 WinRT).
    ///
    /// Serve per la chiave del server di controllo. Una risposta diversa da 200 e'
    /// un errore con il suo status code, non un corpo da interpretare: e' la stessa
    /// regola dell'adattatore desktop, cosi' il chiamante non deve sapere su quale
    /// piattaforma sta girando.
    /// </summary>
    public sealed class PhoneHttpFetcher : IHttpFetcher
    {
        public string Get(string url, int timeoutMs)
        {
            if (url == null) throw new ArgumentNullException("url");

            using (HttpClient client = new HttpClient())
            using (CancellationTokenSource cancel = new CancellationTokenSource(timeoutMs))
            {
                // Windows.Web.Http non ha una proprieta' Timeout: il limite si da'
                // con un token, altrimenti un server che non risponde lascia il fetch
                // appeso per sempre.
                HttpResponseMessage response;
                try
                {
                    response = client.GetAsync(new Uri(url)).AsTask(cancel.Token).Result;
                }
                catch (Exception ex)
                {
                    // Rete assente, DNS, proxy, TLS: qui non si distingue, ma il
                    // messaggio dice l'URL e il motivo.
                    throw new HttpFetchException("GET " + url + " fallita: " + ex.Message, 0, ex);
                }

                string body = response.Content.ReadAsStringAsync().AsTask(cancel.Token).Result;
                int status = (int)response.StatusCode;
                if (status != 200)
                    throw new HttpFetchException("risposta " + status + " da " + url, status, null);
                return body;
            }
        }
    }
}
