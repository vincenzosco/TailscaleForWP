using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Conversione ASCII esplicita.
    ///
    /// Il profilo WP8.1 non espone System.Text.Encoding.ASCII (solo UTF8 e
    /// Unicode), quindi il core non puo' usarla: e' lo stesso tipo di vincolo di
    /// System.Security.Cryptography, ed e' la build ARM a farlo emergere.
    ///
    /// Tutte le stringhe convertite qui (nome dell'istanza Noise, prologo,
    /// messaggi di errore) sono ASCII per costruzione; un carattere fuori
    /// dall'intervallo viene sostituito da '?', non troncato in silenzio a un byte
    /// che cambierebbe il protocollo.
    /// </summary>
    public static class Ascii
    {
        public static byte[] GetBytes(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            byte[] result = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                result[i] = c <= 0x7F ? (byte)c : (byte)'?';
            }
            return result;
        }

        public static string GetString(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");

            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = bytes[i] <= 0x7F ? (char)bytes[i] : '?';
            return new string(chars);
        }
    }
}
