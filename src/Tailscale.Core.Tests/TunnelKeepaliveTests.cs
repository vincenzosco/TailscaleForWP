using System;
using Tailscale.Core.Net;
using Tailscale.Core.Net.Ip;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il keepalive e il segnale di vita del tunnel: dopo 10 s di silenzio
    /// parte un keepalive (un messaggio di trasporto vuoto), e un peer senza
    /// pacchetti validi per la soglia configurata smette di essere "vivo"
    /// nella UI.
    /// </summary>
    public static class TunnelKeepaliveTests
    {
        public static void Run()
        {
            Case("tunnel: keepalive dopo 10 s di silenzio", KeepaliveAfterSilence);
            Case("tunnel: nessun keepalive se c'e' traffico", NoKeepaliveWithTraffic);
            Case("tunnel: peer non vivo dopo la soglia", UnreachableAfterThreshold);
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

        private sealed class MutableClock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            public void Advance(int ms) { Now = Now.AddMilliseconds(ms); }
        }

        private static void KeepaliveAfterSilence()
        {
            WgSession ourSession;
            WgSession peerSession;
            TunnelTests.MakePair(out ourSession, out peerSession);

            MutableClock clock = new MutableClock();
            TunnelOptions options = new TunnelOptions();
            options.Clock = delegate { return clock.Now; };
            Tunnel tunnel = new Tunnel(options);

            TunnelTests.FakeTransport transport = new TunnelTests.FakeTransport();
            TunnelPeer peer = new TunnelPeer();
            peer.Name = "peer-a";
            peer.Address = IpV4.ToUInt32(IpV4.Address(100, 64, 0, 2));
            peer.Session = ourSession;
            peer.Transport = transport;
            tunnel.AddPeer(peer);

            clock.Advance(9000);
            tunnel.Tick();
            T.Eq(0, transport.Sent.Count, "a 9 s ancora niente");

            clock.Advance(1500);
            tunnel.Tick();
            T.Eq(1, transport.Sent.Count, "a 10,5 s il keepalive parte");

            byte[] payload = peerSession.Decapsulate(transport.Sent[0], clock.Now);
            T.Eq(0, payload.Length, "il keepalive e' un messaggio vuoto");
        }

        private static void NoKeepaliveWithTraffic()
        {
            WgSession ourSession;
            WgSession peerSession;
            TunnelTests.MakePair(out ourSession, out peerSession);

            MutableClock clock = new MutableClock();
            TunnelOptions options = new TunnelOptions();
            options.Clock = delegate { return clock.Now; };
            Tunnel tunnel = new Tunnel(options);

            TunnelTests.FakeTransport transport = new TunnelTests.FakeTransport();
            TunnelPeer peer = new TunnelPeer();
            peer.Name = "peer-a";
            peer.Address = IpV4.ToUInt32(IpV4.Address(100, 64, 0, 2));
            peer.Session = ourSession;
            peer.Transport = transport;
            tunnel.AddPeer(peer);

            byte[] packet = IpV4.Build(IpV4.Address(100, 64, 0, 1), IpV4.Address(100, 64, 0, 2), 6, new byte[4]);
            clock.Advance(8000);
            tunnel.SendIpPacket(peer.Address, packet);   // traffico: il timer riparte
            clock.Advance(8000);
            tunnel.Tick();
            T.Eq(1, transport.Sent.Count, "solo il pacchetto di dati, niente keepalive");

            clock.Advance(3000);
            tunnel.Tick();
            T.Eq(2, transport.Sent.Count, "dopo altri 3 s di silenzio il keepalive parte");
        }

        private static void UnreachableAfterThreshold()
        {
            WgSession ourSession;
            WgSession peerSession;
            TunnelTests.MakePair(out ourSession, out peerSession);

            MutableClock clock = new MutableClock();
            TunnelOptions options = new TunnelOptions();
            options.Clock = delegate { return clock.Now; };
            options.UnreachableSeconds = 5;
            Tunnel tunnel = new Tunnel(options);

            TunnelPeer peer = new TunnelPeer();
            peer.Name = "peer-a";
            peer.Address = IpV4.ToUInt32(IpV4.Address(100, 64, 0, 2));
            peer.Session = ourSession;
            peer.Transport = new TunnelTests.FakeTransport();
            tunnel.AddPeer(peer);

            DateTime now = clock.Now;
            byte[] packet = IpV4.Build(IpV4.Address(100, 64, 0, 2), IpV4.Address(100, 64, 0, 1), 6, new byte[4]);
            tunnel.OnDatagram(peer, peerSession.Encapsulate(packet, now));
            T.True(peer.IsAlive, "appena arrivato un pacchetto valido: vivo");

            clock.Advance(4000);
            tunnel.Tick();
            T.True(peer.IsAlive, "a 4 s e' ancora vivo");

            clock.Advance(1500);
            tunnel.Tick();
            T.False(peer.IsAlive, "a 5,5 s non e' piu' vivo");

            tunnel.OnDatagram(peer, peerSession.Encapsulate(packet, clock.Now));
            T.True(peer.IsAlive, "un altro pacchetto valido lo riporta vivo");
        }
    }
}
