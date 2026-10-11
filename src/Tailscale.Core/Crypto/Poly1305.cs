using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// Poly1305 (RFC 8439 §2.5).
    ///
    /// Accumulatore a 5 limb da 26 bit tenuto in <c>uint</c>, prodotti in
    /// <c>ulong</c>: nessun intero a 128 bit e nessun ramo dipendente dai dati.
    /// E' la variante portabile ("donna-32") riscritta in una versione a un solo
    /// colpo, che e' tutto quello che serve all'AEAD del canale di controllo.
    /// </summary>
    public static class Poly1305
    {
        public const int TagSize = 16;
        private const int BlockSize = 16;

        /// <summary>Tag di 16 byte del messaggio sotto la chiave monouso di 32 byte.</summary>
        public static byte[] Tag(byte[] key32, byte[] message)
        {
            if (key32 == null || key32.Length != 32) throw new ArgumentException("la chiave deve essere di 32 byte", "key32");
            if (message == null) throw new ArgumentNullException("message");

            uint r0 = ChaCha20.ReadUInt32(key32, 0) & 0x3ffffff;
            uint r1 = (ChaCha20.ReadUInt32(key32, 3) >> 2) & 0x3ffff03;
            uint r2 = (ChaCha20.ReadUInt32(key32, 6) >> 4) & 0x3ffc0ff;
            uint r3 = (ChaCha20.ReadUInt32(key32, 9) >> 6) & 0x3f03fff;
            uint r4 = (ChaCha20.ReadUInt32(key32, 12) >> 8) & 0x00fffff;

            uint s1 = unchecked(r1 * 5);
            uint s2 = unchecked(r2 * 5);
            uint s3 = unchecked(r3 * 5);
            uint s4 = unchecked(r4 * 5);

            uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

            int full = message.Length / BlockSize;
            for (int block = 0; block < full; block++)
            {
                Blocks(ref h0, ref h1, ref h2, ref h3, ref h4, message, block * BlockSize, 1u << 24,
                       r0, r1, r2, r3, r4, s1, s2, s3, s4);
            }

            int leftover = message.Length - full * BlockSize;
            if (leftover > 0)
            {
                byte[] last = new byte[BlockSize];
                Array.Copy(message, full * BlockSize, last, 0, leftover);
                last[leftover] = 1;
                // Ultimo blocco: il bit "1 << 128" non si aggiunge (hibit = 0).
                Blocks(ref h0, ref h1, ref h2, ref h3, ref h4, last, 0, 0,
                       r0, r1, r2, r3, r4, s1, s2, s3, s4);
            }

            // Riporto completo.
            uint c = h1 >> 26; h1 &= 0x3ffffff;
            h2 += c; c = h2 >> 26; h2 &= 0x3ffffff;
            h3 += c; c = h3 >> 26; h3 &= 0x3ffffff;
            h4 += c; c = h4 >> 26; h4 &= 0x3ffffff;
            h0 += c * 5; c = h0 >> 26; h0 &= 0x3ffffff;
            h1 += c;

            // h + -p, e selezione se h >= p.
            uint g0 = unchecked(h0 + 5); c = g0 >> 26; g0 &= 0x3ffffff;
            uint g1 = unchecked(h1 + c); c = g1 >> 26; g1 &= 0x3ffffff;
            uint g2 = unchecked(h2 + c); c = g2 >> 26; g2 &= 0x3ffffff;
            uint g3 = unchecked(h3 + c); c = g3 >> 26; g3 &= 0x3ffffff;
            uint g4 = unchecked(h4 + c - (1u << 26));

            uint mask = (g4 >> 31) - 1;  // 0xffffffff se h < p, 0 altrimenti
            g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
            uint nmask = ~mask;
            h0 = (h0 & nmask) | g0;
            h1 = (h1 & nmask) | g1;
            h2 = (h2 & nmask) | g2;
            h3 = (h3 & nmask) | g3;
            h4 = (h4 & nmask) | g4;

            // Prima si ripiega h dalle 5 cifre da 26 bit in quattro word da 32 bit
            // (h % 2^128), POI si somma il pad: sommare il pad alle cifre da 26 bit
            // e ripiegarle dopo produce un tag sbagliato dal quarto byte in poi.
            uint w0 = h0 | (h1 << 26);
            uint w1 = (h1 >> 6) | (h2 << 20);
            uint w2 = (h2 >> 12) | (h3 << 14);
            uint w3 = (h3 >> 18) | (h4 << 8);

            ulong f = (ulong)w0 + ChaCha20.ReadUInt32(key32, 16); w0 = (uint)f;
            f = (ulong)w1 + ChaCha20.ReadUInt32(key32, 20) + (f >> 32); w1 = (uint)f;
            f = (ulong)w2 + ChaCha20.ReadUInt32(key32, 24) + (f >> 32); w2 = (uint)f;
            f = (ulong)w3 + ChaCha20.ReadUInt32(key32, 28) + (f >> 32); w3 = (uint)f;

            byte[] tag = new byte[TagSize];
            ChaCha20.WriteUInt32(tag, 0, w0);
            ChaCha20.WriteUInt32(tag, 4, w1);
            ChaCha20.WriteUInt32(tag, 8, w2);
            ChaCha20.WriteUInt32(tag, 12, w3);
            return tag;
        }

        private static void Blocks(
            ref uint h0, ref uint h1, ref uint h2, ref uint h3, ref uint h4,
            byte[] message, int offset, uint hibit,
            uint r0, uint r1, uint r2, uint r3, uint r4,
            uint s1, uint s2, uint s3, uint s4)
        {
            unchecked
            {
                h0 += ChaCha20.ReadUInt32(message, offset) & 0x3ffffff;
                h1 += (ChaCha20.ReadUInt32(message, offset + 3) >> 2) & 0x3ffffff;
                h2 += (ChaCha20.ReadUInt32(message, offset + 6) >> 4) & 0x3ffffff;
                h3 += (ChaCha20.ReadUInt32(message, offset + 9) >> 6) & 0x3ffffff;
                h4 += (ChaCha20.ReadUInt32(message, offset + 12) >> 8) | hibit;

                ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
                ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
                ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
                ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
                ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

                uint carry = (uint)(d0 >> 26);
                h0 = (uint)d0 & 0x3ffffff;
                d1 += carry; carry = (uint)(d1 >> 26);
                h1 = (uint)d1 & 0x3ffffff;
                d2 += carry; carry = (uint)(d2 >> 26);
                h2 = (uint)d2 & 0x3ffffff;
                d3 += carry; carry = (uint)(d3 >> 26);
                h3 = (uint)d3 & 0x3ffffff;
                d4 += carry; carry = (uint)(d4 >> 26);
                h4 = (uint)d4 & 0x3ffffff;
                h0 += carry * 5;
                carry = h0 >> 26;
                h0 &= 0x3ffffff;
                h1 += carry;
            }
        }
    }
}
