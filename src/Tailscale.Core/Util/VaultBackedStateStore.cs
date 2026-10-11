using System;
using Tailscale.Core.Keys;

namespace Tailscale.Core
{
    /// <summary>
    /// Lo stato del client diviso in due posti: la chiave macchina nel portachiavi,
    /// tutto il resto nelle impostazioni.
    ///
    /// Il motore vede un solo `IStateStore` e non sa della distinzione: cosi' il
    /// codice condiviso non ha un ramo apposta per il telefono, e l'errore di
    /// mettere un segreto in un posto leggibile non e' possibile. Non e' una
    /// preferenza, e' la differenza fra un segreto custodito e un segreto in un
    /// file: il test lo fissa.
    /// </summary>
    public sealed class VaultBackedStateStore : IStateStore
    {
        /// <summary>Il nome con cui il motore chiede la chiave macchina.</summary>
        public const string MachineKeyName = "machineKey";

        private readonly IStateStore _settings;
        private readonly MachineKeyStore _keys;

        public VaultBackedStateStore(IStateStore settings, IKeyVault vault, ILogSink log)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _keys = new MachineKeyStore(vault, log);
        }

        /// <summary>Il portachiavi, per la verifica all'avvio.</summary>
        public MachineKeyStore Keys { get { return _keys; } }

        public string Read(string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            if (name == MachineKeyName)
            {
                KeyPair pair = _keys.Load();
                return pair == null ? null : KeyMaterial.SavePrivate(pair);
            }

            return _settings.Read(name);
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

            _settings.Write(name, value);
        }

        public void Delete(string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            if (name == MachineKeyName)
            {
                _keys.Clear();
                return;
            }

            _settings.Delete(name);
        }
    }
}
