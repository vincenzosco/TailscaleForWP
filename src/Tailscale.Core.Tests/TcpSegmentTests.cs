using System;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il formato dei segmenti TCP: header, opzioni, checksum con
    /// pseudo-header. Le sequenze sono a 32 bit e il checksum e' quello di
    /// RFC 793: un byte sbagliato e il segmento non si apre.
    /// </summary>
    public static class TcpSegmentTests
    {
        public static void Run()
        {
            Case("tcp: SYN con opzione MSS, flag e sequenze giuste", SynWithMss);
            Case("tcp: parsing con opzioni e payload", ParseWithOptions);
            Case("tcp: checksum con pseudo-header", ChecksumRoundTrip);
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

        private const uint SrcIp = 0x64400001;
        private const uint DstIp = 0x64400002;

        private static void SynWithMss()
        {
            TcpSegment syn = new TcpSegment();
            syn.SrcPort = 40000;
            syn.DstPort = 80;
            syn.Seq = 0x89ABCDEF;
            syn.Flags = TcpSegment.FlagSyn;
            syn.Window = 65535;
            syn.Options = TcpSegment.MssOption(1240);

            byte[] buffer = TcpSegment.Encode(syn, SrcIp, DstIp);
            T.Eq(20 + 4 + 0, buffer.Length, "header 20 + opzione 4");

            TcpSegment parsed;
            T.True(TcpSegment.TryParse(buffer, SrcIp, DstIp, out parsed), "il SYN si apre");
            if (parsed == null) return;
            T.Eq(2, (int)parsed.Flags, "flag SYN");
            T.Eq(unchecked((int)0x89ABCDEF), (int)parsed.Seq, "sequenza a 32 bit");
            T.Eq(40000, (int)parsed.SrcPort, "porta sorgente");
            T.Eq(80, (int)parsed.DstPort, "porta destinazione");
            T.Eq(65535, (int)parsed.Window, "finestra");
            T.Eq(4, parsed.Options.Length, "opzione MSS a 4 byte");
            T.Eq(2, (int)parsed.Options[0], "tipo opzione MSS");
            T.Eq(1240, (parsed.Options[2] << 8) | parsed.Options[3], "valore MSS");
        }

        private static void ParseWithOptions()
        {
            TcpSegment segment = new TcpSegment();
            segment.SrcPort = 40001;
            segment.DstPort = 443;
            segment.Seq = 1000;
            segment.Ack = 2000;
            segment.Flags = TcpSegment.FlagAck | TcpSegment.FlagPsh;
            segment.Window = 32000;
            segment.Options = TcpSegment.MssOption(1240);
            segment.Payload = new byte[] { 1, 2, 3, 4, 5 };

            byte[] buffer = TcpSegment.Encode(segment, SrcIp, DstIp);
            TcpSegment parsed;
            T.True(TcpSegment.TryParse(buffer, SrcIp, DstIp, out parsed), "si apre");
            if (parsed == null) return;
            T.Eq(2000, (int)parsed.Ack, "ack conservato");
            T.Eq(0x18, (int)parsed.Flags, "PSH e ACK");
            T.Eq(new byte[] { 1, 2, 3, 4, 5 }, parsed.Payload, "payload identico");
        }

        private static void ChecksumRoundTrip()
        {
            TcpSegment segment = new TcpSegment();
            segment.SrcPort = 40002;
            segment.DstPort = 8080;
            segment.Seq = 7;
            segment.Ack = 9;
            segment.Flags = TcpSegment.FlagAck;
            segment.Window = 1000;
            segment.Payload = new byte[] { 0xAA, 0xBB };

            byte[] buffer = TcpSegment.Encode(segment, SrcIp, DstIp);
            TcpSegment parsed;
            T.True(TcpSegment.TryParse(buffer, SrcIp, DstIp, out parsed), "checksum valido: si apre");

            buffer[buffer.Length - 1] ^= 0xFF;
            T.False(TcpSegment.TryParse(buffer, SrcIp, DstIp, out parsed), "byte corrotto: rifiuto");

            T.False(TcpSegment.TryParse(new byte[8], SrcIp, DstIp, out parsed), "troppo corto: rifiuto");
            T.False(TcpSegment.TryParse(null, SrcIp, DstIp, out parsed), "nullo: rifiuto");
        }
    }
}
