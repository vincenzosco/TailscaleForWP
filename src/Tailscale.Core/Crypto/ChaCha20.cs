using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// ChaCha20 (RFC 8439): contatore a 32 bit + nonce a 96 bit.
    ///
    /// Il contatore e' un parametro esplicito perche' l'AEAD ha bisogno del
    /// blocco 0 per derivare la chiave Poly1305 e del blocco 1 in poi per i dati:
    /// confondere i due produce un MAC che il peer rifiuta.
    /// </summary>
    public static class ChaCha20
    {
        public const int BlockSize = 64;

        private const uint C0 = 0x61707865;
        private const uint C1 = 0x3320646e;
        private const uint C2 = 0x79622d32;
        private const uint C3 = 0x6b206574;

        public static byte[] Block(byte[] key, uint counter, byte[] nonce)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("la chiave deve essere di 32 byte", "key");
            if (nonce == null || nonce.Length != 12) throw new ArgumentException("il nonce deve essere di 12 byte", "nonce");

            uint[] state = new uint[16];
            state[0] = C0;
            state[1] = C1;
            state[2] = C2;
            state[3] = C3;
            for (int i = 0; i < 8; i++) state[4 + i] = ReadUInt32(key, i * 4);
            state[12] = counter;
            for (int i = 0; i < 3; i++) state[13 + i] = ReadUInt32(nonce, i * 4);

            uint[] working = new uint[16];
            Array.Copy(state, working, 16);

            for (int i = 0; i < 10; i++)
            {
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            byte[] output = new byte[BlockSize];
            for (int i = 0; i < 16; i++)
            {
                uint value = unchecked(working[i] + state[i]);
                WriteUInt32(output, i * 4, value);
            }
            return output;
        }

        /// <summary>XOR del dato con il keystream che parte dal blocco indicato.</summary>
        public static byte[] XorStream(byte[] key, uint counter, byte[] nonce, byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            byte[] output = new byte[data.Length];
            byte[] block = null;
            for (int offset = 0; offset < data.Length; offset += BlockSize)
            {
                block = Block(key, counter, nonce);
                counter = unchecked(counter + 1);
                int count = Math.Min(BlockSize, data.Length - offset);
                for (int i = 0; i < count; i++)
                {
                    output[offset + i] = (byte)(data[offset + i] ^ block[i]);
                }
            }
            return output;
        }

        /// <summary>Primi 32 byte del blocco 0: la chiave Poly1305 dell'AEAD.</summary>
        public static byte[] Poly1305Key(byte[] key, byte[] nonce)
        {
            byte[] block = Block(key, 0, nonce);
            byte[] polyKey = new byte[32];
            Array.Copy(block, 0, polyKey, 0, 32);
            return polyKey;
        }

        private static void QuarterRound(uint[] state, int a, int b, int c, int d)
        {
            unchecked
            {
                state[a] += state[b]; state[d] ^= state[a]; state[d] = RotateLeft(state[d], 16);
                state[c] += state[d]; state[b] ^= state[c]; state[b] = RotateLeft(state[b], 12);
                state[a] += state[b]; state[d] ^= state[a]; state[d] = RotateLeft(state[d], 8);
                state[c] += state[d]; state[b] ^= state[c]; state[b] = RotateLeft(state[b], 7);
            }
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }

        internal static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        internal static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
            buffer[offset + 2] = (byte)((value >> 16) & 0xff);
            buffer[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
