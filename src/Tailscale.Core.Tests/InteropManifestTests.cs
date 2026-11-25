using System;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il manifest e' la porta fra chi configura e chi scrive: una riga sbagliata
    /// deve dire la riga, e un path fuori dalla whitelist deve essere rifiutato
    /// con il path nel messaggio. Il caso che questi test difendono e' il terzo
    /// del piano 3: un valore di sistema scritto "per provare" e' un guasto, non
    /// un tentativo.
    /// </summary>
    public static class InteropManifestTests
    {
        public static void Run()
        {
            Case("manifest: il default ha connection, on e off", TheDefaultHasThreeSections);
            Case("manifest: commenti e righe vuote si saltano", CommentsAndBlankLinesAreSkipped);
            Case("manifest: i due punti nel valore restano nel valore", ColonsStayInTheValue);
            Case("manifest: una riga malformata dice la riga", ABadLineNamesItsLine);
            Case("manifest: una sezione sconosciuta ferma tutto", AnUnknownSectionStopsTheParser);
            Case("manifest: un path fuori whitelist e' rifiutato con il path", AnOutsidePathIsRefusedWithItsPath);
            Case("whitelist: le sottochiavi delle radici concesse passano", SubKeysOfAllowedRootsPass);
            Case("valore: forma tipo:testo e uguaglianza", ValueShapeAndEquality);
            Case("valore: dword in hex e decimale sono lo stesso valore", HexAndDecimalDwordsAreEqual);
            Case("reg query: l'output diventa un valore", RegQueryOutputBecomesAValue);
            Case("reg query: nomi con spazi, dati vuoti e nomi prefisso", RegQuerySpacedNamesAndEmptyData);
            Case("reg query: la presenza si legge dalle righe, non dalle frasi", PresenceIsProvenByLines);
            Case("reg query: la riga della chiave si distingue dal resto", KeyLineIsNotJustAnyLine);
        }

        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TheDefaultHasThreeSections()
        {
            InteropPlan plan = InteropManifest.Parse(InteropManifest.Default);

            T.Eq(1, plan.Connection.Count, "manifest default: una voce di connessione");
            T.Eq(1, plan.On.Count, "manifest default: una voce di attivazione");
            T.Eq(1, plan.Off.Count, "manifest default: una voce di disattivazione");
            T.Eq("HKLM\\SOFTWARE\\TailscaleWP\\VpnLink", plan.On[0].FullName,
                 "manifest default: la voce di attivazione sta nella chiave dell'app");
            T.Eq("sz:on", plan.On[0].Value.ToString(), "manifest default: il valore di attivazione");
        }

        private static void CommentsAndBlankLinesAreSkipped()
        {
            InteropPlan plan = InteropManifest.Parse(
                "# intestazione\n\n[on]\n\n# voce commentata\nHKLM|SOFTWARE\\TailscaleWP|A|sz:1\n");

            T.Eq(1, plan.On.Count, "manifest: solo le righe di valore contano");
            T.Eq(0, plan.Connection.Count, "manifest: sezione non dichiarata resta vuota");
        }

        private static void ColonsStayInTheValue()
        {
            InteropPlan plan = InteropManifest.Parse(
                "[on]\r\nHKLM|SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings|ProxyServer|sz:127.0.0.1:1080\r\n"
                + "HKLM|SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings|ProxyEnable|dword:1\r\n");

            T.Eq("127.0.0.1:1080", plan.On[0].Value.Text, "manifest: i due punti dell'indirizzo restano nel valore");
            T.Eq("dword", plan.On[1].Value.Kind, "manifest: il tipo dword si riconosce");
            T.Eq("1", plan.On[1].Value.Text, "manifest: il valore dword resta testo");
        }

        private static void ABadLineNamesItsLine()
        {
            string caught = null;
            try
            {
                InteropManifest.Parse("[on]\nHKLM|SOFTWARE\\TailscaleWP|A|sz:1\nsenza separatori\n");
            }
            catch (FormatException ex)
            {
                caught = ex.Message;
            }
            T.True(caught != null, "manifest: una riga malformata e' un errore");
            T.True(caught != null && caught.Contains("riga 3"),
                   "manifest: il messaggio dice la riga sbagliata (" + caught + ")");
        }

        private static void AnUnknownSectionStopsTheParser()
        {
            string caught = null;
            try
            {
                InteropManifest.Parse("[on]\nHKLM|SOFTWARE\\TailscaleWP|A|sz:1\n[altro]\n");
            }
            catch (FormatException ex)
            {
                caught = ex.Message;
            }
            T.True(caught != null && caught.Contains("riga 3"),
                   "manifest: la sezione sconosciuta dice la riga (" + caught + ")");
        }

        private static void AnOutsidePathIsRefusedWithItsPath()
        {
            string caught = null;
            try
            {
                InteropManifest.Parse("[on]\nHKLM|SYSTEM\\CurrentControlSet\\Services|Qualcosa|sz:1\n");
            }
            catch (ArgumentException ex)
            {
                caught = ex.Message;
            }
            T.True(caught != null, "manifest: un path fuori whitelist e' un errore");
            T.True(caught != null && caught.Contains("SYSTEM\\CurrentControlSet\\Services"),
                   "manifest: il messaggio contiene il path rifiutato (" + caught + ")");
        }

        private static void SubKeysOfAllowedRootsPass()
        {
            T.True(InteropWhitelist.IsAllowed("HKLM", "SOFTWARE\\TailscaleWP\\Sottochiave"),
                   "whitelist: la sottochiave dell'app e' ammessa");
            T.True(InteropWhitelist.IsAllowed("HKLM", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"),
                   "whitelist: il proxy di sistema e' ammesso");
            T.False(InteropWhitelist.IsAllowed("HKLM", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run"),
                    "whitelist: l'autostart di sistema non e' ammesso (serve la discovery)");
            T.False(InteropWhitelist.IsAllowed("HKLM", "SYSTEM\\CurrentControlSet"),
                    "whitelist: il resto del registro di sistema non e' ammesso");
            T.False(InteropWhitelist.IsAllowed("HKU", "SOFTWARE\\TailscaleWP"),
                    "whitelist: gli altri alveari non sono ammessi");
        }

        private static void ValueShapeAndEquality()
        {
            T.Eq("sz:ciao", RegValue.Parse("sz:ciao").ToString(), "valore: sz va e torna");
            T.Eq("42", RegValue.Parse("dword:42").Text, "valore: dword va e torna");
            T.Eq("sz:", new RegValue("sz", "").ToString(), "valore: il testo vuoto e' sz:");
            T.Eq("", RegValue.Parse("sz:").Text, "valore: sz: si legge come testo vuoto");
            T.True(new RegValue("sz", "").Equals(RegValue.Parse("sz:")),
                   "valore: il testo vuoto va e torna");
            T.True(new RegValue("sz", "a").Equals(RegValue.Parse("sz:a")), "valore: due valori uguali sono uguali");
            T.False(new RegValue("sz", "1").Equals(RegValue.Parse("dword:1")), "valore: tipi diversi non sono uguali");

            string caught = null;
            try { RegValue.Parse("senzaduepunti"); }
            catch (FormatException ex) { caught = ex.Message; }
            T.True(caught != null, "valore: senza i due punti e' un errore");

            caught = null;
            try { RegValue.Parse("dword:"); }
            catch (FormatException ex) { caught = ex.Message; }
            T.True(caught != null, "valore: un dword senza testo resta un errore");
        }

        private static void HexAndDecimalDwordsAreEqual()
        {
            T.Eq("1", RegValue.Parse("dword:0x1").Text, "valore: l'hex diventa decimale");
            T.True(RegValue.Parse("dword:0x1").Equals(RegValue.Parse("dword:1")),
                   "valore: dword:0x1 e dword:1 sono lo stesso valore");

            string caught = null;
            try { new RegValue("dword", "nonUnNumero"); }
            catch (ArgumentException ex) { caught = ex.Message; }
            T.True(caught != null, "valore: un dword non numerico e' un errore");
        }

        private static void RegQueryOutputBecomesAValue()
        {
            RegValue value;
            T.True(RegQueryParser.TryParseValue(
                       "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP\r\n    VpnLink    REG_SZ    on\r\n",
                       "VpnLink", out value),
                   "reg query: una riga REG_SZ si legge");
            T.Eq("on", value.Text, "reg query: il valore torna intero");

            T.True(RegQueryParser.TryParseValue(
                       "    ProxyEnable    REG_DWORD    0x1\r\n",
                       "ProxyEnable", out value),
                   "reg query: una riga REG_DWORD si legge");
            T.Eq("1", value.Text, "reg query: il dword hex diventa decimale");

            T.True(RegQueryParser.TryParseValue(
                       "    ProxyServer    REG_SZ    127.0.0.1:1080\r\n",
                       "ProxyServer", out value),
                   "reg query: un valore con i due punti resta intero");
            T.Eq("127.0.0.1:1080", value.Text, "reg query: il valore con i due punti");

            T.False(RegQueryParser.TryParseValue(
                        "    Altro    REG_SZ    x\r\n", "VpnLink", out value),
                    "reg query: il valore cercato e non presente e' false");
            T.False(RegQueryParser.TryParseValue(null, "VpnLink", out value),
                    "reg query: output nullo e' false");
        }

        private static void RegQuerySpacedNamesAndEmptyData()
        {
            RegValue value;
            T.True(RegQueryParser.TryParseValue(
                       "    Vpn Link    REG_SZ    on\r\n", "Vpn Link", out value),
                   "reg query: un nome con spazi si legge");
            T.Eq("on", value.Text, "reg query: il valore del nome con spazi torna intero");

            T.True(RegQueryParser.TryParseValue(
                       "    ProxyServer    REG_SZ    a b   c\r\n", "ProxyServer", out value),
                   "reg query: un dato con spazi multipli si legge");
            T.Eq("a b   c", value.Text, "reg query: gli spazi interni del dato restano");

            T.True(RegQueryParser.TryParseValue(
                       "    Empty Val    REG_SZ    \r\n", "Empty Val", out value),
                   "reg query: un REG_SZ vuoto si legge");
            T.Eq("", value.Text, "reg query: il REG_SZ vuoto e' testo vuoto, non assenza");

            T.False(RegQueryParser.TryParseValue(
                        "    Vpn Link    REG_SZ    on\r\n", "Vpn", out value),
                    "reg query: un nome che inizia come un altro non si confonde");

            T.True(RegQueryParser.TryParseValue(
                       "    Vpn REG_SZ x    REG_SZ    y\r\n", "Vpn REG_SZ x", out value),
                   "reg query: un nome con il tipo dentro si legge");
            T.Eq("y", value.Text, "reg query: il dato e' quello dopo il tipo vero");
        }

        private static void PresenceIsProvenByLines()
        {
            // Una riga di valore conta anche con un tipo che il piano non sa
            // descrivere: "c'e' ma non si legge" non e' "non c'e'".
            T.True(RegQueryParser.HasValueLine(
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP\r\n    VpnLink    REG_BINARY    0102\r\n",
                "VpnLink"),
                "una riga REG_BINARY e' una riga di valore");
            T.True(RegQueryParser.HasValueLine(
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP\r\n    Vpn Link    REG_MULTI_SZ    a\r\n    b\r\n",
                "Vpn Link"),
                "una riga REG_MULTI_SZ e' una riga di valore");

            // Il nome si confronta per intero: un prefisso non basta.
            string listing = "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP\r\n"
                + "    Vpn Link    REG_SZ    x\r\n";
            T.False(RegQueryParser.HasValueLine(listing, "Vpn"),
                    "\"Vpn\" non prende la riga di \"Vpn Link\"");
            T.True(RegQueryParser.HasValueLine(listing, "Vpn Link"),
                   "il nome intero si trova");

            // Un messaggio d'errore non contiene righe di valore, in nessuna lingua.
            T.False(RegQueryParser.HasValueLine(
                "ERROR: The system was unable to find the specified registry key or value.",
                "VpnLink"),
                "un messaggio d'errore non e' una riga di valore");
            T.False(RegQueryParser.HasValueLine(
                "Impossibile trovare la chiave o il valore del registro specificato.",
                "VpnLink"),
                "un messaggio in un'altra lingua non e' una riga di valore");
        }

        private static void KeyLineIsNotJustAnyLine()
        {
            string key = "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP";
            T.True(RegQueryParser.HasKeyLine(key + "\r\n    VpnLink    REG_SZ    on\r\n", key),
                   "la prima riga dell'elenco e' la chiave");
            T.False(RegQueryParser.HasKeyLine(
                "HKEY_LOCAL_MACHINE\\SOFTWARE\r\n    TailscaleWP\r\n",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\TailscaleWP"),
                "le sottochiavi elencate dal genitore sono nomi corti, non righe di chiave");
            T.False(RegQueryParser.HasKeyLine(
                key + "\r\n    VpnLink    REG_SZ    on\r\n", key + "\\VpnLink"),
                "una riga di valore non e' una riga di chiave");
            T.False(RegQueryParser.HasKeyLine(
                "ERROR: Access is denied.", key),
                "un messaggio d'errore non e' una riga di chiave");
        }
    }
}