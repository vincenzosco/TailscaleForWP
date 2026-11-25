using System;
using System.Collections.Generic;
using Tailscale.Core.Net;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// L'inoltro UDP grezzo del mini-stack: un datagramma verso un
    /// indirizzo del tailnet diventa un pacchetto UDP/IP, le risposte
    /// tornano alla porta locale giusta. Il checksum UDP si calcola in
    /// invio e si accetta a zero in ricezione (RFC 768).
    /// </summary>
    public static class UdpForwarderTests
    {
        public static void Run()
        {
            Case("udp: invio come datagramma verso il tailnet", SendToPeer);
            Case("udp: risposta consegnata alla porta giusta", ReplyDelivered);
            Case("udp: checksum in invio, zero ammesso in ricezione", ChecksumRules);
        }

        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private const uint OurIp = 0x64400001;
        private const uint PeerIp = 0x64400002;

        private sealed class Collector
        {
            public List<uint> Addresses = new List<uint>();
            public List<byte[]> Packets = new List<byte[]>();
            public void Send(uint address, byte[] packet)
            {
                Addresses.Add(address);
                Packets.Add(packet);
            }
        }

        private static UdpForwarder MakeForwarder(Collector collector)
        {
            UdpForwarderOptions options = new UdpForwarderOptions();
            options.SourceIp = OurIp;
            options.SendIpPacket = collector.Send;
            return new UdpForwarder(options);
        }

        private static void SendToPeer()
        {
            Collector collector = new Collector();
            UdpForwarder forwarder = MakeForwarder(collector);

            forwarder.SendTo(PeerIp, 53, new byte[] { 1, 2, 3 });

            T.Eq(1, collector.Packets.Count, "un pacchetto spedito");
            T.Eq((int)PeerIp, (int)collector.Addresses[0], "all'indirizzo giusto");

            IpV4Header header;
            byte[] udp;
            T.True(IpV4.Parse(collector.Packets[0], out header, out udp), "il pacchetto si apre");
            if (header == null) return;
            T.Eq(17, (int)header.Protocol, "protocollo UDP");

            UdpSegment parsed;
            T.True(UdpSegment.TryParse(udp, IpV4.ToUInt32(header.Src), IpV4.ToUInt32(header.Dst), out parsed), "l'UDP si apre");
            if (parsed == null) return;
            T.Eq(53, (int)parsed.DstPort, "porta di destinazione");
            T.Eq(new byte[] { 1, 2, 3 }, parsed.Payload, "payload identico");
            T.True(parsed.SrcPort >= 40000, "porta sorgente dal nostro range");
        }

        private static void ReplyDelivered()
        {
            Collector collector = new Collector();
            UdpForwarder forwarder = MakeForwarder(collector);
            forwarder.SendTo(PeerIp, 53, new byte[] { 9 });

            IpV4Header header;
            byte[] udp;
            IpV4.Parse(collector.Packets[0], out header, out udp);
            UdpSegment first;
            UdpSegment.TryParse(udp, IpV4.ToUInt32(header.Src), IpV4.ToUInt32(header.Dst), out first);
            if (first == null) return;

            ushort localPort = first.SrcPort;
            UdpSegment reply = new UdpSegment();
            reply.SrcPort = 53;
            reply.DstPort = localPort;
            reply.Payload = new byte[] { 0xAA, 0xBB };
            byte[] replyUdp = UdpSegment.Encode(reply, PeerIp, OurIp);
            byte[] replyIp = IpV4.Build(IpV4.ToBytes(PeerIp), IpV4.ToBytes(OurIp), 17, replyUdp);

            byte[] delivered = null;
            uint fromAddress = 0;
            forwarder.DatagramReceived += delegate(uint address, byte[] payload)
            {
                fromAddress = address;
                delivered = payload;
            };
            forwarder.OnIpPacket(replyIp);

            T.Eq((int)PeerIp, (int)fromAddress, "dal peer giusto");
            T.Eq(new byte[] { 0xAA, 0xBB }, delivered, "payload consegnato");
        }

        private static void ChecksumRules()
        {
            // In invio il checksum si calcola sempre.
            UdpSegment segment = new UdpSegment();
            segment.SrcPort = 40001;
            segment.DstPort = 53;
            segment.Payload = new byte[] { 1, 2 };
            byte[] encoded = UdpSegment.Encode(segment, OurIp, PeerIp);
            T.True((encoded[6] | encoded[7]) != 0, "in invio il checksum c'e'");

            // In ricezione il campo a zero si accette.
            encoded[6] = 0;
            encoded[7] = 0;
            UdpSegment parsed;
            T.True(UdpSegment.TryParse(encoded, OurIp, PeerIp, out parsed), "checksum a zero: si accetta");

            // Uno sbagliato si scarta.
            byte[] broken = UdpSegment.Encode(segment, OurIp, PeerIp);
            broken[5] ^= 0x01;
            T.False(UdpSegment.TryParse(broken, OurIp, PeerIp, out parsed), "checksum rotto: si scarta");
        }
    }
}
