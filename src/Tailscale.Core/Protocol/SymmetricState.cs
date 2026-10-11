using System;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// Lo stato simmetrico di Noise IK, trascritto da
    /// refs/tailscale/control/controlbase/handshake.go (tipo symmetricState).
    ///
    /// Differenza sostanziale rispetto al Noise "standard": qui il nonce dell'AEAD
    /// di handshake e' sempre zero (singleUseCHP) e il ciphertext viene mischiato
    /// in h subito dopo essere stato prodotto, non prima.
    /// </summary>
    public sealed class SymmetricState
    {
        private readonly byte[] _handshakeHash = new byte[32];
        private readonly byte[] _chainingKey = new byte[32];
        private bool _finished;

        public byte[] HandshakeHash
        {
            get { return (byte[])_handshakeHash.Clone(); }
        }

        public byte[] ChainingKey
        {
            get { return (byte[])_chainingKey.Clone(); }
        }

        /// <summary>h = ck = BLAKE2s(protocolName).</summary>
        public void Initialize()
        {
            CheckFinished();
            byte[] h = Blake2s.Hash(Tailscale.Core.Ascii.GetBytes(ProtocolConstants.NoiseInstanceName), 32);
            Array.Copy(h, _handshakeHash, 32);
            Array.Copy(h, _chainingKey, 32);
        }

        /// <summary>h = BLAKE2s(h || data).</summary>
        public void MixHash(byte[] data)
        {
            CheckFinished();
            if (data == null) throw new ArgumentNullException("data");

            byte[] input = new byte[_handshakeHash.Length + data.Length];
            Array.Copy(_handshakeHash, 0, input, 0, _handshakeHash.Length);
            Array.Copy(data, 0, input, _handshakeHash.Length, data.Length);

            byte[] output = Blake2s.Hash(input, 32);
            Array.Copy(output, _handshakeHash, 32);
        }

        /// <summary>
        /// MixKey(X25519(privateKey, publicKey)): aggiorna ck e restituisce la
        /// chiave monouso per l'AEAD di handshake.
        ///
        /// ck' e k sono i primi due blocchi dell'HKDF sullo X25519, con salt = ck.
        /// </summary>
        public byte[] MixDH(byte[] privateKey, byte[] publicKey)
        {
            CheckFinished();

            byte[] keyData = X25519.ScalarMult(privateKey, publicKey);
            byte[] derived = HkdfBlake2s.Derive(keyData, _chainingKey, null, 64);

            Array.Copy(derived, 0, _chainingKey, 0, 32);

            byte[] key = new byte[32];
            Array.Copy(derived, 32, key, 0, 32);
            return key;
        }

        /// <summary>Cifra con nonce zero e AAD = h, poi mischia il ciphertext in h.</summary>
        public byte[] EncryptAndHash(byte[] key, byte[] plaintext)
        {
            CheckFinished();
            if (plaintext == null) throw new ArgumentNullException("plaintext");

            byte[] zeroNonce = new byte[ChaCha20Poly1305.NonceSize];
            ChaCha20Poly1305 cipher = new ChaCha20Poly1305(key);
            byte[] ciphertext = cipher.Seal(zeroNonce, HandshakeHash, plaintext);

            MixHash(ciphertext);
            return ciphertext;
        }

        /// <summary>Decifra con nonce zero e AAD = h, poi mischia il ciphertext in h.</summary>
        public byte[] DecryptAndHash(byte[] key, byte[] ciphertext)
        {
            CheckFinished();
            if (ciphertext == null) throw new ArgumentNullException("ciphertext");

            byte[] zeroNonce = new byte[ChaCha20Poly1305.NonceSize];
            ChaCha20Poly1305 cipher = new ChaCha20Poly1305(key);
            byte[] plaintext = cipher.Open(zeroNonce, HandshakeHash, ciphertext);

            MixHash(ciphertext);
            return plaintext;
        }

        /// <summary>Deriva le due chiavi di trasporto e chiude lo stato.</summary>
        public void Split(out byte[] txKey, out byte[] rxKey)
        {
            CheckFinished();
            _finished = true;

            byte[] derived = HkdfBlake2s.Derive(new byte[0], _chainingKey, null, 64);
            txKey = new byte[32];
            rxKey = new byte[32];
            Array.Copy(derived, 0, txKey, 0, 32);
            Array.Copy(derived, 32, rxKey, 0, 32);
        }

        private void CheckFinished()
        {
            if (_finished)
                throw new InvalidOperationException("stato simmetrico gia' chiuso da Split");
        }


    }
}
