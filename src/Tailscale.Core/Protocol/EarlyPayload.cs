using System;
using System.IO;
using System.Text;

namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// L'"early payload" che il control server puo' mandare *prima* dei frame
    /// HTTP/2, subito dopo l'handshake Noise.
    ///
    /// Dal sorgente di riferimento (control/ts2021/conn.go): i primi 9 byte dal
    /// server sono o l'inizio di un frame HTTP/2, oppure 5 byte di magic
    /// ("\xff\xff\xffTS") piu' una lunghezza big-endian a 4 byte, seguiti da
    /// altrettanti byte di JSON `tailcfg.EarlyNoise`. La scelta del magic non e'
    /// casuale: comincia con 0xff, che non e' un tipo di frame HTTP/2 valido,
    /// proprio perche' i due casi si possano distinguere.
    ///
    /// Il payload e' opzionale e in ogni caso il client non ne ha bisogno (porta
    /// la sfida del node key, che nessun client attuale usa): qui si legge e si
    /// logga. Sbagliare a leggerlo costa caro in modo silenzioso — i 9 byte
    /// verrebbero interpretati come frame e la connessione si chiuderebbe subito
    /// senza che nulla dica perche'.
    /// </summary>
    public static class EarlyPayload
    {
        /// <summary>5 byte che non possono essere un frame HTTP/2 valido.</summary>
        public static readonly byte[] Magic = new byte[] { 0xFF, 0xFF, 0xFF, (byte)'T', (byte)'S' };

        private const int HeaderLength = 9;
        private const int MaxPayloadBytes = 10 << 20;

        /// <summary>
        /// Legge l'early payload se c'e'. Restituisce true se l'ha consumato; se i
        /// 9 byte non sono il magic li rimette nello stream e restituisce false,
        /// cosi' il chiamante legge il primo frame HTTP/2 come se nulla fosse.
        /// </summary>
        public static bool TryConsume(PushbackStream stream, ILogSink log, out string json)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (log == null) log = NullLogSink.Instance;

            json = null;

            byte[] header = new byte[HeaderLength];
            if (!ReadAtLeast(stream, header, HeaderLength))
            {
                // Stream chiuso subito: non e' un errore di formato qui, lo dira'
                // chi legge il primo frame.
                log.Log("early payload: lo stream si e' chiuso prima dei 9 byte di testa");
                return false;
            }

            bool magic = true;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (header[i] != Magic[i]) { magic = false; break; }
            }

            if (!magic)
            {
                // Questi 9 byte sono la testa di un frame HTTP/2: vanno rimessi.
                stream.Pushback(header);
                log.Log("early payload: assente (i primi byte sono un frame HTTP/2: tipo "
                        + (int)header[3] + ", lunghezza "
                        + ((header[0] << 16) | (header[1] << 8) | header[2]) + ")");
                return false;
            }

            int length = (header[5] << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
            if (length < 0 || length > MaxPayloadBytes)
            {
                throw new Http2Exception("early payload di " + length
                    + " byte: oltre il massimo accettato (" + MaxPayloadBytes + ")");
            }

            byte[] payload = new byte[length];
            if (!ReadAtLeast(stream, payload, length))
                throw new Http2Exception("early payload troncato: attesi " + length + " byte di JSON");

            // Il payload e' JSON (tailcfg.EarlyNoise): UTF-8, non un byte per
            // carattere.
            json = Text.FromUtf8(payload);
            log.Log("early payload: " + length + " byte, contenuto = " + Preview(json));
            return true;
        }

        /// <summary>Legge esattamente count byte; false se lo stream finisce prima.</summary>
        private static bool ReadAtLeast(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                try
                {
                    n = stream.Read(buffer, read, count - read);
                }
                catch (IOException)
                {
                    return false;
                }
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "<vuoto>";
            string trimmed = text.Length <= 160 ? text : text.Substring(0, 160) + "...";
            StringBuilder clean = new StringBuilder(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                clean.Append(c >= ' ' && c <= '~' ? c : '.');
            }
            return "\"" + clean + "\"";
        }
    }
}
