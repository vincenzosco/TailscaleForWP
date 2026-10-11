using System;
using System.Collections.Generic;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Core.Control
{
    /// <summary>
    /// La netmap accumulata: il nodo stesso piu' i peer.
    ///
    /// Il server manda MapResponse incrementali, quindi qui si applicano uno dopo
    /// l'altro: `Peers` (lista intera) sostituisce, `PeersChanged` aggiorna per ID,
    /// `PeersRemoved` toglie. Un client che trattasse ogni risposta come completa
    /// perderebbe i peer all'inizio, e uno che ignorasse PeersChanged non vedrebbe
    /// piu' nessun cambiamento dopo il primo messaggio.
    /// </summary>
    public sealed class NetworkMap
    {
        private readonly List<NodeInfo> _peers = new List<NodeInfo>();
        private readonly Dictionary<long, int> _indexById = new Dictionary<long, int>();

        public NodeInfo Self { get; private set; }
        public string DnsSuffix { get; private set; }
        public int LastSeq { get; private set; }
        public string Health { get; private set; }

        public NetworkMap()
        {
            DnsSuffix = "";
            Health = "";
        }

        /// <summary>Vero quando il server ha mandato il nostro nodo: siamo autorizzati.</summary>
        public bool IsAuthorized { get { return Self != null; } }

        // Non List.AsReadOnly: il profilo WP8.1 non lo espone.
        public IList<NodeInfo> Peers { get { return _peers; } }

        public void Apply(MapResponse response)
        {
            if (response == null) throw new ArgumentNullException("response");

            if (response.Seq >= LastSeq) LastSeq = response.Seq;
            if (response.Domain != null && response.Domain.Length > 0) DnsSuffix = response.Domain;
            if (response.Health != null && response.Health.Length > 0) Health = response.Health;

            if (response.Self != null) Self = response.Self;

            if (response.Peers.Count > 0) ReplacePeers(response.Peers);
            foreach (NodeInfo changed in response.PeersChanged) Upsert(changed);
            foreach (long removed in response.PeersRemoved) Remove(removed);
        }

        public NodeInfo FindPeer(long id)
        {
            int index;
            return _indexById.TryGetValue(id, out index) ? _peers[index] : null;
        }

        private void ReplacePeers(List<NodeInfo> peers)
        {
            _peers.Clear();
            _indexById.Clear();
            foreach (NodeInfo peer in peers) Upsert(peer);
        }

        private void Upsert(NodeInfo peer)
        {
            if (peer == null) return;

            int index;
            if (_indexById.TryGetValue(peer.ID, out index))
            {
                _peers[index] = peer;
                return;
            }
            _indexById[peer.ID] = _peers.Count;
            _peers.Add(peer);
        }

        private void Remove(long id)
        {
            int index;
            if (!_indexById.TryGetValue(id, out index)) return;

            _peers.RemoveAt(index);
            _indexById.Remove(id);

            // Gli indici successivi scalano di uno.
            for (int i = index; i < _peers.Count; i++)
                _indexById[_peers[i].ID] = i;
        }

        public override string ToString()
        {
            return "self=" + (Self == null ? "<nessuno>" : Self.Hostname)
                 + ", peer=" + _peers.Count
                 + ", dns=" + DnsSuffix;
        }
    }
}
