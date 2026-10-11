// SHA-256 (FIPS 180-4) e HMAC-SHA256 (RFC 2104) in codice gestito.
//
// Perche' non System.Security.Cryptography: il profilo WP8.1 non include
// quell'assembly (scoperto compilando la soluzione ARM del telefono, non per
// ipotesi). Implementarlo qui una volta sola evita #if per piattaforma e
// mantiene il core identico sui due target.
//
// Le costanti H e K sono le parti frazionarie delle radici quadrate e cubiche
// dei primi 64 numeri primi, generate e verificate fuori dal codice; i digest
// sono verificati contro hashlib di Python nei test.

using System;

namespace Tailscale.Core.Crypto
{
    public static class Sha256
    {
        public const int DigestSize = 32;
        private const int BlockSize = 64;

        private static readonly uint[] K = new uint[]
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
            0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
            0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
            0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
            0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
            0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
            0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
            0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
            0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        private static readonly uint[] H0 = new uint[]
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        };

        public static byte[] Hash(byte[] data)
        {
            uint[] h = (uint[])H0.Clone();
            byte[] padded = Pad(data);
            for (int off = 0; off < padded.Length; off += BlockSize)
                Compress(h, padded, off);

            byte[] digest = new byte[DigestSize];
            for (int i = 0; i < 8; i++)
            {
                digest[i * 4 + 0] = (byte)(h[i] >> 24);
                digest[i * 4 + 1] = (byte)(h[i] >> 16);
                digest[i * 4 + 2] = (byte)(h[i] >> 8);
                digest[i * 4 + 3] = (byte)(h[i]);
            }
            return digest;
        }

        private static byte[] Pad(byte[] data)
        {
            long bitLen = (long)data.Length * 8;
            int total = data.Length + 1;          // il byte 0x80
            while (total % BlockSize != 56)
                total++;
            total += 8;                           // lunghezza a 64 bit

            byte[] outp = new byte[total];
            Buffer.BlockCopy(data, 0, outp, 0, data.Length);
            outp[data.Length] = 0x80;
            for (int i = 0; i < 8; i++)
                outp[total - 1 - i] = (byte)(bitLen >> (8 * i));
            return outp;
        }

        private static void Compress(uint[] h, byte[] block, int off)
        {
            uint[] w = new uint[64];
            for (int i = 0; i < 16; i++)
            {
                w[i] = ((uint)block[off + i * 4 + 0] << 24)
                     | ((uint)block[off + i * 4 + 1] << 16)
                     | ((uint)block[off + i * 4 + 2] << 8)
                     | ((uint)block[off + i * 4 + 3]);
            }
            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = unchecked(w[i - 16] + s0 + w[i - 7] + s1);
            }

            uint a = h[0], b = h[1], c = h[2], d = h[3];
            uint e = h[4], f = h[5], g = h[6], hh = h[7];

            for (int i = 0; i < 64; i++)
            {
                uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = unchecked(hh + S1 + ch + K[i] + w[i]);
                uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = unchecked(S0 + maj);

                hh = g; g = f; f = e; e = unchecked(d + t1);
                d = c; c = b; b = a; a = unchecked(t1 + t2);
            }

            h[0] = unchecked(h[0] + a); h[1] = unchecked(h[1] + b);
            h[2] = unchecked(h[2] + c); h[3] = unchecked(h[3] + d);
            h[4] = unchecked(h[4] + e); h[5] = unchecked(h[5] + f);
            h[6] = unchecked(h[6] + g); h[7] = unchecked(h[7] + hh);
        }

        private static uint Rotr(uint x, int n)
        {
            return (x >> n) | (x << (32 - n));
        }

        /// <summary>HMAC-SHA256 (RFC 2104).</summary>
        public static byte[] Hmac(byte[] key, byte[] data)
        {
            byte[] k = key.Length > BlockSize ? Hash(key) : key;
            byte[] ipad = new byte[BlockSize];
            byte[] opad = new byte[BlockSize];
            for (int i = 0; i < BlockSize; i++)
            {
                byte b = i < k.Length ? k[i] : (byte)0;
                ipad[i] = (byte)(b ^ 0x36);
                opad[i] = (byte)(b ^ 0x5c);
            }

            byte[] inner = Hash(Concat(ipad, data));
            return Hash(Concat(opad, inner));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] outp = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, outp, 0, a.Length);
            Buffer.BlockCopy(b, 0, outp, a.Length, b.Length);
            return outp;
        }
    }
}
