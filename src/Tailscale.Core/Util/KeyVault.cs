using Tailscale.Core.Keys;

namespace Tailscale.Core
{
    /// <summary>
    /// Il portachiavi del sistema, visto da fuori.
    ///
    /// Esiste per una ragione precisa: la chiave macchina e' l'unico segreto che
    /// l'app deve custodire, e sul telefono va in `PasswordVault`, non in
    /// `LocalSettings` (che e' un file leggibile). Con questa interfaccia la
    /// politica - cosa si salva, cosa si rilegge, cosa si cancella - sta nel core e
    /// si prova senza telefono, mentre l'adattatore WinRT resta una chiamata e
    /// basta.
    /// </summary>
    public interface IKeyVault
    {
        void Save(KeyPair pair);

        /// <summary>La chiave salvata, o null quando non c'e' (primo avvio).</summary>
        KeyPair Load();

        void Clear();
    }

    /// <summary>Portachiavi in memoria: per i test e per chi non vuole persistenza.</summary>
    public sealed class MemoryKeyVault : IKeyVault
    {
        private readonly object _gate = new object();
        private KeyPair _key;

        public void Save(KeyPair pair)
        {
            if (pair == null) throw new System.ArgumentNullException("pair");
            lock (_gate) _key = pair;
        }

        public KeyPair Load()
        {
            lock (_gate) return _key;
        }

        public void Clear()
        {
            lock (_gate) _key = null;
        }
    }
}
