using System;
using System.IO;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// Framing del trasporto controlbase sopra una sessione Noise stabilita.
    ///
    /// Formato di un frame, da conn.go (encryptLocked/decryptOneLocked):
    ///   1 byte  tipo, sempre 0x04 (msgTypeRecord)
    ///   2 byte  lunghezza del ciphertext, big-endian = plaintext + 16
    ///   N byte  ciphertext AEAD
    ///
    /// Il frame completo non puo' superare 4096 byte, quindi il plaintext massimo in
    /// un frame e' 4077. Un messaggio piu' lungo viene spezzato su piu' frame, come
    /// fa Conn.Write: il limite non deve diventare un errore per chi scrive un
    /// corpo grande.
    /// </summary>
    public sealed class NoiseTransport : IDisposable
    {
        private readonly Stream _stream;
        private readonly NoiseSession _session;
        private bool _disposed;

        public NoiseTransport(Stream stream, NoiseSession session)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (session == null) throw new ArgumentNullException("session");

            _stream = stream;
            _session = session;
        }

        public MachineKey PeerKey
        {
            get { return _session.PeerKey; }
        }

        public byte[] HandshakeHash
        {
            get { return _session.HandshakeHash; }
        }

        public ushort ProtocolVersion
        {
            get { return _session.ProtocolVersion; }
        }

        /// <summary>
        /// Scrive un solo frame. Rifiuta un plaintext piu' grande del massimo invece
        /// di spezzarlo in silenzio: chi vuole spezzare lo dice, con <see cref="Write"/>.
        /// </summary>
        public void WriteFrame(byte[] plaintext)
        {
            EnsureUsable();
            if (plaintext == null) throw new ArgumentNullException("plaintext");
            if (plaintext.Length > ProtocolConstants.MaxPlaintextSize)
                throw new ArgumentException(
                    "plaintext di " + plaintext.Length + " byte: il massimo per un frame e' "
                    + ProtocolConstants.MaxPlaintextSize + " (usare Write per spezzare)", "plaintext");

            byte[] ciphertext = _session.Tx.Seal(plaintext);
            byte[] frame = new byte[ProtocolConstants.HeaderLen + ciphertext.Length];
            frame[0] = ProtocolConstants.MsgTypeRecord;
            frame[1] = (byte)(ciphertext.Length >> 8);
            frame[2] = (byte)ciphertext.Length;
            Array.Copy(ciphertext, 0, frame, ProtocolConstants.HeaderLen, ciphertext.Length);

            // Flush a ogni frame: sulla catena del telefono il flush di chi scrive
            // (Http2Connection) passa da NoiseFrameStream e da PushbackStream, e se
            // non arriva fino al socket i byte restano nel buffer dello stream di
            // output di WinRT. Il costo e' una chiamata per frame.
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
        }

        /// <summary>Spinge fuori i byte scritti, fino allo stream sottostante.</summary>
        public void Flush()
        {
            if (_disposed) return;
            _stream.Flush();
        }

        /// <summary>Spezza il messaggio su piu' frame, come Conn.Write del sorgente.</summary>
        public void Write(byte[] data)
        {
            EnsureUsable();
            if (data == null) throw new ArgumentNullException("data");

            // Come Conn.Write: nessun frame per un messaggio vuoto.
            int offset = 0;
            while (offset < data.Length)
            {
                int count = Math.Min(ProtocolConstants.MaxPlaintextSize, data.Length - offset);
                byte[] chunk = new byte[count];
                Array.Copy(data, offset, chunk, 0, count);
                WriteFrame(chunk);
                offset += count;
            }
        }

        /// <summary>Legge un frame e restituisce il plaintext (vuoto se il frame era vuoto).</summary>
        public byte[] ReadFrame()
        {
            EnsureUsable();

            byte[] header = NoiseHandshake.ReadExact(_stream, ProtocolConstants.HeaderLen);
            if (header[0] != ProtocolConstants.MsgTypeRecord)
                throw new NoiseHandshakeException(
                    "tipo di frame inatteso " + header[0] + ", atteso " + ProtocolConstants.MsgTypeRecord);

            int length = (header[1] << 8) | header[2];
            int total = ProtocolConstants.HeaderLen + length;
            if (total > ProtocolConstants.MaxMessageSize)
                throw new NoiseHandshakeException(
                    "frame di " + total + " byte: oltre il massimo di " + ProtocolConstants.MaxMessageSize);

            byte[] ciphertext = NoiseHandshake.ReadExact(_stream, length);
            return _session.Rx.Open(ciphertext);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Dispose, non Close: il profilo WP8.1 non ha Stream.Close.
                _stream.Dispose();
            }
            catch (IOException)
            {
            }
        }

        private void EnsureUsable()
        {
            if (_disposed) throw new ObjectDisposedException("NoiseTransport");
        }
    }
}
