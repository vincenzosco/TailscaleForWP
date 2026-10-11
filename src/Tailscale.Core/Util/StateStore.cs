using System;
using System.Collections.Generic;

namespace Tailscale.Core
{
    /// <summary>
    /// Dove il client tiene quello che deve sopravvivere al riavvio: la chiave
    /// della macchina, la node key e l'URL di login.
    ///
    /// Le firme sono quelle del contratto condiviso (spec §5): sul telefono
    /// l'implementazione e' `LocalSettings` + `PasswordVault`, sulla CLI e' un file
    /// JSON, nei test e' memoria. Il core non sa quale delle tre sta usando.
    /// </summary>
    public interface IStateStore
    {
        /// <summary>Il valore associato a un nome, o null se non c'e'.</summary>
        string Read(string name);

        void Write(string name, string value);

        void Delete(string name);
    }

    /// <summary>
    /// Store in memoria: serve ai test e a chi vuole un client che dimentica tutto
    /// alla chiusura (la CLI senza `--state`).
    /// </summary>
    public sealed class MemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        private readonly object _gate = new object();

        public string Read(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            lock (_gate)
            {
                string value;
                return _values.TryGetValue(name, out value) ? value : null;
            }
        }

        public void Write(string name, string value)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (value == null) throw new ArgumentNullException("value");
            lock (_gate) _values[name] = value;
        }

        public void Delete(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            lock (_gate) _values.Remove(name);
        }

        /// <summary>Quanti valori ci sono: serve ai test per contare le scritture.</summary>
        public int Count { get { lock (_gate) return _values.Count; } }
    }
}
