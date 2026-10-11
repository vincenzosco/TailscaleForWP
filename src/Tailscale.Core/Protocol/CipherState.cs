using System;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// Uno stato di cifratura del trasporto Noise: una chiave e un contatore.
    ///
    /// Formato del nonce, da conn.go (tipo nonce): i primi 4 byte sono zero e gli
    /// ultimi 8 sono il contatore a 64 bit big-endian. Il nonce 0 e' quindi tutto
    /// zero, ed e' quello usato dall'handshake.
    /// </summary>
    public sealed class CipherState
    {
        private readonly ChaCha20Poly1305 _cipher;
        private readonly byte[] _nonce = new byte[ChaCha20Poly1305.NonceSize];
        private ulong _counter;

        public CipherState(byte[] key)
        {
            _cipher = new ChaCha20Poly1305(key);
        }

        public ulong Counter
        {
            get { return _counter; }
        }

        public byte[] Seal(byte[] plaintext)
        {
            if (_counter == ulong.MaxValue)
                throw new InvalidOperationException("nonce esauriti per questa chiave");
            byte[] output = _cipher.Seal(_nonce, null, plaintext);
            Increment();
            return output;
        }

        public byte[] Open(byte[] ciphertext)
        {
            if (_counter == ulong.MaxValue)
                throw new InvalidOperationException("nonce esauriti per questa chiave");
            byte[] output = _cipher.Open(_nonce, null, ciphertext);
            Increment();
            return output;
        }

        private void Increment()
        {
            _counter++;
            // Contatore big-endian negli ultimi 8 byte: indici 4..11.
            for (int i = 0; i < 8; i++)
                _nonce[4 + i] = (byte)(_counter >> (8 * (7 - i)));
        }
    }
}
