using System;
using System.Text;

namespace Tailscale.Core
{
    /// <summary>
    /// Conversioni di testo condivise.
    ///
    /// Esiste per un motivo preciso: il corpo delle richieste di controllo non e'
    /// ASCII. Un hostname puo' avere accenti (e il nome macchina di Windows li ha
    /// spesso), e passarlo per una conversione ASCII lo degrada a `?` — il server
    /// vedrebbe un altro nome, senza errore e senza modo di accorgersene.
    /// </summary>
    public static class Text
    {
        /// <summary>Il corpo da mandare sulla rete: UTF-8, come vuole JSON.</summary>
        public static byte[] Utf8Bytes(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>Un corpo ricevuto, interpretato come UTF-8.</summary>
        public static string FromUtf8(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            // Il profilo WP8.1 non ha GetString(byte[]): si passa dagli indici, che
            // esistono su entrambi i target.
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Byte come latin-1: un byte, un carattere. E' la conversione giusta per
        /// i pezzi di protocollo (header HTTP, prefissi di lunghezza), dove il
        /// contenuto *e'* byte e non testo da interpretare.
        /// </summary>
        public static string Latin1(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
