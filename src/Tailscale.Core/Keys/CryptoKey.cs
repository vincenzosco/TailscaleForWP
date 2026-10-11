using System;

namespace Tailscale.Core.Keys
{
    /// <summary>
    /// Chiave pubblica a 32 byte con un prefisso testuale che ne fissa il tipo.
    ///
    /// I tipi derivati (NodeKey, MachineKey, DiscoKey) non sono intercambiabili:
    /// il compilatore impedisce di usare un NodeKey dove serve un MachineKey, che
    /// e' esattamente il tipo di errore che i prefissi distinguono sulla rete.
    /// </summary>
    public abstract class CryptoKey : IEquatable<CryptoKey>
    {
        private readonly byte[] _bytes;

        protected CryptoKey(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length != 32)
                throw new ArgumentException("una chiave ha 32 byte, ricevuti " + bytes.Length, "bytes");
            _bytes = (byte[])bytes.Clone();
        }

        /// <summary>Il prefisso testuale, ad esempio "nodekey:".</summary>
        protected abstract string Prefix { get; }

        /// <summary>I 32 byte. E' una copia: modificarla non cambia la chiave.</summary>
        public byte[] Bytes
        {
            get { return (byte[])_bytes.Clone(); }
        }

        public override string ToString()
        {
            return Prefix + Hex.Encode(_bytes);
        }

        public bool Equals(CryptoKey other)
        {
            if (other == null) return false;
            if (GetType() != other.GetType()) return false;
            for (int i = 0; i < 32; i++)
                if (_bytes[i] != other._bytes[i]) return false;
            return true;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as CryptoKey);
        }

        public override int GetHashCode()
        {
            int hash = GetType().Name.GetHashCode();
            for (int i = 0; i < 32; i++)
                hash = unchecked(hash * 31 + _bytes[i]);
            return hash;
        }

        /// <summary>
        /// Interpreta "prefisso + 64 cifre esadecimali". Accetta le maiuscole,
        /// rifiuta prefisso sbagliato, lunghezza sbagliata e caratteri non esadecimali.
        /// </summary>
        protected static byte[] ParseBytes(string text, string expectedPrefix)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (!text.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new FormatException("manca il prefisso \"" + expectedPrefix + "\": " + Truncate(text));

            string hex = text.Substring(expectedPrefix.Length);
            if (hex.Length != 64)
                throw new FormatException("attese 64 cifre esadecimali, ricevute " + hex.Length + ": " + Truncate(text));

            return Hex.Decode(hex);
        }

        private static string Truncate(string text)
        {
            return text.Length <= 24 ? text : text.Substring(0, 24) + "...";
        }
    }

    /// <summary>
    /// Sorgente di byte casuali, iniettata invece che letta da una API di
    /// piattaforma.
    ///
    /// Motivo: il profilo WP8.1 non espone System.Security.Cryptography (ne'
    /// RandomNumberGenerator), mentre il desktop si'; nessuna singola API esiste
    /// su entrambi i target senza una direttiva di compilazione condizionale,
    /// quindi la sorgente la fornisce chi ospita il core (l'app del telefono usa
    /// CryptographicBuffer, la CLI usa RNGCryptoServiceProvider).
    /// </summary>
    public interface IRandomSource
    {
        void NextBytes(byte[] buffer);
    }
}
