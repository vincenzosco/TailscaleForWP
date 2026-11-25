using System;

namespace Tailscale.Core
{
    /// <summary>
    /// L'output di `reg query /v` diventa un <see cref="RegValue"/>. Serve al
    /// canale CMD Injector: l'unico modo per leggere un valore attraverso un
    /// guscio di comandi e' leggere il testo che `reg` stampa.
    ///
    /// La forma attesa e' quella vera di reg.exe:
    ///
    ///     HKEY_LOCAL_MACHINE\SOFTWARE\TailscaleWP
    ///         VpnLink    REG_SZ    on
    ///
    /// Il nome del valore, il tipo, il resto della riga. I nomi delle chiavi non
    /// contano: si cerca la riga del valore, non l'intestazione.
    /// </summary>
    public static class RegQueryParser
    {
        public static bool TryParseValue(string output, string name, out RegValue value)
        {
            value = null;
            if (output == null || name == null) return false;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;

                // Il nome puo' contenere spazi ("Vpn Link"), quindi la riga non si
                // spezza in campi: il separatore reale e' il token del tipo,
                // delimitato da spazi. Tutto quello che sta prima e' il nome (con
                // gli spazi interni intatti), tutto quello che sta dopo e' il
                // dato, con gli spazi interni preservati: un ProxyServer
                // "127.0.0.1:1080" e un nome con spazi devono tornare interi.
                int search = 0;
                while (true)
                {
                    string kind;
                    int typeLength;
                    int typeStart = FindType(line, search, out kind, out typeLength);
                    if (typeStart < 0) break;

                    if (string.Equals(line.Substring(0, typeStart).Trim(), name,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        string text = line.Substring(typeStart + typeLength).Trim();
                        try
                        {
                            value = new RegValue(kind, text);
                            return true;
                        }
                        catch (ArgumentException)
                        {
                            // Un dword non numerico: il valore c'e' ma non e'
                            // qualcosa che il piano sa confrontare.
                            return false;
                        }
                    }

                    // Il tipo puo' comparire anche nel nome (un valore si puo'
                    // chiamare "Vpn REG_SZ x"): si prova al token successivo.
                    search = typeStart + typeLength;
                }
            }
            return false;
        }

        /// <summary>
        /// Vero se l'output contiene una riga di valore con questo nome, con
        /// QUALSIASI tipo (REG_SZ, REG_BINARY, REG_MULTI_SZ...). Distingue
        /// "il valore c'e' ma non si legge" da "non c'e'", senza guardare le
        /// parole di reg.exe. Il nome si confronta per intero: "Vpn" non deve
        /// prendere la riga di "Vpn Link".
        /// </summary>
        public static bool HasValueLine(string output, string name)
        {
            if (output == null || name == null) return false;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;

                int search = 0;
                while (true)
                {
                    int typeLength;
                    int typeStart = FindAnyType(line, search, out typeLength);
                    if (typeStart < 0) break;
                    if (string.Equals(line.Substring(0, typeStart).Trim(), name,
                                      StringComparison.OrdinalIgnoreCase)) return true;
                    search = typeStart + typeLength;
                }
            }
            return false;
        }

        /// <summary>
        /// Vero se l'output contiene la riga di una chiave con questo percorso:
        /// l'elenco di una chiave comincia con la riga della chiave, scritta
        /// per intero. Le righe di valore e i messaggi d'errore non contano.
        /// </summary>
        public static bool HasKeyLine(string output, string keyPath)
        {
            if (output == null || keyPath == null) return false;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.Equals(lines[i].Trim(), keyPath,
                                  StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// L'inizio del primo token REG_... da `from` in poi, delimitato da
        /// spazi, o -1. Qui contano tutti i tipi, anche quelli che il piano non
        /// sa descrivere: serve a vedere che una riga di valore esiste.
        /// </summary>
        private static int FindAnyType(string line, int from, out int length)
        {
            for (int i = from; i < line.Length; i++)
            {
                if (i > 0 && !char.IsWhiteSpace(line[i - 1])) continue;
                if (string.Compare(line, i, "REG_", 0, 4,
                                   StringComparison.OrdinalIgnoreCase) != 0) continue;
                int end = i + 4;
                while (end < line.Length && (char.IsLetter(line[end]) || line[end] == '_')) end++;
                if (end > i + 4 && (end == line.Length || char.IsWhiteSpace(line[end])))
                {
                    length = end - i;
                    return i;
                }
            }
            length = 0;
            return -1;
        }

        /// <summary>
        /// L'inizio del primo token REG_SZ/REG_DWORD da `from` in poi, delimitato
        /// da spazi, o -1. REG_MULTI_SZ e amici: non sono valori che il piano sa
        /// descrivere e non fanno da separatore fra nome e dato.
        /// </summary>
        private static int FindType(string line, int from, out string kind, out int length)
        {
            for (int i = from; i < line.Length; i++)
            {
                if (i > 0 && !char.IsWhiteSpace(line[i - 1])) continue;
                if (EndsAsToken(line, i, "REG_SZ"))
                {
                    kind = RegValue.Sz;
                    length = "REG_SZ".Length;
                    return i;
                }
                if (EndsAsToken(line, i, "REG_DWORD"))
                {
                    kind = RegValue.Dword;
                    length = "REG_DWORD".Length;
                    return i;
                }
            }
            kind = null;
            length = 0;
            return -1;
        }

        private static bool EndsAsToken(string line, int start, string token)
        {
            if (start + token.Length > line.Length) return false;
            if (string.Compare(line, start, token, 0, token.Length,
                               StringComparison.OrdinalIgnoreCase) != 0) return false;
            return start + token.Length == line.Length || char.IsWhiteSpace(line[start + token.Length]);
        }
    }
}