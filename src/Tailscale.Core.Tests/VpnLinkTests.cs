using System;
using System.Collections.Generic;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il collegamento via interop, tutto il giro: aggiungi, attiva, disattiva,
    /// conferma, ripristino. I casi che difende sono quelli che sul telefono si
    /// pagano cari:
    ///
    /// - uno stato mostrato dalla **rilettura** e non da un flag: la pagina dice
    ///   cio' che il registro contiene davvero;
    /// - nessuna scrittura quando il probe non passa: un telefono bloccato non
    ///   deve ricevere mezzi valori;
    /// - il backup prima della scrittura e il ripristino esatto, assenze comprese;
    /// - la scrittura che non torna alla rilettura e' un fallimento dichiarato,
    ///   non un "fatto" sperato.
    /// </summary>
    public static class VpnLinkTests
    {
        public static void Run()
        {
            Case("vpn: connect aggiunge la connessione e attiva, con conferma", ConnectAddsAndActivates);
            Case("vpn: connect due volte non sovrascrive il backup", ConnectTwiceKeepsTheFirstBackup);
            Case("vpn: una scrittura non confermata e' un fallimento", AnUnconfirmedWriteFails);
            Case("vpn: telefono bloccato, nessuna scrittura", ALockedPhoneWritesNothing);
            Case("vpn: probe incoerente, nessuna scrittura", AnIncoherentProbeWritesNothing);
            Case("vpn: disconnect disattiva e lo stato si rilegge", DisconnectDeactivates);
            Case("vpn: niente valori, niente stato attivo", WithoutValuesNothingIsOn);
            Case("vpn: revert ripristina valori e assenze", RevertRestoresValuesAndAbsences);
            Case("payload: backup prima, scrittura, verifica dopo", PayloadBacksUpBeforeWriting);
            Case("payload: nomi con spazi e nomi che iniziano come un altro", PayloadSplitsNamesWithSpaces);
            Case("payload: deprovision ripristina dal backup", DeprovisionRestoresFromBackup);
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

        private static VpnLink NewLink(FakeRegistry registry, MemoryStateStore backup)
        {
            InteropPlan plan = InteropManifest.Parse(InteropManifest.Default);
            return new VpnLink(registry, NullLogSink.Instance, plan, backup);
        }

        private static void ConnectAddsAndActivates()
        {
            FakeRegistry registry = new FakeRegistry();
            VpnLink link = NewLink(registry, new MemoryStateStore());

            link.Connect();

            T.Eq(VpnLinkState.On.ToString(), link.State.ToString(), "vpn: dopo Connect lo stato e' On");
            T.Eq("interop", registry.Read("HKLM", "SOFTWARE\\TailscaleWP", "VpnProfile").Text,
                 "vpn: la connessione e' stata aggiunta");
            T.Eq("on", registry.Read("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink").Text,
                 "vpn: il collegamento e' attivo");

            // La conferma viene dal registro: un link nuovo, senza memoria, deve
            // rileggere lo stesso stato.
            VpnLink second = NewLink(registry, new MemoryStateStore());
            second.Refresh();
            T.Eq(VpnLinkState.On.ToString(), second.State.ToString(),
                 "vpn: lo stato si rilegge dal registro, non da un flag");
        }

        private static void ConnectTwiceKeepsTheFirstBackup()
        {
            FakeRegistry registry = new FakeRegistry();
            registry.Write("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink", new RegValue(RegValue.Sz, "vecchio"));
            MemoryStateStore backup = new MemoryStateStore();
            VpnLink link = NewLink(registry, backup);

            link.Connect();
            link.Connect();
            link.Revert();

            T.Eq("vecchio", registry.Read("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink").Text,
                 "vpn: il ripristino torna al valore di prima della prima scrittura");
        }

        private static void AnUnconfirmedWriteFails()
        {
            FakeRegistry registry = new FakeRegistry();
            registry.LieOnRead = true;
            VpnLink link = NewLink(registry, new MemoryStateStore());

            link.Connect();

            T.Eq(VpnLinkState.Failed.ToString(), link.State.ToString(),
                 "vpn: una scrittura non confermata e' Failed, non On");
            T.True(registry.Logged.Count > 0, "vpn: il fallimento lascia una riga di registro");
        }

        private static void ALockedPhoneWritesNothing()
        {
            FakeRegistry registry = new FakeRegistry();
            registry.FailWrites = true;
            VpnLink link = NewLink(registry, new MemoryStateStore());

            link.Connect();

            T.Eq(VpnLinkState.Locked.ToString(), link.State.ToString(), "vpn: telefono bloccato, stato Locked");
            T.Eq(0, registry.PlanWrites, "vpn: telefono bloccato, nessun valore del piano scritto");
        }

        private static void AnIncoherentProbeWritesNothing()
        {
            FakeRegistry registry = new FakeRegistry();
            registry.LieOnProbe = true;
            VpnLink link = NewLink(registry, new MemoryStateStore());

            link.Connect();

            T.Eq(VpnLinkState.Unknown.ToString(), link.State.ToString(), "vpn: probe incoerente, stato Unknown");
            T.Eq(0, registry.PlanWrites, "vpn: probe incoerente, nessun valore del piano scritto");
        }

        private static void DisconnectDeactivates()
        {
            FakeRegistry registry = new FakeRegistry();
            VpnLink link = NewLink(registry, new MemoryStateStore());
            link.Connect();

            link.Disconnect();

            T.Eq(VpnLinkState.Off.ToString(), link.State.ToString(), "vpn: dopo Disconnect lo stato e' Off");
            T.Eq("off", registry.Read("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink").Text,
                 "vpn: il valore di disattivazione e' al suo posto");
        }

        private static void WithoutValuesNothingIsOn()
        {
            FakeRegistry registry = new FakeRegistry();
            VpnLink link = NewLink(registry, new MemoryStateStore());

            link.Refresh();

            T.Eq(VpnLinkState.NotConfigured.ToString(), link.State.ToString(),
                 "vpn: registro vuoto, stato NotConfigured");
        }

        private static void RevertRestoresValuesAndAbsences()
        {
            FakeRegistry registry = new FakeRegistry();
            registry.Write("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink", new RegValue(RegValue.Sz, "vecchio"));
            MemoryStateStore backup = new MemoryStateStore();
            VpnLink link = NewLink(registry, backup);

            link.Connect();
            link.Revert();

            T.Eq("vecchio", registry.Read("HKLM", "SOFTWARE\\TailscaleWP", "VpnLink").Text,
                 "vpn: il valore precedente e' tornato");
            RegValue profile;
            T.False(registry.TryRead("HKLM", "SOFTWARE\\TailscaleWP", "VpnProfile", out profile),
                    "vpn: il valore che non c'era e' stato cancellato");
            T.True(backup.Read("vpn.bak.HKLM\\SOFTWARE\\TailscaleWP\\VpnLink") == null,
                   "vpn: il backup si svuota dopo il ripristino");
        }

        private static void PayloadBacksUpBeforeWriting()
        {
            InteropPlan plan = InteropManifest.Parse(InteropManifest.Default);
            string batch = ProvisionPayload.Generate(plan, "20261013-120000");

            int backup = batch.IndexOf("backup dei valori presenti");
            int write = batch.IndexOf("rem --- scrittura ---");
            int verify = batch.IndexOf("verifica finale");

            T.True(backup >= 0 && write > backup && verify > write,
                   "payload: l'ordine e' backup, scrittura, verifica");
            T.True(batch.Contains("^|assente"), "payload: le assenze si registrano nel backup");
            T.True(batch.Contains("/t REG_SZ") && batch.Contains("/f"),
                   "payload: la scrittura usa reg add con il tipo giusto");
            T.True(batch.Contains("/d \"on\""), "payload: il provision attiva il collegamento");
            T.False(batch.Contains("/d \"off\""),
                    "payload: il provision non scrive i valori di disattivazione");
            int writes = batch.Split(new string[] { "reg add " }, StringSplitOptions.None).Length - 1;
            T.Eq(2, writes, "payload: una riga di scrittura per ogni valore distinto");
            T.True(batch.Contains("if not errorlevel 1 goto skip1"),
                   "payload: rieseguire il batch non sovrascrive il backup");
            T.True(batch.Contains("NON crea alcun tunnel"),
                   "payload: il batch dice che il registro non crea il tunnel");
        }

        private static void PayloadSplitsNamesWithSpaces()
        {
            InteropPlan plan = new InteropPlan();
            plan.On.Add(new RegistryEntry("HKLM", "SOFTWARE\\TailscaleWP", "Vpn Link",
                                          new RegValue(RegValue.Sz, "on")));
            plan.On.Add(new RegistryEntry("HKLM", "SOFTWARE\\TailscaleWP", "Vpn",
                                          new RegValue(RegValue.Sz, "on")));
            string batch = ProvisionPayload.Generate(plan, "20261013-120000");

            T.True(batch.Contains("tokens=3,*"),
                   "payload: il tipo del nome con spazi e' la terza colonna");
            T.True(batch.Contains("tokens=2,*"),
                   "payload: il tipo del nome senza spazi e' la seconda colonna");
            T.False(batch.Contains("tokens=1,2*"),
                    "payload: piu' niente cattura spezzata sugli spazi");
            T.True(batch.Contains("sz:%%B"),
                   "payload: il dato si prende tutto d'un pezzo, spazi compresi");
            T.True(batch.Contains("findstr /l /b /c:\"HKLM|SOFTWARE\\TailscaleWP|Vpn|\""),
                   "payload: la guardia del backup e' ancorata al nome esatto");
            T.True(batch.Contains("findstr /l /b /c:\"HKLM|SOFTWARE\\TailscaleWP|Vpn Link|\""),
                   "payload: il nome con spazi ha la sua riga di backup");
        }

        private static void DeprovisionRestoresFromBackup()
        {
            InteropPlan plan = InteropManifest.Parse(InteropManifest.Default);
            string batch = ProvisionPayload.GenerateDeprovision(plan, "20261013-120000");

            T.True(batch.Contains(":restore"), "payload: il ripristino e' un blocco proprio");
            T.True(batch.Contains("reg delete"), "payload: le assenze si cancellano");
            T.True(batch.Contains("backup-20261013-120000.txt"),
                   "payload: il deprovision legge il backup del provision");
            T.True(batch.Contains("interrotto a meta'"),
                   "payload: il deprovision dice che regge anche a un provisioning interrotto");
        }
    }

    /// <summary>
    /// Un registro in memoria, con i difetti che servono a provare i guasti: le
    /// scritture che falliscono (telefono bloccato), le letture che mentono
    /// (probe incoerente, scrittura non confermata). Conta le scritture del piano
    /// separatamente da quelle del probe: "nessuna scrittura" significa proprio
    /// nessuna scrittura fuori dalla chiave di prova.
    /// </summary>
    internal sealed class FakeRegistry : IRegistryApi
    {
        private readonly Dictionary<string, RegValue> _values = new Dictionary<string, RegValue>();
        private readonly object _gate = new object();

        public bool FailWrites;
        public bool LieOnRead;
        public bool LieOnProbe;
        public int PlanWrites;
        public readonly List<string> Logged = new List<string>();

        private static string Key(string hive, string path, string name)
        {
            return hive + "\\" + path + "\\" + name;
        }

        public bool TryRead(string hive, string path, string name, out RegValue value)
        {
            lock (_gate)
            {
                RegValue stored;
                bool found = _values.TryGetValue(Key(hive, path, name), out stored);
                if (!found)
                {
                    value = null;
                    return false;
                }
                if (LieOnProbe && name == InteropProbe.ProbeName)
                {
                    value = new RegValue(stored.Kind, stored.Text + "-alterato");
                    return true;
                }
                if (LieOnRead && name != InteropProbe.ProbeName)
                {
                    value = new RegValue(stored.Kind, stored.Text + "-alterato");
                    return true;
                }
                value = stored;
                return true;
            }
        }

        public void Write(string hive, string path, string name, RegValue value)
        {
            lock (_gate)
            {
                if (FailWrites) throw new UnauthorizedAccessException("registro bloccato");
                _values[Key(hive, path, name)] = value;
                if (name != InteropProbe.ProbeName) PlanWrites++;
                Logged.Add("write " + Key(hive, path, name));
            }
        }

        public void Delete(string hive, string path, string name)
        {
            lock (_gate)
            {
                if (FailWrites) throw new UnauthorizedAccessException("registro bloccato");
                _values.Remove(Key(hive, path, name));
            }
        }

        /// <summary>Legge un valore senza le menzogne: serve a vedere cosa c'e' davvero.</summary>
        public RegValue Read(string hive, string path, string name)
        {
            lock (_gate)
            {
                RegValue stored;
                return _values.TryGetValue(Key(hive, path, name), out stored) ? stored : null;
            }
        }
    }
}