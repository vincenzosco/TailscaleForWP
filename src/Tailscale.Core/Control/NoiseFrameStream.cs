using System;
using System.IO;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Control
{
    /// <summary>
    /// Espone i frame del trasporto Noise come uno stream di byte.
    ///
    /// Il control plane parla HTTP/2 sopra Noise: qui sotto c'e' quindi un client
    /// HTTP/2 (Tailscale.Core.Protocol.Http2Connection), che legge e scrive su
    /// questo stream.
    ///
    /// In lettura i frame vengono consumati come richiesto dai lettori di byte:
    /// senza questo buffer ogni lettura di pochi byte costerebbe un frame intero.
    /// </summary>
    public sealed class NoiseFrameStream : Stream
    {
        private readonly NoiseTransport _transport;
        private byte[] _pending = new byte[0];
        private int _position;

        // Nessuna scadenza qui: una lettura bloccata su questo stream non si puo'
        // interrompere da dentro, e un tempo limite che nessuno usa fa credere che
        // una scadenza ci sia. La scadenza vive dove si puo' chiudere il canale:
        // NoiseControlConnection.
        public NoiseFrameStream(NoiseTransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _transport = transport;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

        /// <summary>
        /// Inoltra il flush al trasporto: i frame HTTP/2 vengono scritti uno per
        /// volta e chi li scrive chiede il flush credendo che i byte escano da qui.
        /// Un Flush vuoto rendeva quella richiesta decorativa.
        /// </summary>
        public override void Flush() { _transport.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            while (_position >= _pending.Length)
            {
                _pending = _transport.ReadFrame();
                _position = 0;
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
}
