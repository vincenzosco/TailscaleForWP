using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// Il core Salsa20 e HSalsa20, piu' lo stream XSalsa20 usato da crypto_box.
    ///
    /// XSalsa20 (nonce 24 byte) e' Salsa20 con la chiave derivata da HSalsa20 sui
    /// primi 16 byte del nonce, usando i restanti 8 byte come nonce di Salsa20.
    /// Questa e' anche la costruzione di crypto_box_beforenm combinata con
    /// crypto_scalarmult, quindi le due funzioni si verificano a vicenda.
    ///
    /// Verificato indirettamente ma in modo forte: NaClBoxTests confronta la chiave
    /// condivisa e il ciphertext con vector prodotti da TweetNaCl, che coprono
    /// HSalsa20, lo stream e il layout Poly1305 insieme.
    /// </summary>
    public static class Salsa20
    {
        private const uint Sigma0 = 0x61707865; // "expa"
        private const uint Sigma1 = 0x3320646e; // "nd 3"
        private const uint Sigma2 = 0x79622d32; // "2-by"
        private const uint Sigma3 = 0x6b206574; // "te k"

        /// <summary>
        /// HSalsa20: le 8 parole di stato (0,5,10,15,6,7,8,9) dopo i 20 round,
        /// senza la somma finale con lo stato di ingresso.
        /// </summary>
        /// <param name="key">Chiave di 32 byte.</param>
        /// <param name="input">Ingresso di 16 byte, che occupa le parole 6..9.</param>
        public static byte[] HSalsa20(byte[] key, byte[] input)
        {
            RequireLength(key, 32, "key");
            RequireLength(input, 16, "input");

            uint[] x = new uint[16];
            x[0] = Sigma0; x[5] = Sigma1; x[10] = Sigma2; x[15] = Sigma3;
            for (int i = 0; i < 4; i++)
            {
                x[1 + i] = ReadUInt32Le(key, 4 * i);
                x[11 + i] = ReadUInt32Le(key, 16 + 4 * i);
                x[6 + i] = ReadUInt32Le(input, 4 * i);
            }

            for (int i = 0; i < 10; i++) DoubleRound(x);

            byte[] output = new byte[32];
            WriteUInt32Le(output, 0, x[0]);
            WriteUInt32Le(output, 4, x[5]);
            WriteUInt32Le(output, 8, x[10]);
            WriteUInt32Le(output, 12, x[15]);
            WriteUInt32Le(output, 16, x[6]);
            WriteUInt32Le(output, 20, x[7]);
            WriteUInt32Le(output, 24, x[8]);
            WriteUInt32Le(output, 28, x[9]);
            return output;
        }

        /// <summary>Stream Salsa20/20 con nonce di 8 byte e contatore a 64 bit che parte da 0.</summary>
        public static byte[] Keystream(byte[] key, byte[] nonce8, int length)
        {
            RequireLength(key, 32, "key");
            RequireLength(nonce8, 8, "nonce8");
            if (length < 0) throw new ArgumentOutOfRangeException("length");

            byte[] output = new byte[length];
            byte[] block = new byte[64];
            int written = 0;
            long counter = 0;

            while (written < length)
            {
                Block(key, nonce8, counter, block);
                int count = Math.Min(64, length - written);
                Array.Copy(block, 0, output, written, count);
                written += count;
                counter++;
            }
            return output;
        }

        /// <summary>Stream XSalsa20/20 con nonce di 24 byte.</summary>
        public static byte[] XSalsa20Keystream(byte[] key, byte[] nonce24, int length)
        {
            RequireLength(nonce24, 24, "nonce24");

            byte[] prefix = new byte[16];
            Array.Copy(nonce24, 0, prefix, 0, 16);
            byte[] subKey = HSalsa20(key, prefix);

            byte[] nonce8 = new byte[8];
            Array.Copy(nonce24, 16, nonce8, 0, 8);
            return Keystream(subKey, nonce8, length);
        }

        /// <summary>Un blocco Salsa20/20 di 64 byte, con contatore esplicito.</summary>
        public static void Block(byte[] key, byte[] nonce8, long counter, byte[] output)
        {
            RequireLength(key, 32, "key");
            RequireLength(nonce8, 8, "nonce8");
            RequireLength(output, 64, "output");

            uint[] x = new uint[16];
            x[0] = Sigma0; x[5] = Sigma1; x[10] = Sigma2; x[15] = Sigma3;
            for (int i = 0; i < 4; i++)
            {
                x[1 + i] = ReadUInt32Le(key, 4 * i);
                x[11 + i] = ReadUInt32Le(key, 16 + 4 * i);
            }
            x[6] = ReadUInt32Le(nonce8, 0);
            x[7] = ReadUInt32Le(nonce8, 4);
            x[8] = (uint)counter;
            x[9] = (uint)((ulong)counter >> 32);

            uint[] original = (uint[])x.Clone();
            for (int i = 0; i < 10; i++) DoubleRound(x);

            for (int i = 0; i < 16; i++)
                WriteUInt32Le(output, 4 * i, unchecked(x[i] + original[i]));
        }

        private static void DoubleRound(uint[] x)
        {
            // Round per colonne.
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 5, 9, 13, 1);
            QuarterRound(x, 10, 14, 2, 6);
            QuarterRound(x, 15, 3, 7, 11);
            // Round per righe.
            QuarterRound(x, 0, 1, 2, 3);
            QuarterRound(x, 5, 6, 7, 4);
            QuarterRound(x, 10, 11, 8, 9);
            QuarterRound(x, 15, 12, 13, 14);
        }

        private static void QuarterRound(uint[] x, int a, int b, int c, int d)
        {
            x[b] ^= Rotl(unchecked(x[a] + x[d]), 7);
            x[c] ^= Rotl(unchecked(x[b] + x[a]), 9);
            x[d] ^= Rotl(unchecked(x[c] + x[b]), 13);
            x[a] ^= Rotl(unchecked(x[d] + x[c]), 18);
        }

        private static uint Rotl(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }

        private static uint ReadUInt32Le(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }

        private static void WriteUInt32Le(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void RequireLength(byte[] value, int length, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value.Length != length)
                throw new ArgumentException(name + ": attesi " + length + " byte, ricevuti " + value.Length, name);
        }
    }
}
