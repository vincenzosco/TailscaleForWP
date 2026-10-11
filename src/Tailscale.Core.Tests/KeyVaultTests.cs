using System;
using System.Collections.Generic;
using Tailscale.Core;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Due invarianti, uno dei quali e' di sicurezza.
    ///
    /// Il primo: un portachiavi che accetta la scrittura ma restituisce altro in
    /// lettura non deve far perdere la chiave di prima — altrimenti il controllo
    /// danneggia cio' che controlla, e il tailnet si riempie di dispositivi nuovi.
    ///
    /// Il secondo: la chiave macchina non deve finire dove finiscono le
    /// preferenze. Sul telefono quella differenza e' fra un segreto nel portachiavi
    /// e un segreto in un file leggibile, e prima di questo test nessuno la
    /// difendeva.
    /// </summary>
    public static class KeyVaultTests
    {
        public static void Run()
        {
            Case("portachiavi: scrittura e rilettura", AKeySurvivesTheVaultRoundTrip);
            Case("portachiavi rotto: la verifica lo dice e non perde la chiave", AVerifiedVaultKeepsTheOldKey);
            Case("portachiavi sano: la verifica restituisce la chiave di prima", AHealthyVaultKeepsTheOldKey);
            Case("stato: la chiave macchina non finisce nelle impostazioni", TheMachineKeyNeverReachesTheSettings);
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
                Console.WriteLine("FAIL chiavi/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void AKeySurvivesTheVaultRoundTrip()
        {
            List<string> lines = new List<string>();
            MachineKeyStore store = new MachineKeyStore(new MemoryKeyVault(), new DelegateLogSink(lines.Add));
            KeyPair pair = KeyPair.Generate(new TestRandomSource(1));

            T.True(store.Load() == null, "portachiavi: al primo avvio non c'e' nessuna chiave, e non e' un errore");

            store.Save(pair);
            KeyPair back = store.Load();
            T.True(back != null, "portachiavi: la chiave salvata si rilegge");
            T.Eq(T.ToHex(pair.PrivateKey), T.ToHex(back.PrivateKey), "portachiavi: ed e' la stessa chiave");

            store.Clear();
            T.True(store.Load() == null, "portachiavi: dopo la cancellazione non resta niente");
        }

        private static void AVerifiedVaultKeepsTheOldKey()
        {
            List<string> lines = new List<string>();
            KeyPair original = KeyPair.Generate(new TestRandomSource(2));
            ForgetfulKeyVault vault = new ForgetfulKeyVault(original);
            MachineKeyStore store = new MachineKeyStore(vault, new DelegateLogSink(lines.Add));

            T.False(store.Verify(new TestRandomSource(3)),
                    "portachiavi rotto: la verifica fallisce");
            T.True(HasLine(lines, "la chiave scritta non si rilegge identica"),
                   "portachiavi rotto: il registro dice cosa non torna");
            T.Eq(T.ToHex(original.PrivateKey), T.ToHex(store.Load().PrivateKey),
                 "portachiavi rotto: la chiave di prima e' ancora quella leggibile");
        }

        private static void AHealthyVaultKeepsTheOldKey()
        {
            List<string> lines = new List<string>();
            KeyPair original = KeyPair.Generate(new TestRandomSource(4));
            MemoryKeyVault vault = new MemoryKeyVault();
            vault.Save(original);
            MachineKeyStore store = new MachineKeyStore(vault, new DelegateLogSink(lines.Add));

            T.True(store.Verify(new TestRandomSource(5)), "portachiavi sano: la verifica riesce");
            T.Eq(T.ToHex(original.PrivateKey), T.ToHex(store.Load().PrivateKey),
                 "portachiavi sano: la prova non ha lasciato la chiave di prova al posto di quella vera");
            T.True(HasLine(lines, "scrittura e rilettura verificate"),
                   "portachiavi sano: il registro lo dice");
        }

        private static void TheMachineKeyNeverReachesTheSettings()
        {
            RecordingSettings settings = new RecordingSettings();
            List<string> lines = new List<string>();
            VaultBackedStateStore store = new VaultBackedStateStore(settings, new MemoryKeyVault(),
                                                                  new DelegateLogSink(lines.Add));
            KeyPair pair = KeyPair.Generate(new TestRandomSource(6));

            // Prima fase: le impostazioni rifiutano qualunque scrittura. Se la chiave
            // macchina passasse di li', il test fallirebbe con un'eccezione invece di
            // limitarsi a contare: e' la prova piu' diretta che il percorso e' un altro.
            settings.RejectAll = true;
            store.Write(VaultBackedStateStore.MachineKeyName, KeyMaterial.SavePrivate(pair));
            T.Eq(0, settings.Written.Count, "stato: la chiave macchina non tocca le impostazioni");
            T.Eq(T.ToHex(pair.PrivateKey),
                 T.ToHex(KeyMaterial.LoadPrivate(store.Read(VaultBackedStateStore.MachineKeyName)).PrivateKey),
                 "stato: e si rilegge dal portachiavi");

            // Seconda fase: le altre chiavi passano di li', e si rileggono.
            settings.RejectAll = false;
            store.Write("authorized", "1");
            T.True(settings.Written.Contains("authorized"), "stato: le altre chiavi vanno nelle impostazioni");
            T.Eq("1", store.Read("authorized"), "stato: e si rileggono da li'");
            T.False(settings.Written.Contains(VaultBackedStateStore.MachineKeyName),
                    "stato: in tutto il test la chiave macchina non e' mai passata dalle impostazioni");
        }

        private static bool HasLine(List<string> lines, string fragment)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != null && lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>Una sorgente di prova deterministica: le chiavi dei test devono essere sempre le stesse.</summary>
        private sealed class TestRandomSource : IRandomSource
        {
            private uint _state;

            public TestRandomSource(uint seed) { _state = seed == 0 ? 12345u : seed; }

            public void NextBytes(byte[] buffer)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    _state = _state * 1664525u + 1013904223u;
                    buffer[i] = (byte)(_state >> 16);
                }
            }
        }

        /// <summary>
        /// Impostazioni che registrano cosa ricevono, e che all'occorrenza rifiutano
        /// qualsiasi scrittura: serve a dimostrare che la chiave macchina non passa
        /// di li' nemmeno quando sarebbe l'unico posto disponibile.
        /// </summary>
        private sealed class RecordingSettings : IStateStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

            public readonly List<string> Written = new List<string>();
            public bool RejectAll;

            public string Read(string name)
            {
                string value;
                return _values.TryGetValue(name, out value) ? value : null;
            }

            public void Write(string name, string value)
            {
                if (RejectAll) throw new InvalidOperationException("in questo store non si scrive: " + name);
                Written.Add(name);
                _values[name] = value;
            }

            public void Delete(string name) { _values.Remove(name); }
        }

        /// <summary>
        /// Un portachiavi che accetta tutto e non salva niente: la scrittura non
        /// lancia (il sistema "accetta"), ma cio' che si rilegge e' sempre quello di
        /// prima. E' il guasto vero — l'identita' si rigenera a ogni avvio — e il
        /// controllo deve accorgersene **senza** perdere la chiave che c'era.
        /// </summary>
        private sealed class ForgetfulKeyVault : IKeyVault
        {
            private readonly KeyPair _stored;

            public ForgetfulKeyVault(KeyPair initial) { _stored = initial; }

            public void Save(KeyPair pair)
            {
                // Perde la scrittura: nessuna eccezione, nessun salvataggio.
            }

            public KeyPair Load() { return _stored; }

            public void Clear() { }
        }
    }
}
