using System;

namespace Tailscale.Core.Net
{
    /// <summary>
    /// Una GET sincrona del corpo di una risposta.
    ///
    /// Motivo dell'astrazione: su WP8.1 HttpWebRequest ha solo l'API asincrona
    /// (BeginGetResponse, nessuna proprieta' Timeout e nessun GetResponse), quindi
    /// il core non puo' contenere la meccanica HTTP senza rompere la build del
    /// telefono. E' lo stesso schema di IRandomSource e IStreamConnector.
    /// </summary>
    public interface IHttpFetcher
    {
        /// <summary>Restituisce il corpo della risposta, o lancia <see cref="HttpFetchException"/>.</summary>
        string Get(string url, int timeoutMs);
    }

    /// <summary>Errore di rete o risposta HTTP non 200. StatusCode 0 = errore di trasporto.</summary>
    public class HttpFetchException : Exception
    {
        public HttpFetchException(string message) : base(message) { }

        public HttpFetchException(string message, int statusCode, Exception inner) : base(message, inner)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; private set; }

        public bool IsTransportFailure { get { return StatusCode == 0; } }
    }
}
