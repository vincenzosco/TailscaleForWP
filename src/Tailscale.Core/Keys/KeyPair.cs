using System;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Keys
{
    /// <summary>
    /// Una coppia di chiavi Curve25519. I byte sono esposti perche' servono al
    /// protocollo; non vengono mai scritti nei log (vedi le regole in
    /// docs/protocol-notes.md).
    /// </summary>
    public sealed class KeyPair
    {
        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;

        private KeyPair(byte[] privateKey, byte[] publicKey)
        {
            _privateKey = privateKey;
            _publicKey = publicKey;
        }

        public byte[] PrivateKey { get { return (byte[])_privateKey.Clone(); } }
        public byte[] PublicKey { get { return (byte[])_publicKey.Clone(); } }

        /// <summary>Deriva la pubblica da una privata esistente.</summary>
        public static KeyPair FromPrivateKey(byte[] privateKey)
        {
            if (privateKey == null) throw new ArgumentNullException("privateKey");
            if (privateKey.Length != 32)
                throw new ArgumentException("una chiave privata ha 32 byte, ricevuti " + privateKey.Length, "privateKey");

            return new KeyPair((byte[])privateKey.Clone(), X25519.PublicFromPrivate(privateKey));
        }

        /// <summary>Genera una coppia nuova dalla sorgente casuale fornita.</summary>
        public static KeyPair Generate(IRandomSource random)
        {
            if (random == null) throw new ArgumentNullException("random");

            byte[] privateKey = new byte[32];
            random.NextBytes(privateKey);
            return FromPrivateKey(privateKey);
        }
    }
}
