using System;
using System.Collections.Generic;
using Tailscale.Core.Net;
using Tailscale.Core.Net.Ip;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il tunnel: un pacchetto IP verso un indirizzo del tailnet va al peer
    /// giusto, incapsulato nella sua sessione; il datagramma in ingresso si
    /// smonta e il pacchetto finisce al socket layer. Il trasporto e' finto:
    /// il tunnel conosce solo "spedisci datagramma", non DERP o UDP.
    /// </summary>
    public static class TunnelTests
    {
        public static void Run()
        {
            Case("tunnel: instradamento per indirizzo del peer", Routing);
            Case("tunnel: ingresso smontato e consegnato", InboundDelivery);
            Case("tunnel: indirizzo sconosciuto, scarto esplicito", UnknownAddress);
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

        private static TunnelOptions Opts()
        {
            TunnelOptions options = new TunnelOptions();
            options.Clock = delegate { return new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); };
            return options;
        }

        /// <summary>Un trasporto finto: i datagrammi restano in una lista.</summary>
        public sealed class FakeTransport : IPeerTransport
        {
            public List<byte[]> Sent = new List<byte[]>();
            public void Send(byte[] datagram) { Sent.Add(datagram); }
        }

        private static byte[] Key(byte fill)
        {
            byte[] key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = fill;
            return key;
        }

        /// <summary>Due sessioni opposte: a invia con la chiave che b apre.</summary>
        public static void MakePair(out WgSession a, out WgSession b)
        {
            WgSessionKeys aKeys = new WgSessionKeys();
            aKeys.SendKey = Key(0x11);
            aKeys.RecvKey = Key(0x22);
            aKeys.LocalIndex = 0xAAAA1111;
            aKeys.RemoteIndex = 0xBBBB2222;
            WgSessionKeys bKeys = new WgSessionKeys();
            bKeys.SendKey = Key(0x22);
            bKeys.RecvKey = Key(0x11);
            bKeys.LocalIndex = 0xBBBB2222;
            bKeys.RemoteIndex = 0xAAAA1111;
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            a = new WgSession(aKeys, now);
            b = new WgSession(bKeys, now);
        }

        private static byte[] Src = IpV4.Address(100, 64, 0, 1);
        private static byte[] PeerAddr = IpV4.Address(100, 64, 0, 2);

        private static void Routing()
        {
            WgSession ourSession;
            WgSession peerSession;
            MakePair(out ourSession, out peerSession);

            Tunnel tunnel = new Tunnel(Opts());
            FakeTransport transportA = new FakeTransport();
            FakeTransport transportB = new FakeTransport();

            TunnelPeer peerA = new TunnelPeer();
            peerA.Name = "peer-a";
            peerA.Address = IpV4.ToUInt32(PeerAddr);
            peerA.Session = ourSession;
            peerA.Transport = transportA;
            tunnel.AddPeer(peerA);

            TunnelPeer peerB = new TunnelPeer();
            peerB.Name = "peer-b";
            peerB.Address = IpV4.ToUInt32(IpV4.Address(100, 64, 0, 3));
            peerB.Session = ourSession;
            peerB.Transport = transportB;
            tunnel.AddPeer(peerB);

            byte[] packet = IpV4.Build(Src, PeerAddr, 6, new byte[] { 7, 7, 7 });
            tunnel.SendIpPacket(IpV4.ToUInt32(PeerAddr), packet);

            T.Eq(1, transportA.Sent.Count, "un datagramma al peer giusto");
            T.Eq(0, transportB.Sent.Count, "niente all'altro peer");

            // Il datagramma si apre con la sessione del peer.
            byte[] payload = peerSession.Decapsulate(transportA.Sent[0], new DateTime(2026, 1, 1, 12, 0, 5, DateTimeKind.Utc));
            T.Eq(packet, payload, "il pacchetto e' quello");
        }

        private static void InboundDelivery()
        {
            WgSession ourSession;
            WgSession peerSession;
            MakePair(out ourSession, out peerSession);

            Tunnel tunnel = new Tunnel(Opts());
            TunnelPeer peer = new TunnelPeer();
            peer.Name = "peer-a";
            peer.Address = IpV4.ToUInt32(PeerAddr);
            peer.Session = ourSession;
            peer.Transport = new FakeTransport();
            tunnel.AddPeer(peer);

            byte[] delivered = null;
            tunnel.PacketReceived += delegate(byte[] incoming) { delivered = incoming; };

            byte[] packet = IpV4.Build(PeerAddr, Src, 17, new byte[] { 1, 2 });
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            byte[] datagram = peerSession.Encapsulate(packet, now);
            tunnel.OnDatagram(peer, datagram);

            T.Eq(packet, delivered, "il pacchetto arriva al socket layer");
            T.True(peer.IsAlive, "un pacchetto valido: il peer e' vivo");
        }

        private static void UnknownAddress()
        {
            WgSession ourSession;
            WgSession peerSession;
            MakePair(out ourSession, out peerSession);

            Tunnel tunnel = new Tunnel(Opts());
            bool refused = false;
            try
            {
                tunnel.SendIpPacket(0x01020304, IpV4.Build(Src, PeerAddr, 6, new byte[4]));
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            T.True(refused, "nessun peer per quell'indirizzo: errore esplicito");
        }
    }
}
