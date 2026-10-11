using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// HKDF con SHA-256 (RFC 5869), nella forma extract-then-expand.
    ///
    /// Nota: il canale di controllo Noise NON usa HKDF-SHA256 — usa l'HKDF
    /// istanziato su BLAKE2s dentro SymmetricState. Questa classe esiste perche'
    /// fa parte del contratto della spec (§5) e serve altrove (derivazione di chiavi
    /// applicative); l'HMAC e' quello gestito di Sha256.Hmac, perche' System.Security.Cryptography
    /// non esiste nel profilo di riferimento di Windows Phone 8.1.
    /// </summary>
    public static class Hkdf
    {
        public const int Sha256OutputSize = 32;

        /// <summary>HKDF-Extract(salt, IKM) -> PRK.</summary>
        public static byte[] ExtractSha256(byte[] salt, byte[] inputKeyMaterial)
        {
            if (inputKeyMaterial == null) throw new ArgumentNullException("inputKeyMaterial");
            // RFC 5869 §2.2: senza salt si usa una stringa di HashLen zeri.
            byte[] effectiveSalt = salt == null ? new byte[Sha256OutputSize] : salt;
            return Hmac(effectiveSalt, inputKeyMaterial);
        }

        /// <summary>HKDF-Expand(PRK, info, L) -> OKM.</summary>
        public static byte[] ExpandSha256(byte[] pseudoRandomKey, byte[] info, int length)
        {
            if (pseudoRandomKey == null) throw new ArgumentNullException("pseudoRandomKey");
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            if (length > 255 * Sha256OutputSize) throw new ArgumentOutOfRangeException("length", "massimo 255*32 byte");

            byte[] output = new byte[length];
            byte[] previous = new byte[0];
            int offset = 0;
            byte counter = 1;

            while (offset < length)
            {
                byte[] input = new byte[previous.Length + (info == null ? 0 : info.Length) + 1];
                Array.Copy(previous, 0, input, 0, previous.Length);
                if (info != null) Array.Copy(info, 0, input, previous.Length, info.Length);
                input[input.Length - 1] = counter;

                previous = Hmac(pseudoRandomKey, input);
                int count = Math.Min(previous.Length, length - offset);
                Array.Copy(previous, 0, output, offset, count);
                offset += count;
                counter++;
            }
            return output;
        }

        /// <summary>Extract seguito da Expand, la forma usata normalmente.</summary>
        public static byte[] Sha256(byte[] inputKeyMaterial, byte[] salt, byte[] info, int length)
        {
            byte[] prk = ExtractSha256(salt, inputKeyMaterial);
            return ExpandSha256(prk, info, length);
        }

        private static byte[] Hmac(byte[] key, byte[] data)
        {
            // Qualificato: dentro questa classe "Sha256" e' anche il nome del metodo
            // di derivazione, che nasconderebbe la classe omonima.
            return Crypto.Sha256.Hmac(key, data);
        }
    }
}
