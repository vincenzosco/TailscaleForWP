using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Hex minuscolo, il formato che Tailscale usa per le chiavi nei testi
    /// (`nodekey:`, `mkey:`, `discokey:`).
    /// </summary>
    public static class Hex
    {
        public static string Encode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");

            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Nibble(bytes[i] >> 4);
                chars[i * 2 + 1] = Nibble(bytes[i] & 0x0F);
            }
            return new string(chars);
        }

        public static byte[] Decode(string hex)
        {
            if (hex == null) throw new ArgumentNullException("hex");
            if (hex.Length % 2 != 0)
                throw new FormatException("hex di lunghezza dispari: " + hex.Length);

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int high = Digit(hex[i * 2]);
                int low = Digit(hex[i * 2 + 1]);
                result[i] = (byte)((high << 4) | low);
            }
            return result;
        }

        private static char Nibble(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }

        private static int Digit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("carattere non esadecimale: '" + c + "'");
        }
    }
}
