using System;
using System.IO;

namespace Tailscale.Core
{
    /// <summary>
    /// Stream che permette di rimettere in lettura byte gia' consumati.
    ///
    /// Serve a un caso preciso: dopo l'handshake il server puo' mandare un "early
    /// payload" di 9 byte, che sono riconoscibili dal loro magic. Per saperlo
    /// bisogna leggerli, ma se non lo sono quei 9 byte *sono* l'inizio di un frame
    /// HTTP/2 e vanno restituiti intatti: senza rimetterli, il primo frame perso
    /// significa una connessione che non si apre e un errore che non dice perche'.
    /// </summary>
    public sealed class PushbackStream : Stream
    {
        private readonly Stream _inner;
        private byte[] _pending = new byte[0];
        private int _offset;

        public PushbackStream(Stream inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
        }

        /// <summary>Stream sottostante (per chi deve leggere dal trasporto vero).</summary>
        public Stream Inner { get { return _inner; } }

        /// <summary>Rimette i byte davanti a quelli non ancora letti.</summary>
        public void Pushback(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            int remaining = _pending.Length - _offset;
            byte[] combined = new byte[bytes.Length + remaining];
            Array.Copy(bytes, 0, combined, 0, bytes.Length);
            if (remaining > 0) Array.Copy(_pending, _offset, combined, bytes.Length, remaining);
            _pending = combined;
            _offset = 0;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return _inner.CanWrite; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { _inner.Flush(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_offset < _pending.Length)
            {
                int available = Math.Min(count, _pending.Length - _offset);
                Array.Copy(_pending, _offset, buffer, offset, available);
                _offset += available;
                if (_offset == _pending.Length)
                {
                    _pending = new byte[0];
                    _offset = 0;
                }
                return available;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
