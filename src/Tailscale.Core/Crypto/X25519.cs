using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// X25519 (Curve25519 Diffie-Hellman) come da RFC 7748.
    ///
    /// Aritmetica di campo a 16 limb da 16 bit, ciascuno in un <c>long</c>: i
    /// prodotti di due limb stanno largamente in 64 bit, quindi non servono
    /// interi a 128 bit (che .NET 4.5 non ha) e il codice e' lo stesso su PC e
    /// telefono. E' la rappresentazione usata da TweetNaCl, qui riscritta in C#.
    ///
    /// Il ladder e' a tempo costante: <c>Sel25519</c> scambia senza rami
    /// dipendenti dai bit dello scalare.
    /// </summary>
    public static class X25519
    {
        public const int KeySize = 32;

        private const long _121665 = 0xDB41;  // (486662 - 2) / 4 = 121665
        private const long _0xffed = 0xffed;
        private const long _0xffff = 0xffff;
        private const long _0x7fff = 0x7fff;

        /// <summary>Moltiplicazione scalare-punto: ScalarMult(n, p).</summary>
        public static byte[] ScalarMult(byte[] scalar, byte[] point)
        {
            if (scalar == null) throw new ArgumentNullException("scalar");
            if (point == null) throw new ArgumentNullException("point");
            if (scalar.Length != KeySize) throw new ArgumentException("lo scalare deve essere di 32 byte", "scalar");
            if (point.Length != KeySize) throw new ArgumentException("il punto deve essere di 32 byte", "point");

            byte[] z = new byte[32];
            long[] x = new long[80];
            long[] a = new long[16], b = new long[16], c = new long[16], d = new long[16];
            long[] e = new long[16], f = new long[16];

            // Clamp: i 3 bit piu' bassi azzerati, il bit 254 acceso.
            for (int i = 0; i < 31; i++) z[i] = scalar[i];
            z[31] = (byte)((scalar[31] & 127) | 64);
            z[0] = (byte)(z[0] & 248);

            Unpack25519(x, point);
            for (int i = 0; i < 16; i++)
            {
                b[i] = x[i];
                d[i] = 0;
                a[i] = 0;
                c[i] = 0;
            }
            a[0] = 1;
            d[0] = 1;

            for (int i = 254; i >= 0; i--)
            {
                long r = (z[i >> 3] >> (i & 7)) & 1;
                Sel25519(a, b, (int)r);
                Sel25519(c, d, (int)r);
                Add(e, a, c);
                Sub(a, a, c);
                Add(c, b, d);
                Sub(b, b, d);
                Sqr(d, e);
                Sqr(f, a);
                Mul(a, c, a);
                Mul(c, b, e);
                Add(e, a, c);
                Sub(a, a, c);
                Sqr(b, a);
                Sub(c, d, f);
                Mul(a, c, Const121665());
                Add(a, a, d);
                Mul(c, c, a);
                Mul(a, d, f);
                Mul(d, b, x);
                Sqr(b, e);
                Sel25519(a, b, (int)r);
                Sel25519(c, d, (int)r);
            }

            for (int i = 0; i < 16; i++)
            {
                x[i + 16] = a[i];
                x[i + 32] = c[i];
                x[i + 48] = b[i];
                x[i + 64] = d[i];
            }

            long[] cInv = new long[16];
            for (int i = 0; i < 16; i++) cInv[i] = x[i + 32];
            Inv25519(cInv, cInv);

            long[] aTimesInv = new long[16];
            for (int i = 0; i < 16; i++) aTimesInv[i] = x[i + 16];
            Mul(aTimesInv, aTimesInv, cInv);

            byte[] result = new byte[32];
            Pack25519(result, aTimesInv);
            return result;
        }

        /// <summary>Moltiplicazione per il punto base (9): da chiave privata a pubblica.</summary>
        public static byte[] ScalarMultBase(byte[] scalar)
        {
            byte[] basePoint = new byte[32];
            basePoint[0] = 9;
            return ScalarMult(scalar, basePoint);
        }

        /// <summary>Chiave pubblica corrispondente alla chiave privata data.</summary>
        public static byte[] PublicFromPrivate(byte[] privateKey)
        {
            return ScalarMultBase(privateKey);
        }

        public static bool IsZeroAll(byte[] value)
        {
            if (value == null) return false;
            int acc = 0;
            for (int i = 0; i < value.Length; i++) acc |= value[i];
            return acc == 0;
        }

        // ---- aritmetica di campo ----

        /// <summary>
        /// La costante (A-2)/4 = 121665 = 0x1DB41 occupa DUE limb da 16 bit:
        /// limb0 = 0xDB41, limb1 = 1. Con un solo limb il ladder produce valori
        /// sbagliati (i vector RFC 7748 lo rilevano subito).
        /// </summary>
        private static long[] Const121665()
        {
            long[] v = new long[16];
            v[0] = _121665;
            v[1] = 1;
            return v;
        }

        private static void Unpack25519(long[] o, byte[] n)
        {
            for (int i = 0; i < 16; i++)
            {
                o[i] = (long)(n[2 * i] + ((long)n[2 * i + 1] << 8));
            }
            o[15] &= _0x7fff;
        }

        private static void Pack25519(byte[] o, long[] n)
        {
            long[] t = new long[16];
            long[] m = new long[16];
            for (int i = 0; i < 16; i++) t[i] = n[i];

            Car25519(t);
            Car25519(t);
            Car25519(t);

            for (int j = 0; j < 2; j++)
            {
                m[0] = t[0] - _0xffed;
                for (int i = 1; i < 15; i++)
                {
                    m[i] = t[i] - _0xffff - ((m[i - 1] >> 16) & 1);
                    m[i - 1] &= _0xffff;
                }
                m[15] = t[15] - _0x7fff - ((m[14] >> 16) & 1);
                long b = (m[15] >> 16) & 1;
                m[14] &= _0xffff;
                Sel25519(t, m, (int)(1 - b));
            }

            for (int i = 0; i < 16; i++)
            {
                o[2 * i] = (byte)(t[i] & 0xff);
                o[2 * i + 1] = (byte)(t[i] >> 8);
            }
        }

        private static void Add(long[] o, long[] a, long[] b)
        {
            for (int i = 0; i < 16; i++) o[i] = a[i] + b[i];
        }

        private static void Sub(long[] o, long[] a, long[] b)
        {
            for (int i = 0; i < 16; i++) o[i] = a[i] - b[i];
        }

        private static void Mul(long[] o, long[] a, long[] b)
        {
            long[] t = new long[31];
            for (int i = 0; i < 31; i++) t[i] = 0;
            for (int i = 0; i < 16; i++)
            {
                for (int j = 0; j < 16; j++)
                {
                    t[i + j] += a[i] * b[j];
                }
            }
            for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
            for (int i = 0; i < 16; i++) o[i] = t[i];
            Car25519(o);
            Car25519(o);
        }

        private static void Sqr(long[] o, long[] a)
        {
            Mul(o, a, a);
        }

        /// <summary>
        /// Propagazione del riporto. Il limb 15 rientra nel limb 0 con il fattore
        /// 38 perche' 2^256 = 38 mod (2^255 - 19).
        /// </summary>
        private static void Car25519(long[] o)
        {
            for (int i = 0; i < 16; i++)
            {
                o[i] += 1L << 16;
                long c = o[i] >> 16;
                if (i < 15)
                {
                    o[i + 1] += c - 1;
                }
                else
                {
                    o[0] += 38 * (c - 1);
                }
                o[i] -= c << 16;
            }
        }

        /// <summary>Scambio condizionale a tempo costante: se b==1 scambia p e q.</summary>
        private static void Sel25519(long[] p, long[] q, int b)
        {
            long c = ~(b - 1L);
            for (int i = 0; i < 16; i++)
            {
                long t = c & (p[i] ^ q[i]);
                p[i] ^= t;
                q[i] ^= t;
            }
        }

        private static void Inv25519(long[] o, long[] i)
        {
            long[] c = new long[16];
            for (int a = 0; a < 16; a++) c[a] = i[a];
            for (int a = 253; a >= 0; a--)
            {
                Sqr(c, c);
                if (a != 2 && a != 4) Mul(c, c, i);
            }
            for (int a = 0; a < 16; a++) o[a] = c[a];
        }
    }
}
