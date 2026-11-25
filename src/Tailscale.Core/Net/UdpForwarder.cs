using System;
using System.Collections.Generic;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Net
{
    /// <summary>Le dipendenze dell'inoltro UDP, tutte iniettate.</summary>
    public sealed class UdpForwarderOptions
    {
        /// <summary>L'indirizzo IPv4 del tailnet di questo terminatore.</summary>
        public uint SourceIp;

        /// <summary>Per spedire il pacchetto IP verso il tunnel.</summary>
        public Action<uint, byte[]> SendIpPacket;
    }

    /// <summary>
    /// L'inoltro UDP grezzo del mini-stack: un datagramma verso un
    /// indirizzo del tailnet diventa un pacchetto UDP/IP; le risposte per le
    /// nostre porte locali tornano all'applicazione con l'indirizzo del
    /// mittente. Niente socket nel core: sono pacchetti, come per il TCP.
    /// </summary>
    public sealed class UdpForwarder
    {
        private readonly object _gate = new object();
        private readonly UdpForwarderOptions _options;
        private readonly Dictionary<ushort, uint> _endpoints = new Dictionary<ushort, uint>();
        private int _nextPort = 40000;

        /// <summary>Consegna all'applicazione: (indirizzo del mittente, payload).</summary>
        public event Action<uint, byte[]> DatagramReceived;

        public UdpForwarder(UdpForwarderOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.SendIpPacket == null) throw new ArgumentException("serve il canale verso il tunnel", "options");
            _options = options;
        }

        /// <summary>Spedisce un datagramma e restituisce la porta locale usata.</summary>
        public ushort SendTo(uint dstAddress, ushort dstPort, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            lock (_gate)
            {
                ushort localPort = (ushort)_nextPort;
                _nextPort = 40000 + ((_nextPort - 39999) % 20000);
                _endpoints[localPort] = dstAddress;

                UdpSegment segment = new UdpSegment();
                segment.SrcPort = localPort;
                segment.DstPort = dstPort;
                segment.Payload = payload;
                byte[] udp = UdpSegment.Encode(segment, _options.SourceIp, dstAddress);
                byte[] ip = IpV4.Build(IpV4.ToBytes(_options.SourceIp), IpV4.ToBytes(dstAddress), 17, udp);
                _options.SendIpPacket(dstAddress, ip);
                return localPort;
            }
        }

        /// <summary>
        /// Il tunnel consegna qui i pacchetti. Quelli che non sono risposte
        /// per una porta locale aperta si scartano.
        /// </summary>
        public void OnIpPacket(byte[] ipPacket)
        {
            Action<uint, byte[]> handler;
            uint fromAddress;
            byte[] payload;
            lock (_gate)
            {
                IpV4Header header;
                byte[] udp;
                if (!IpV4.Parse(ipPacket, out header, out udp)) return;
                if (header.Protocol != 17) return;
                if (IpV4.ToUInt32(header.Dst) != _options.SourceIp) return;

                UdpSegment segment;
                if (!UdpSegment.TryParse(udp, IpV4.ToUInt32(header.Src), IpV4.ToUInt32(header.Dst), out segment)) return;
                if (segment == null) return;
                if (!_endpoints.ContainsKey(segment.DstPort)) return;

                fromAddress = IpV4.ToUInt32(header.Src);
                payload = segment.Payload;
                handler = DatagramReceived;
            }
            if (handler != null) handler(fromAddress, payload);
        }
    }
}
