using System;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il livello IPv4 del mini-stack: costruzione, parsing, checksum, MTU.
    ///
    /// Il tunnel muove al massimo 1280 byte: un pacchetto piu' grande viene
    /// rifiutato in modo esplicito, mai troncato in silenzio. I frammenti in
    /// ingresso si scartano: niente reassembly in questa fase.
    /// </summary>
    public static class IpV4Tests
    {
        public static void Run()
        {
            Case("ip: build/parse a giro di 1200 byte", RoundTrip);
            Case("ip: il checksum dell'header torna", Checksum);
            Case("ip: totalLength incoerente, rifiutato", BadTotalLength);
            Case("ip: oltre l'MTU, rifiuto esplicito", OverMtu);
            Case("ip: frammenti scartati, header corti scartati", FragmentsAndShorts);
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

        private static byte[] Src = IpV4.Address(100, 64, 0, 1);
        private static byte[] Dst = IpV4.Address(100, 64, 0, 2);

        private static void RoundTrip()
        {
            byte[] payload = new byte[1200];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 13);

            byte[] packet = IpV4.Build(Src, Dst, 6, payload);
            T.Eq(1220, packet.Length, "header 20 + payload 1200");

            IpV4Header header;
            byte[] parsed;
            T.True(IpV4.Parse(packet, out header, out parsed), "il pacchetto si apre");
            T.Eq(Src, header.Src, "sorgente conservata");
            T.Eq(Dst, header.Dst, "destinazione conservata");
            T.Eq(6, (int)header.Protocol, "protocollo conservato");
            T.Eq(payload, parsed, "payload identico");
        }

        private static void Checksum()
        {
            byte[] packet = IpV4.Build(Src, Dst, 17, new byte[8]);
            T.Eq(0xFFFF, IpChecksum.SumOfHeader(packet), "la somma dell'header e' 0xFFFF");
        }

        private static void BadTotalLength()
        {
            byte[] packet = IpV4.Build(Src, Dst, 6, new byte[100]);
            packet[2] = 0; // totalLength alto: incoerente con il buffer
            packet[3] = 20;

            IpV4Header header;
            byte[] parsed;
            T.False(IpV4.Parse(packet, out header, out parsed), "totalLength incoerente: rifiuto");
        }

        private static void OverMtu()
        {
            bool refused = false;
            try
            {
                IpV4.Build(Src, Dst, 6, new byte[1400]);
            }
            catch (ArgumentException)
            {
                refused = true;
            }
            T.True(refused, "1400 byte oltre l'MTU: rifiuto esplicito");

            // Esattamente all'MTU si puo'.
            byte[] packet = IpV4.Build(Src, Dst, 6, new byte[IpV4.TunnelMtu - 20]);
            T.Eq(IpV4.TunnelMtu, packet.Length, "esattamente 1280 byte");
        }

        private static void FragmentsAndShorts()
        {
            IpV4Header header;
            byte[] parsed;

            byte[] packet = IpV4.Build(Src, Dst, 6, new byte[40]);
            T.True(IpV4.Parse(packet, out header, out parsed), "intero: accettato");

            // Flag di frammento: bit 1 del terzo byte.
            byte[] fragment = IpV4.Build(Src, Dst, 6, new byte[40]);
            fragment[6] = 0x20;
            T.False(IpV4.Parse(fragment, out header, out parsed), "frammento: scartato");

            T.False(IpV4.Parse(new byte[10], out header, out parsed), "header corto: scartato");
            T.False(IpV4.Parse(null, out header, out parsed), "nullo: scartato");
        }
    }
}
