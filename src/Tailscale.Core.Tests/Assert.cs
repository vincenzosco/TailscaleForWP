using System;
using System.Text;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Helper di asserzione minimale. Non lancia eccezioni: conta i fallimenti
    /// e continua, così un run mostra tutti i problemi invece del primo.
    /// </summary>
    public static class T
    {
        public static int Failures;

        public static void Eq(string expected, string actual, string name)
        {
            if (expected != actual)
            {
                Failures++;
                Console.WriteLine("FAIL " + name);
                Console.WriteLine("  expected: " + (expected == null ? "<null>" : expected));
                Console.WriteLine("  actual:   " + (actual == null ? "<null>" : actual));
            }
        }

        public static void Eq(int expected, int actual, string name)
        {
            Eq(expected.ToString(), actual.ToString(), name);
        }

        public static void Eq(byte[] expected, byte[] actual, string name)
        {
            Eq(ToHex(expected), ToHex(actual), name);
        }

        public static void True(bool condition, string name)
        {
            if (!condition)
            {
                Failures++;
                Console.WriteLine("FAIL " + name + " (atteso true)");
            }
        }

        public static void False(bool condition, string name)
        {
            if (condition)
            {
                Failures++;
                Console.WriteLine("FAIL " + name + " (atteso false)");
            }
        }

        /// <summary>Verifica che l'azione lanci una qualunque eccezione.</summary>
        public static void Throws(Action action, string name)
        {
            try
            {
                action();
                Failures++;
                Console.WriteLine("FAIL " + name + " (nessuna eccezione lanciata)");
            }
            catch (Exception)
            {
            }
        }

        public static string ToHex(byte[] bytes)
        {
            if (bytes == null) return "<null>";
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null) return null;
            if (hex.Length % 2 != 0) throw new ArgumentException("hex di lunghezza dispari");
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }
    }
}
