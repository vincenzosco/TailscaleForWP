using System;
using System.Collections.Generic;
using Tailscale.Core;
using Tailscale.Core.Keys;
using Windows.Security.Credentials;

namespace Tailscale.Services
{
    /// <summary>
    /// La chiave macchina in `PasswordVault`.
    ///
    /// Non e' una preferenza: e' il segreto con cui il client si identifica al
    /// server di controllo, quindi sta nel portachiavi del sistema e non in
    /// `LocalSettings` (che e' un file leggibile). Se non c'e', si genera; se e'
    /// illeggibile, si logga e si genera — un'app che non parte perche' il
    /// portachiavi e' storto e' peggio di un dispositivo nuovo da autorizzare.
    /// </summary>
    public sealed class PhoneKeyStore
    {
        private const string VaultResource = "TailscaleWP";
        private const string MachineKeyName = "machineKey";

        private readonly ILogSink _log;

        public PhoneKeyStore(ILogSink log)
        {
            _log = log ?? NullLogSink.Instance;
        }

        public void Save(KeyPair pair)
        {
            if (pair == null) throw new ArgumentNullException("pair");

            PasswordVault vault = new PasswordVault();
            RemoveExisting(vault);
            vault.Add(new PasswordCredential(VaultResource, MachineKeyName, KeyMaterial.SavePrivate(pair)));
            _log.Log("portachiavi: chiave macchina salvata");
        }

        /// <summary>La chiave salvata, o null al primo avvio.</summary>
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
                // Primo avvio: la voce non esiste. Non e' un errore e non va loggato
                // come tale, altrimenti ogni prima partenza sembra un guasto.
                return null;
            }
        }

        public void Clear()
        {
            try
            {
                RemoveExisting(new PasswordVault());
                _log.Log("portachiavi: chiave macchina cancellata");
            }
            catch (Exception ex)
            {
                _log.Log("portachiavi: cancellazione fallita: " + ex.Message);
            }
        }

        /// <summary>
        /// Scrive e rilegge per controllare che il portachiavi funzioni: se il
        /// sistema rifiuta la scrittura, meglio saperlo all'avvio che quando il
        /// server respinge la chiave.
        /// </summary>
        public bool Verify(IRandomSource random)
        {
            try
            {
                KeyPair probe = KeyPair.Generate(random);
                KeyPair current = Load();
                Save(probe);
                KeyPair back = Load();
                if (back == null || !SameKey(probe, back))
                {
                    if (current != null) Save(current);
                    _log.Log("portachiavi: la chiave scritta non si rilegge identica");
                    return false;
                }
                if (current != null) Save(current); else Clear();
                _log.Log("portachiavi: scrittura e rilettura verificate");
                return true;
            }
            catch (Exception ex)
            {
                _log.Log("portachiavi: verifica fallita (" + ex.GetType().Name + "): " + ex.Message);
                return false;
            }
        }

        private static bool SameKey(KeyPair a, KeyPair b)
        {
            byte[] left = a.PrivateKey;
            byte[] right = b.PrivateKey;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
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
    /// Lo stato del client sul telefono: tutto in `LocalSettings`, **tranne** la
    /// chiave macchina, che va nel portachiavi.
    ///
    /// Il motore vede un solo `IStateStore` e non sa della distinzione: cosi' il
    /// codice condiviso non ha un ramo apposta per il telefono, e l'errore di
    /// mettere un segreto in un posto leggibile non e' possibile.
    /// </summary>
    public sealed class PhoneStateStore : IStateStore
    {
        private const string MachineKeyName = "machineKey";

        private readonly ILogSink _log;
        private readonly PhoneKeyStore _keys;

        public PhoneStateStore(ILogSink log)
        {
            _log = log ?? NullLogSink.Instance;
            _keys = new PhoneKeyStore(_log);
        }

        public PhoneKeyStore Keys { get { return _keys; } }

        public string Read(string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            if (name == MachineKeyName)
            {
                KeyPair pair = _keys.Load();
                return pair == null ? null : KeyMaterial.SavePrivate(pair);
            }

            object value = Windows.Storage.ApplicationData.Current.LocalSettings.Values[name];
            return value as string;
        }

        public void Write(string name, string value)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (value == null) throw new ArgumentNullException("value");

            if (name == MachineKeyName)
            {
                _keys.Save(KeyMaterial.LoadPrivate(value));
                return;
            }

            Windows.Storage.ApplicationData.Current.LocalSettings.Values[name] = value;
        }

        public void Delete(string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            if (name == MachineKeyName)
            {
                _keys.Clear();
                return;
            }

            Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(name);
        }
    }
}
