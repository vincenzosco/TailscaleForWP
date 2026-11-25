using System;
using System.Collections.Generic;
using Tailscale.Core.Net.Ip;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Net
{
    /// <summary>Un peer del tailnet cosi' come lo vede il tunnel.</summary>
    public sealed class TunnelPeer
    {
        public string Name;

        /// <summary>L'indirizzo IPv4 del tailnet: la chiave di instradamento.</summary>
        public uint Address;

        /// <summary>La NodeKey del peer (diagnostica).</summary>
        public byte[] PublicKey;

        /// <summary>La sessione di trasporto corrente (cambia col rekey).</summary>
        public WgSession Session;

        /// <summary>Dove partono i datagrammi per questo peer.</summary>
        public IPeerTransport Transport;

        /// <summary>Vero quando arrivano pacchetti validi entro la soglia.</summary>
        public bool IsAlive;

        internal DateTime LastValidAt;
        internal DateTime LastSentAt;
    }

    /// <summary>Le dipendenze del tunnel, tutte iniettate.</summary>
    public sealed class TunnelOptions
    {
        /// <summary>L'orologio (iniettato): keepalive e vita dei peer passano da qui.</summary>
        public Func<DateTime> Clock;

        /// <summary>Secondi di silenzio in invio prima del keepalive (10 s).</summary>
        public int KeepaliveSeconds = 10;

        /// <summary>Secondi senza pacchetti validi per dare un peer per non vivo.</summary>
        public int UnreachableSeconds = 5;
    }

    /// <summary>
    /// Il tunnel WireGuard userspace: instrada i pacchetti IP verso il peer
    /// giusto incapsulandoli nella sua sessione, smonta i datagrammi in
    /// ingresso e li consegna al socket layer, manda un keepalive dopo 10 s
    /// di silenzio e segnala i peer non piu' raggiungibili.
    ///
    /// Il tempo arriva dall'orologio iniettato: il core non lo legge.
    /// </summary>
    public sealed class Tunnel
    {
        private readonly object _gate = new object();
        private readonly TunnelOptions _options;
        private readonly Dictionary<uint, TunnelPeer> _peers = new Dictionary<uint, TunnelPeer>();

        /// <summary>Il socket layer si registra qui per i pacchetti decrittati.</summary>
        public event Action<byte[]> PacketReceived;

        public Tunnel(TunnelOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.Clock == null) throw new ArgumentException("serve un orologio", "options");
            _options = options;
        }

        public void AddPeer(TunnelPeer peer)
        {
            if (peer == null) throw new ArgumentNullException("peer");
            if (peer.Session == null) throw new ArgumentException("serve una sessione", "peer");
            if (peer.Transport == null) throw new ArgumentException("serve un trasporto", "peer");
            lock (_gate)
            {
                _peers[peer.Address] = peer;
                peer.LastSentAt = _options.Clock();
                peer.LastValidAt = DateTime.MinValue;
            }
        }

        public TunnelPeer FindPeerByAddress(uint address)
        {
            lock (_gate)
            {
                TunnelPeer peer;
                return _peers.TryGetValue(address, out peer) ? peer : null;
            }
        }

        /// <summary>
        /// Incapsula il pacchetto e lo spedisce sul trasporto del peer.
        /// InvalidOperationException se nessun peer serve quell'indirizzo.
        /// </summary>
        public void SendIpPacket(uint dstAddress, byte[] ipPacket)
        {
            if (ipPacket == null) throw new ArgumentNullException("ipPacket");
            lock (_gate)
            {
                TunnelPeer peer;
                if (!_peers.TryGetValue(dstAddress, out peer))
                    throw new InvalidOperationException("nessun peer per l'indirizzo "
                                                        + IpV4.ToBytes(dstAddress)[0] + "."
                                                        + IpV4.ToBytes(dstAddress)[1] + "."
                                                        + IpV4.ToBytes(dstAddress)[2] + "."
                                                        + IpV4.ToBytes(dstAddress)[3]);
                DateTime now = _options.Clock();
                byte[] datagram = peer.Session.Encapsulate(ipPacket, now);
                peer.Transport.Send(datagram);
                peer.LastSentAt = now;
            }
        }

        /// <summary>
        /// Il trasporto consegna qui i datagrammi. Quelli che non si aprono o
        /// non sono pacchetti validi si scartano e non tengono vivo il peer.
        /// </summary>
        public void OnDatagram(TunnelPeer peer, byte[] datagram)
        {
            if (peer == null) throw new ArgumentNullException("peer");
            Action<byte[]> handler;
            byte[] packet;
            lock (_gate)
            {
                packet = peer.Session.Decapsulate(datagram, _options.Clock());
                if (packet == null) return;
                IpV4Header header;
                byte[] payload;
                if (!IpV4.Parse(packet, out header, out payload)) return;

                DateTime now = _options.Clock();
                peer.LastValidAt = now;
                peer.IsAlive = true;
                handler = PacketReceived;
            }
            if (handler != null) handler(packet);
        }

        /// <summary>
        /// Pompa i timer: keepalive per i peer in silenzio e vita dei peer.
        /// Il proprietario lo chiama con la cadenza che vuole.
        /// </summary>
        public void Tick()
        {
            lock (_gate)
            {
                DateTime now = _options.Clock();
                foreach (TunnelPeer peer in _peers.Values)
                {
                    if (peer.IsAlive
                        && (now - peer.LastValidAt).TotalSeconds > _options.UnreachableSeconds)
                    {
                        peer.IsAlive = false;
                    }
                    if ((now - peer.LastSentAt).TotalSeconds >= _options.KeepaliveSeconds)
                    {
                        byte[] keepalive = peer.Session.Encapsulate(new byte[0], now);
                        peer.Transport.Send(keepalive);
                        peer.LastSentAt = now;
                    }
                }
            }
        }
    }
}
