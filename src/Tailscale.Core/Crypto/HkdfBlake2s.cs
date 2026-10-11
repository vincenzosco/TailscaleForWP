using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// HMAC-BLAKE2s e HKDF-BLAKE2s.
    ///
    /// Il canale di controllo Noise non usa HKDF-SHA256: MixDH e Split derivano le
    /// chiavi con HKDF istanziato su BLAKE2s (`hkdf.New(newBLAKE2s, ...)` in
    /// handshake.go). HMAC e' quello standard RFC 2104 sull'hash non chiavato, non
    /// la modalita' a chiave nativa di BLAKE2s: sono due cose diverse e il server
    /// usa la prima.
    ///
    /// I vector dei test sono prodotti con hmac/hashlib di Python.
    /// </summary>
    public static class HkdfBlake2s
    {
        public const int OutputSize = 32;
        public const int BlockSize = 64;

        /// <summary>HMAC-BLAKE2s (RFC 2104).</summary>
        public static byte[] Hmac(byte[] key, byte[] data)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (data == null) throw new ArgumentNullException("data");

            byte[] k = key.Length > BlockSize ? Blake2s.Hash(key, OutputSize) : key;

            byte[] inner = new byte[BlockSize + data.Length];
            byte[] outer = new byte[BlockSize + OutputSize];
            for (int i = 0; i < BlockSize; i++)
            {
                byte b = i < k.Length ? k[i] : (byte)0;
                inner[i] = (byte)(b ^ 0x36);
                outer[i] = (byte)(b ^ 0x5c);
            }
            Array.Copy(data, 0, inner, BlockSize, data.Length);
            Array.Copy(Blake2s.Hash(inner, OutputSize), 0, outer, BlockSize, OutputSize);
            return Blake2s.Hash(outer, OutputSize);
        }

        /// <summary>HKDF-Extract(salt, ikm) -> PRK. Salt null o vuoto = 32 zeri.</summary>
        public static byte[] Extract(byte[] salt, byte[] inputKeyMaterial)
        {
            if (inputKeyMaterial == null) throw new ArgumentNullException("inputKeyMaterial");
            byte[] effectiveSalt = salt == null || salt.Length == 0 ? new byte[OutputSize] : salt;
            return Hmac(effectiveSalt, inputKeyMaterial);
        }

        /// <summary>HKDF-Expand(PRK, info, length).</summary>
        public static byte[] Expand(byte[] pseudoRandomKey, byte[] info, int length)
        {
            if (pseudoRandomKey == null) throw new ArgumentNullException("pseudoRandomKey");
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            if (length > 255 * OutputSize) throw new ArgumentOutOfRangeException("length", "massimo 255*32 byte");

            byte[] effectiveInfo = info ?? new byte[0];
            byte[] output = new byte[length];
            byte[] previous = new byte[0];
            int offset = 0;
            byte counter = 1;

            while (offset < length)
            {
                byte[] input = new byte[previous.Length + effectiveInfo.Length + 1];
                Array.Copy(previous, 0, input, 0, previous.Length);
                Array.Copy(effectiveInfo, 0, input, previous.Length, effectiveInfo.Length);
                input[input.Length - 1] = counter;

                previous = Hmac(pseudoRandomKey, input);
                int count = Math.Min(previous.Length, length - offset);
                Array.Copy(previous, 0, output, offset, count);
                offset += count;
                counter++;
            }
            return output;
        }

        /// <summary>Extract seguito da Expand: la forma usata da Noise.</summary>
        public static byte[] Derive(byte[] secret, byte[] salt, byte[] info, int length)
        {
            return Expand(Extract(salt, secret ?? new byte[0]), info, length);
        }
    }
}
