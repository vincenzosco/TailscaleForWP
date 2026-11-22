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
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                string[] fields = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3) continue;
                if (!string.Equals(fields[0], name, StringComparison.OrdinalIgnoreCase)) continue;

                string type = fields[1].ToUpperInvariant();
                string kind;
                if (type == "REG_SZ") kind = RegValue.Sz;
                else if (type == "REG_DWORD") kind = RegValue.Dword;
                else continue;  // REG_MULTI_SZ e amici: non sono valori che il piano sa descrivere

                // Il testo del valore e' il resto della riga, con gli spazi
                // interni preservati: un ProxyServer "127.0.0.1:1080" e un nome
                // con spazi devono tornare interi.
                int start = line.IndexOf(fields[1], fields[0].Length, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start += fields[1].Length;
                string text = line.Substring(start).Trim();

                try
                {
                    value = new RegValue(kind, text);
                    return true;
                }
                catch (ArgumentException)
                {
                    // Un dword non numerico (o un REG_SZ vuoto): il valore c'e'
                    // ma non e' qualcosa che il piano sa confrontare.
                    return false;
                }
            }
            return false;
        }
    }
}