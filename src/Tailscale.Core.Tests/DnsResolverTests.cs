using System;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il resolver DNS del mini-stack: query A col formato giusto (ID
    /// casuale, recursion desired, tipo A), risposta A presa dalla risposta,
    /// fallback a TCP quando il datagramma e' troncato, errore esplicito se
    /// non c'e' nessun record A. Il canale e' iniettato: UDP e TCP li decide
    /// chi lo costruisce.
    /// </summary>
    public static class DnsResolverTests
    {
        public static void Run()
        {
            Case("dns: query A col formato giusto", QueryFormat);
            Case("dns: risposta A presa dalla risposta", AddressParsed);
            Case("dns: fallback a TCP se la risposta e' troncata", TcpFallback);
            Case("dns: nessun record A, errore esplicito", NoAnswer);
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

        private sealed class SequenceRandom : IRandomSource
        {
            private int _step = 7;
            public void NextBytes(byte[] buffer)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(_step++);
            }
        }

        /// <summary>
        /// Un canale finto: costruisce la risposta sulla query viva, cosi'
        /// l'ID torna sempre e il copione non si aggancia ai numeri.
        /// </summary>
        private sealed class ScriptedChannel : Net.Dns.IDnsChannel
        {
            public byte[] UdpRequest;
            public int TcpCalls;
            public byte[] Address;      // null = nessun record A
            public bool UdpTruncated;

            public byte[] QueryUdp(byte[] request)
            {
                UdpRequest = request;
                return Response(request, UdpTruncated, Address);
            }

            public byte[] QueryTcp(byte[] request)
            {
                TcpCalls++;
                return Response(request, false, Address);
            }
        }

        /// <summary>Una risposta DNS minimale con un record A (o senza).</summary>
        private static byte[] Response(byte[] request, bool truncated, byte[] address)
        {
            int questionEnd = request.Length;
            byte[] message = new byte[questionEnd + (address == null ? 0 : 16)];
            message[0] = request[0];
            message[1] = request[1];
            message[2] = (byte)(0x81 | (truncated ? 0x02 : 0x00)); // risposta, RD, TC opzionale
            message[3] = 0x80;                                     // recursion available
            message[5] = 1;                                        // una domanda
            message[6] = (byte)(address == null ? 0 : 1);          // risposte
            Array.Copy(request, 12, message, 12, questionEnd - 12);
            if (address != null)
            {
                int at = questionEnd;
                message[at] = 0xC0;
                message[at + 1] = 0x0C;       // puntatore al nome
                message[at + 2] = 0;
                message[at + 3] = 1;          // tipo A
                message[at + 4] = 0;
                message[at + 5] = 1;          // classe IN
                message[at + 9] = 30;         // TTL
                message[at + 11] = 4;         // RDLENGTH
                message[at + 12] = address[0];
                message[at + 13] = address[1];
                message[at + 14] = address[2];
                message[at + 15] = address[3];
            }
            return message;
        }

        private static void QueryFormat()
        {
            ScriptedChannel channel = new ScriptedChannel();
            Net.Dns.DnsResolver resolver = new Net.Dns.DnsResolver(channel, new SequenceRandom());

            try
            {
                resolver.ResolveA("esempio.test");
            }
            catch (InvalidOperationException)
            {
                // Nessun record A: qui interessa la QUERY.
            }

            byte[] query = channel.UdpRequest;
            T.True(query != null, "la query e' partita");
            if (query == null) return;
            T.Eq(1, (query[4] << 8) | query[5], "una domanda");
            T.Eq(0x01, (int)(query[2] & 0x01), "recursion desired");
            T.Eq(0x00, (int)(query[2] & 0x78), "opcode standard");

            // La domanda: esempio.test = 07 esempio 04 test 00, tipo A, classe IN.
            T.Eq(7, (int)query[12], "etichetta esempio");
            T.Eq((int)'e', (int)query[13], "e");
            T.Eq((int)'o', (int)query[19], "o");
            T.Eq(4, (int)query[20], "etichetta test");
            T.Eq(0x00, (int)query[25], "fine nome");
            T.Eq(1, (query[26] << 8) | query[27], "tipo A");
            T.Eq(1, (query[28] << 8) | query[29], "classe IN");
        }

        private static void AddressParsed()
        {
            ScriptedChannel channel = new ScriptedChannel();
            channel.Address = new byte[] { 93, 184, 216, 34 };
            Net.Dns.DnsResolver resolver = new Net.Dns.DnsResolver(channel, new SequenceRandom());

            uint address = resolver.ResolveA("esempio.test");
            T.Eq(93, (int)((address >> 24) & 0xFF), "primo ottetto");
            T.Eq(184, (int)((address >> 16) & 0xFF), "secondo ottetto");
            T.Eq(216, (int)((address >> 8) & 0xFF), "terzo ottetto");
            T.Eq(34, (int)(address & 0xFF), "quarto ottetto");
        }

        private static void TcpFallback()
        {
            ScriptedChannel channel = new ScriptedChannel();
            channel.Address = new byte[] { 10, 20, 30, 40 };
            channel.UdpTruncated = true;
            Net.Dns.DnsResolver resolver = new Net.Dns.DnsResolver(channel, new SequenceRandom());

            uint address = resolver.ResolveA("esempio.test");
            T.Eq(1, channel.TcpCalls, "fallback a TCP");
            T.Eq(10, (int)((address >> 24) & 0xFF), "indirizzo dalla risposta TCP");
        }

        private static void NoAnswer()
        {
            ScriptedChannel channel = new ScriptedChannel();
            Net.Dns.DnsResolver resolver = new Net.Dns.DnsResolver(channel, new SequenceRandom());

            bool refused = false;
            try
            {
                resolver.ResolveA("esempio.test");
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            T.True(refused, "senza record A: errore esplicito");
        }
    }
}
