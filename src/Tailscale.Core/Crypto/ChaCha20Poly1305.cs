using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// AEAD_CHACHA20_POLY1305 (RFC 8439 §2.8).
    ///
    /// <c>Seal</c> produce ciphertext || tag (tag in coda, 16 byte) e
    /// <c>Open</c> verifica il tag a tempo costante prima di restituire il
    /// plaintext: un tag sbagliato lancia, non restituisce dati parziali.
    /// </summary>
    public sealed class ChaCha20Poly1305
    {
        public const int TagSize = 16;
        public const int NonceSize = 12;
        public const int KeySize = 32;

        private readonly byte[] _key;

        public ChaCha20Poly1305(byte[] key)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("la chiave deve essere di 32 byte", "key");
            _key = new byte[KeySize];
            Array.Copy(key, _key, KeySize);
        }

        public byte[] Seal(byte[] nonce, byte[] aad, byte[] plaintext)
        {
            ValidateNonce(nonce);
            if (plaintext == null) throw new ArgumentNullException("plaintext");

            byte[] ciphertext = ChaCha20.XorStream(_key, 1, nonce, plaintext);
            byte[] tag = ComputeTag(nonce, aad, ciphertext);

            byte[] output = new byte[ciphertext.Length + TagSize];
            Array.Copy(ciphertext, 0, output, 0, ciphertext.Length);
            Array.Copy(tag, 0, output, ciphertext.Length, TagSize);
            return output;
        }

        public byte[] Open(byte[] nonce, byte[] aad, byte[] ciphertextAndTag)
        {
            ValidateNonce(nonce);
            if (ciphertextAndTag == null) throw new ArgumentNullException("ciphertextAndTag");
            if (ciphertextAndTag.Length < TagSize) throw new ArgumentException("ciphertext piu' corto del tag", "ciphertextAndTag");

            int length = ciphertextAndTag.Length - TagSize;
            byte[] ciphertext = new byte[length];
            Array.Copy(ciphertextAndTag, 0, ciphertext, 0, length);
            byte[] expected = ComputeTag(nonce, aad, ciphertext);

            if (!ConstantTimeEquals(expected, ciphertextAndTag, length)) throw new TagVerificationException("tag Poly1305 non valido");
            return ChaCha20.XorStream(_key, 1, nonce, ciphertext);
        }

        private byte[] ComputeTag(byte[] nonce, byte[] aad, byte[] ciphertext)
        {
            byte[] polyKey = ChaCha20.Poly1305Key(_key, nonce);

            int aadLength = aad == null ? 0 : aad.Length;
            byte[] macData = new byte[PaddedLength(aadLength) + PaddedLength(ciphertext.Length) + 16];
            int offset = 0;
            if (aadLength > 0)
            {
                Array.Copy(aad, 0, macData, offset, aadLength);
                offset += PaddedLength(aadLength);
            }
            Array.Copy(ciphertext, 0, macData, offset, ciphertext.Length);
            offset += PaddedLength(ciphertext.Length);
            WriteUInt64(macData, offset, (ulong)aadLength);
            WriteUInt64(macData, offset + 8, (ulong)ciphertext.Length);

            return Poly1305.Tag(polyKey, macData);
        }

        private static int PaddedLength(int length)
        {
            return length + ((16 - (length % 16)) % 16);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)((value >> (8 * i)) & 0xff);
            }
        }

        private static bool ConstantTimeEquals(byte[] expected, byte[] actual, int actualOffset)
        {
            int diff = 0;
            for (int i = 0; i < TagSize; i++)
            {
                diff |= expected[i] ^ actual[actualOffset + i];
            }
            return diff == 0;
        }

        private static void ValidateNonce(byte[] nonce)
        {
            if (nonce == null || nonce.Length != NonceSize) throw new ArgumentException("il nonce deve essere di 12 byte", "nonce");
        }
    }

    /// <summary>
    /// Errore di verifica crittografica. Nome distinto da
    /// <c>System.Security.Cryptography.CryptographicException</c> per non
    /// collidere quando un file importa entrambi gli spazi dei nomi.
    /// </summary>
    public class TagVerificationException : Exception
    {
        public TagVerificationException(string message) : base(message) { }
    }
}
