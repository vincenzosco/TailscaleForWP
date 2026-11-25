using System;
using System.Collections.Generic;
using Tailscale.Core.Keys;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// La macchina a stati TCP del client: SYN/SYN-ACK/ACK porta a
    /// Established, RST chiude con errore in ogni stato, un ACK per dati gia'
    /// confermati non fa avanzare la sequenza, FIN chiude il flusso in
    /// lettura (Read ritorna 0), i segmenti malformati non cambiano stato.
    ///
    /// Il "server" qui e' scritto a mano: crafta segmenti e li butta dentro
    /// OnIpPacket, come farebbe il tunnel.
    /// </summary>
    public static class TcpStateTests
    {
        public static void Run()
        {
            Case("tcp: handshake porta a Established", Handshake);
            Case("tcp: RST chiude con errore", ResetCloses);
            Case("tcp: ack duplicato non fa avanzare", DuplicateAck);
            Case("tcp: FIN chiude il flusso in lettura", FinEof);
            Case("tcp: segmento malformato non cambia stato", MalformedIgnored);
            Case("tcp: dati nei due sensi con parziali", DataRoundTrip);
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

        private const uint ClientIp = 0x64400001;
        private const uint ServerIp = 0x64400002;
        private const ushort ServerPort = 80;

        private sealed class MutableClock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            public void Advance(int ms) { Now = Now.AddMilliseconds(ms); }
        }

        private sealed class SequenceRandom : IRandomSource
        {
            private int _step;
            public void NextBytes(byte[] buffer)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(_step++);
            }
        }

        /// <summary>Il server TCP di test: risponde e parla per conto proprio.</summary>
        private sealed class TestServer
        {
            public const uint Isn = 5000;
            public uint NextSeq = Isn + 1;
            public List<byte[]> ReceivedData = new List<byte[]>();
            private TcpConnection _client;

            public void Wire(TcpConnection client) { _client = client; }

            public void OnClientPacket(byte[] ipPacket)
            {
                IpV4Header header;
                byte[] tcpBytes;
                if (!IpV4.Parse(ipPacket, out header, out tcpBytes)) return;
                TcpSegment seg;
                if (!TcpSegment.TryParse(tcpBytes, IpV4.ToUInt32(header.Src), IpV4.ToUInt32(header.Dst), out seg)) return;
                if (seg == null) return;

                if ((seg.Flags & TcpSegment.FlagSyn) != 0)
                {
                    TcpSegment reply = new TcpSegment();
                    reply.SrcPort = seg.DstPort;
                    reply.DstPort = seg.SrcPort;
                    reply.Seq = Isn;
                    reply.Ack = seg.Seq + 1;
                    reply.Flags = TcpSegment.FlagSyn | TcpSegment.FlagAck;
                    reply.Window = 65535;
                    Deliver(reply);
                    return;
                }
                if ((seg.Flags & TcpSegment.FlagRst) != 0) return;
                if (seg.Payload.Length > 0) ReceivedData.Add(seg.Payload);
            }

            public void SendToClient(byte[] data)
            {
                TcpSegment seg = NewSegment();
                seg.Flags = TcpSegment.FlagAck | TcpSegment.FlagPsh;
                seg.Payload = data;
                NextSeq += (uint)data.Length;
                Deliver(seg);
            }

            public void AckToClient(uint ack)
            {
                TcpSegment seg = NewSegment();
                seg.Flags = TcpSegment.FlagAck;
                seg.Ack = ack;
                Deliver(seg);
            }

            public void FinToClient()
            {
                TcpSegment seg = NewSegment();
                seg.Flags = TcpSegment.FlagAck | TcpSegment.FlagFin;
                seg.Ack = 0xFFFFFFFF; // non importa: l'ack valido non va all'indietro
                NextSeq += 1;
                Deliver(seg);
            }

            public void RstToClient()
            {
                TcpSegment seg = NewSegment();
                seg.Flags = TcpSegment.FlagRst | TcpSegment.FlagAck;
                Deliver(seg);
            }

            public void GarbageToClient()
            {
                _client.OnIpPacket(new byte[] { 0x45, 0, 0, 20, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 });
            }

            private TcpSegment NewSegment()
            {
                TcpSegment seg = new TcpSegment();
                seg.SrcPort = ServerPort;
                seg.Seq = NextSeq;
                seg.Window = 65535;
                return seg;
            }

            private void Deliver(TcpSegment seg)
            {
                seg.DstPort = ClientPort;
                byte[] tcp = TcpSegment.Encode(seg, ServerIp, ClientIp);
                _client.OnIpPacket(IpV4.Build(IpV4.ToBytes(ServerIp), IpV4.ToBytes(ClientIp), 6, tcp));
            }
        }

        /// <summary>La porta sorgente che SequenceRandom produce: 40000 + ((0<<8|1) % 20000) = 40001.</summary>
        private const ushort ClientPort = 40001;

        private static TcpConnection MakeClient(TestServer server, MutableClock clock, out Action closed)
        {
            bool wasClosed = false;
            TcpConnectionOptions options = new TcpConnectionOptions();
            options.SourceIp = ClientIp;
            options.Random = new SequenceRandom();
            options.Sleep = delegate(int ms) { clock.Advance(ms); };
            options.Clock = delegate { return clock.Now; };
            options.SendIpPacket = server.OnClientPacket;
            TcpConnection client = new TcpConnection(options);
            client.Closed += delegate { wasClosed = true; };
            server.Wire(client);
            closed = delegate { T.True(wasClosed, "evento Closed scattato"); };
            return client;
        }

        private static void Handshake()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);

            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));
            T.True(client.IsConnected, "handshake completato");
            T.False(client.IsClosed, "non chiusa");
        }

        private static void ResetCloses()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);
            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));

            server.RstToClient();
            T.True(client.IsClosed, "RST: chiusa");

            bool refused = false;
            try
            {
                client.Write(new byte[] { 1 });
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            T.True(refused, "dopo RST la scrittura rifiuta");
            closed();
        }

        private static void DuplicateAck()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);
            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));

            client.Write(new byte[] { 1, 2, 3 });
            T.Eq(1, server.ReceivedData.Count, "primo segmento arrivato");
            server.AckToClient(client.SendNxt); // ack esatto: il primo invio si chiude

            client.Write(new byte[] { 4, 5, 6 });
            T.Eq(2, server.ReceivedData.Count, "secondo segmento arrivato");

            // Un ack per dati GIA' confermati non deve chiudere il secondo invio.
            server.AckToClient(client.SendNxt - 3);

            // Niente altro ack: scaduto il timer il secondo segmento si rispede.
            clock.Advance(250);
            client.Tick();
            T.Eq(3, server.ReceivedData.Count, "retransmit dopo l'ack duplicato: 2 dati + 1 ripetuto");
        }

        private static void FinEof()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);
            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));

            server.FinToClient();
            byte[] buffer = new byte[100];
            T.Eq(0, client.Read(buffer, TimeSpan.FromSeconds(1)), "dopo FIN la lettura e' a fine stream");

            bool eofAgain = false;
            try
            {
                client.Read(buffer, TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                eofAgain = true;
            }
            T.False(eofAgain, "la fine del resto resta EOF, non un timeout");
        }

        private static void MalformedIgnored()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);
            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));

            server.GarbageToClient();
            T.True(client.IsConnected, "segmento malformato: stato invariato");

            client.Write(new byte[] { 9 });
            T.Eq(1, server.ReceivedData.Count, "la connessione funziona ancora");
        }

        private static void DataRoundTrip()
        {
            MutableClock clock = new MutableClock();
            TestServer server = new TestServer();
            Action closed;
            TcpConnection client = MakeClient(server, clock, out closed);
            client.Connect(ServerIp, ServerPort, TimeSpan.FromSeconds(5));

            client.Write(new byte[] { 1, 2, 3, 4, 5 });
            T.Eq(5, server.ReceivedData[0].Length, "i dati del client arrivano");

            server.SendToClient(new byte[] { 0x61, 0x62, 0x63 }); // "abc"
            byte[] buffer = new byte[2];
            T.Eq(2, client.Read(buffer, TimeSpan.FromSeconds(1)), "primo parziale");
            T.Eq(new byte[] { 0x61, 0x62 }, buffer, "primi due byte");
            T.Eq(1, client.Read(buffer, TimeSpan.FromSeconds(1)), "resto del parziale");

            server.SendToClient(new byte[] { 0x64 });
            T.Eq(1, client.Read(buffer, TimeSpan.FromSeconds(1)), "l'ultimo byte");
        }
    }
}
