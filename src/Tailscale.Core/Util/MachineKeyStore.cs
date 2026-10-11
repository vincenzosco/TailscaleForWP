using System;
using Tailscale.Core.Keys;

namespace Tailscale.Core
{
    /// <summary>
    /// La chiave macchina nel portachiavi, con le decisioni che prima stavano nel
    /// file dell'app: si salva **solo la privata**, una voce sola (si sostituisce,
    /// non si accumula), e una lettura che fallisce e' un primo avvio — un'app che
    /// non parte perche' il portachiavi e' storto e' peggio di un dispositivo nuovo
    /// da autorizzare.
    ///
    /// La verifica (`Verify`) e' la ragione per cui questa classe esiste: se il
    /// sistema accetta la scrittura ma restituisce altro in lettura, l'identita' non
    /// sopravvive al riavvio e il tailnet si riempie di dispositivi nuovi. La
    /// verifica lo scopre all'avvio e **rimette la chiave di prima**, perche' un
    /// controllo che danneggia cio' che controlla non e' un controllo.
    /// </summary>
    public sealed class MachineKeyStore
    {
        private readonly IKeyVault _vault;
        private readonly ILogSink _log;

        public MachineKeyStore(IKeyVault vault, ILogSink log)
        {
            if (vault == null) throw new ArgumentNullException("vault");
            _vault = vault;
            _log = log ?? NullLogSink.Instance;
        }

        public void Save(KeyPair pair)
        {
            if (pair == null) throw new ArgumentNullException("pair");
            _vault.Save(pair);
            _log.Log("portachiavi: chiave macchina salvata");
        }

        /// <summary>La chiave salvata, o null al primo avvio.</summary>
        public KeyPair Load()
        {
            return _vault.Load();
        }

        public void Clear()
        {
            _vault.Clear();
            _log.Log("portachiavi: chiave macchina cancellata");
        }

        /// <summary>
        /// Scrive, rilegge e confronta: se il sistema rifiuta la scrittura, meglio
        /// saperlo all'avvio che quando il server respinge la chiave. La chiave di
        /// prima viene rimessa comunque, anche quando la verifica fallisce.
        /// </summary>
        public bool Verify(IRandomSource random)
        {
            if (random == null) throw new ArgumentNullException("random");

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
            if (a == null || b == null) return false;
            byte[] left = a.PrivateKey;
            byte[] right = b.PrivateKey;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }
    }
}
