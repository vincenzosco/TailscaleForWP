using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// BLAKE2s (RFC 7693): hash del canale di controllo
    /// (Noise_IK_25519_ChaChaPoly_BLAKE2s) e base di mac1 in WireGuard.
    ///
    /// Solo modalita' sequenziale: il protocollo non usa il tree hashing.
    /// Il contatore dei byte compressi e' un parametro esplicito di Compress,
    /// secondo RFC 7693 §3.3: i blocchi non finali usano (i+1)*blockSize, l'ultimo
    /// usa la lunghezza del messaggio (piu' blockSize se c'e' una chiave).
    /// </summary>
    public static class Blake2s
    {
        public const int BlockSize = 64;
        public const int MaxOutputSize = 32;
        public const int MaxKeySize = 32;

        private static readonly uint[] IV =
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
        };

        private static readonly byte[,] Sigma =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
            { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
            { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
            { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
            { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
            { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
            { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
            { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
            { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 }
        };

        public static byte[] Hash(byte[] input, int outLen)
        {
            return KeyedHash(null, input, outLen);
        }

        /// <summary>
        /// Hash con chiave. <paramref name="key"/> null o vuoto significa hash
        /// senza chiave, che non equivale a una chiave di 32 byte tutti zero.
        /// </summary>
        public static byte[] KeyedHash(byte[] key, byte[] input, int outLen)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (outLen < 1 || outLen > MaxOutputSize) throw new ArgumentOutOfRangeException("outLen", "1..32 byte");
            int keyLen = key == null ? 0 : key.Length;
            if (keyLen > MaxKeySize) throw new ArgumentException("la chiave non puo' superare 32 byte", "key");

            uint[] h = (uint[])IV.Clone();
            // p[0] = 0x0101kknn: digest_length (nn) | key_length (kk) << 8 | fanout 1 | depth 1
            h[0] ^= 0x01010000u ^ (uint)(keyLen << 8) ^ (uint)outLen;

            ulong counter = 0;

            if (keyLen > 0)
            {
                byte[] keyBlock = new byte[BlockSize];
                Array.Copy(key, keyBlock, keyLen);
                counter = BlockSize;
                Compress(h, keyBlock, 0, counter, false);
            }

            byte[] block = new byte[BlockSize];
            int offset = 0;
            int remaining = input.Length;

            while (remaining > BlockSize)
            {
                counter += BlockSize;
                Compress(h, input, offset, counter, false);
                offset += BlockSize;
                remaining -= BlockSize;
            }

            Array.Copy(input, offset, block, 0, remaining);
            counter += (ulong)remaining;
            Compress(h, block, 0, counter, true);

            byte[] output = new byte[outLen];
            for (int i = 0; i < outLen; i++)
            {
                output[i] = (byte)(h[i / 4] >> (8 * (i % 4)));
            }
            return output;
        }

        /// <summary>
        /// Funzione di compressione F (RFC 7693 §3.2). <paramref name="counter"/>
        /// e' il contatore dei byte t; <paramref name="isFinal"/> inverte i bit di v[14].
        /// </summary>
        private static void Compress(uint[] h, byte[] block, int offset, ulong counter, bool isFinal)
        {
            uint[] m = new uint[16];
            for (int i = 0; i < 16; i++)
            {
                m[i] = ChaCha20.ReadUInt32(block, offset + i * 4);
            }

            uint[] v = new uint[16];
            for (int i = 0; i < 8; i++) v[i] = h[i];
            for (int i = 0; i < 8; i++) v[8 + i] = IV[i];

            unchecked
            {
                v[12] ^= (uint)counter;
                v[13] ^= (uint)(counter >> 32);
            }
            if (isFinal) v[14] = ~v[14];

            for (int round = 0; round < 10; round++)
            {
                Mix(v, 0, 4, 8, 12, m[Sigma[round, 0]], m[Sigma[round, 1]]);
                Mix(v, 1, 5, 9, 13, m[Sigma[round, 2]], m[Sigma[round, 3]]);
                Mix(v, 2, 6, 10, 14, m[Sigma[round, 4]], m[Sigma[round, 5]]);
                Mix(v, 3, 7, 11, 15, m[Sigma[round, 6]], m[Sigma[round, 7]]);
                Mix(v, 0, 5, 10, 15, m[Sigma[round, 8]], m[Sigma[round, 9]]);
                Mix(v, 1, 6, 11, 12, m[Sigma[round, 10]], m[Sigma[round, 11]]);
                Mix(v, 2, 7, 8, 13, m[Sigma[round, 12]], m[Sigma[round, 13]]);
                Mix(v, 3, 4, 9, 14, m[Sigma[round, 14]], m[Sigma[round, 15]]);
            }

            for (int i = 0; i < 8; i++)
            {
                unchecked
                {
                    h[i] ^= v[i] ^ v[i + 8];
                }
            }
        }

        private static void Mix(uint[] v, int a, int b, int c, int d, uint x, uint y)
        {
            unchecked
            {
                v[a] = v[a] + v[b] + x;
                v[d] = RotateRight(v[d] ^ v[a], 16);
                v[c] = v[c] + v[d];
                v[b] = RotateRight(v[b] ^ v[c], 12);
                v[a] = v[a] + v[b] + y;
                v[d] = RotateRight(v[d] ^ v[a], 8);
                v[c] = v[c] + v[d];
                v[b] = RotateRight(v[b] ^ v[c], 7);
            }
        }

        private static uint RotateRight(uint value, int count)
        {
            return (value >> count) | (value << (32 - count));
        }
    }
}
