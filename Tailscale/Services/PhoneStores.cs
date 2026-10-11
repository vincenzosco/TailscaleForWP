using System;
using System.Collections.Generic;
using Tailscale.Core;
using Tailscale.Core.Keys;
using Windows.Security.Credentials;

namespace Tailscale.Services
{
    /// <summary>
    /// Il portachiavi del sistema visto come `IKeyVault`: e' l'unica parte del
    /// progetto che sa di `PasswordVault`.
    ///
    /// La politica — cosa si salva, cosa si rilegge, cosa succede se il sistema
    /// restituisce altro, cosa si cancella — non e' qui: sta in
    /// `src/Tailscale.Core/Util/MachineKeyStore.cs`, dove si prova senza telefono.
    /// Questo file e' rimasto senza decisioni apposta: le decisioni che stanno in un
    /// file che si compila solo per il telefono sono decisioni che non ha mai
    /// verificato nessuno, ed e' esattamente da li' che sono arrivati i guasti di
    /// questa app.
    /// </summary>
    public sealed class PasswordVaultAdapter : IKeyVault
    {
        private const string VaultResource = "TailscaleWP";
        private const string MachineKeyName = "machineKey";

        public void Save(KeyPair pair)
        {
            if (pair == null) throw new ArgumentNullException("pair");

            PasswordVault vault = new PasswordVault();
            RemoveExisting(vault);
            vault.Add(new PasswordCredential(VaultResource, MachineKeyName, KeyMaterial.SavePrivate(pair)));
        }

        public KeyPair Load()
        {
            try
            {
                PasswordVault vault = new PasswordVault();
                PasswordCredential credential = vault.Retrieve(VaultResource, MachineKeyName);
                credential.RetrievePassword();
                return KeyMaterial.LoadPrivate(credential.Password);
            }
            catch (Exception)
            {
                // Primo avvio: la voce non esiste. Non e' un errore e non va segnalato
                // come tale, altrimenti ogni prima partenza sembra un guasto.
                return null;
            }
        }

        public void Clear()
        {
            RemoveExisting(new PasswordVault());
        }

        private static void RemoveExisting(PasswordVault vault)
        {
            try
            {
                IReadOnlyList<PasswordCredential> found = vault.FindAllByResource(VaultResource);
                for (int i = 0; i < found.Count; i++) vault.Remove(found[i]);
            }
            catch (Exception)
            {
                // Nessuna voce: e' il caso normale al primo avvio.
            }
        }
    }

    /// <summary>
    /// Le impostazioni del telefono viste come `IStateStore`: `LocalSettings`, che e'
    /// un file leggibile.
    ///
    /// Per questo la chiave macchina non passa di qui, e la scelta di dove va ogni
    /// chiave la fa `src/Tailscale.Core/Util/VaultBackedStateStore.cs`, con un test
    /// che verifica che il segreto non arrivi mai a questo adattatore.
    /// </summary>
    public sealed class LocalSettingsAdapter : IStateStore
    {
        public string Read(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            object value = Windows.Storage.ApplicationData.Current.LocalSettings.Values[name];
            return value as string;
        }

        public void Write(string name, string value)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (value == null) throw new ArgumentNullException("value");
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[name] = value;
        }

        public void Delete(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(name);
        }
    }
}
