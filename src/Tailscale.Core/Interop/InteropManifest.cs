using System;
using System.Collections.Generic;

namespace Tailscale.Core
{
    /// <summary>
    /// La whitelist chiusa: l'unico perimetro in cui il collegamento via interop
    /// puo' scrivere (piano 3). Tre radici, nient'altro:
    ///
    ///   HKLM\SOFTWARE\TailscaleWP                                    la chiave dell'app
    ///   HKCU\Software\TailscaleWP                                    la stessa, per i test sul desktop
    ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings   il proxy di sistema
    ///
    /// La radice del proxy di sistema e' quella che il design doc chiama
    /// "collegamento di sistema": e' il meccanismo documentato su WP8.1. Le chiavi
    /// del profilo VPN di sistema non sono qui perche' nessuno le ha ancora
    /// verificate su un telefono vero: si aggiungono quando `provisioning/discovery.bat`
    /// le porta in luce, con l'evidenza nel documento, non prima.
    /// </summary>
    public static class InteropWhitelist
    {
        private static readonly string[] HklmRoots =
        {
            "SOFTWARE\\TailscaleWP",
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"
        };

        private static readonly string[] HkcuRoots =
        {
            "Software\\TailscaleWP"
        };

        public static bool IsAllowed(string hive, string path)
        {
            if (hive == null || path == null) return false;
            string normalized = path.Replace('/', '\\').Trim().Trim('\\');
            string[] roots;
            if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) roots = HklmRoots;
            else if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)) roots = HkcuRoots;
            else return false;

            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(normalized, roots[i], StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(roots[i] + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Il manifest del collegamento: cosa aggiunge la connessione, cosa la
    /// attiva, cosa la disattiva. Tre liste di valori di registro, nient'altro.
    /// </summary>
    public sealed class InteropPlan
    {
        /// <summary>Aggiunge la connessione: il profilo, la porta, cio' che la definisce.</summary>
        public readonly List<RegistryEntry> Connection = new List<RegistryEntry>();

        /// <summary>Attiva il collegamento.</summary>
        public readonly List<RegistryEntry> On = new List<RegistryEntry>();

        /// <summary>Disattiva il collegamento.</summary>
        public readonly List<RegistryEntry> Off = new List<RegistryEntry>();

        /// <summary>Ogni valore che il piano sa toccare, nell'ordine: serve al backup e al ripristino.</summary>
        public IEnumerable<RegistryEntry> All()
        {
            for (int i = 0; i < Connection.Count; i++) yield return Connection[i];
            for (int i = 0; i < On.Count; i++) yield return On[i];
            for (int i = 0; i < Off.Count; i++) yield return Off[i];
        }
    }

    /// <summary>
    /// Il testo del manifest diventa un <see cref="InteropPlan"/>. Il formato e'
    /// una voce per riga, `hive|path|nome|tipo:valore`, e tre sezioni:
    /// `[connection]`, `[on]`, `[off]`. Le righe che iniziano con `#` sono
    /// commenti.
    ///
    /// Una riga sbagliata non si salta: ferma il parser con il numero di riga nel
    /// messaggio. Un manifest che non si legge per intero e' un manifesto che non
    /// si capisce, e un valore scritto "a memoria" e' esattamente il guasto che la
    /// whitelist esiste per impedire.
    /// </summary>
    public static class InteropManifest
    {
        /// <summary>
        /// Il manifest che l'app porta con se'. I valori sono quelli dell'app: la
        /// connessione si aggiunge e si attiva senza toccare niente di sistema, e
        /// il giro completo (aggiungi, attiva, disattiva, conferma, ripristina) e'
        /// visibile sul telefono da subito.
        ///
        /// Le righe commentate sono l'esempio del proxy di sistema: si
        /// decommentano quando c'e' una porta locale in ascolto, non prima --
        /// puntare il proxy di sistema a una porta chiusa spegne la rete del
        /// telefono.
        /// </summary>
        public const string Default = @"
# Manifest del collegamento ""VPN via interop"".
# Formato di ogni riga: hive|path|nome|tipo:valore   (tipo: sz oppure dword)
# I path fuori dalla whitelist vengono rifiutati, con il path nel messaggio.
#
# [connection] aggiunge la connessione, [on] la attiva, [off] la disattiva.
# Il backup di ogni valore precedente e' automatico: la riga log:vpn nel
# registro dell'app, oppure backup-<data>.txt nel payload per CMD Injector.

[connection]
HKLM|SOFTWARE\TailscaleWP|VpnProfile|sz:interop

[on]
HKLM|SOFTWARE\TailscaleWP|VpnLink|sz:on

[off]
HKLM|SOFTWARE\TailscaleWP|VpnLink|sz:off

# Esempio, proxy di sistema (solo con una porta locale in ascolto):
# [connection]
# HKLM|SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings|ProxyServer|sz:127.0.0.1:1080
# [on]
# HKLM|SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings|ProxyEnable|dword:1
# [off]
# HKLM|SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings|ProxyEnable|dword:0
";

        public static InteropPlan Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            InteropPlan plan = new InteropPlan();
            List<RegistryEntry> target = null;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.StartsWith("["))
                {
                    string section = line.Trim('[', ']').Trim().ToLowerInvariant();
                    if (section == "connection") target = plan.Connection;
                    else if (section == "on") target = plan.On;
                    else if (section == "off") target = plan.Off;
                    else throw new FormatException("riga " + (i + 1) + ": sezione sconosciuta: " + line);
                    continue;
                }

                if (target == null)
                    throw new FormatException("riga " + (i + 1) + ": valore fuori da ogni sezione: " + line);

                string[] fields = line.Split('|');
                if (fields.Length != 4)
                    throw new FormatException("riga " + (i + 1) + ": atteso hive|path|nome|tipo:valore, trovato: " + line);

                string hive = fields[0].Trim();
                string path = fields[1].Trim();
                string name = fields[2].Trim();
                if (name.Length == 0)
                    throw new FormatException("riga " + (i + 1) + ": nome del valore vuoto");

                if (!InteropWhitelist.IsAllowed(hive, path))
                    throw new ArgumentException("path fuori dalla whitelist: " + hive + "\\" + path);

                RegValue value;
                try
                {
                    value = RegValue.Parse(fields[3].Trim());
                }
                catch (FormatException ex)
                {
                    throw new FormatException("riga " + (i + 1) + ": " + ex.Message);
                }

                target.Add(new RegistryEntry(hive, path, name, value));
            }

            return plan;
        }
    }
}