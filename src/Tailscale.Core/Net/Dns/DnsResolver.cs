using System;
using System.Collections.Generic;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Net.Dns
{
    /// <summary>
    /// Il resolver DNS del mini-stack: solo record A (IPv4), domanda singola
    /// con recursion desired, ID casuale. Se il datagramma risponde con il
    /// bit TC (troncato) la domanda si ripete su TCP. Nessun CNAME, nessun
    /// AAAA: il mini-stack parla IPv4 e basta.
    /// </summary>
    public sealed class DnsResolver
    {
        private readonly IDnsChannel _channel;
        private readonly IRandomSource _random;

        public DnsResolver(IDnsChannel channel, IRandomSource random)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            if (random == null) throw new ArgumentNullException("random");
            _channel = channel;
            _random = random;
        }

        /// <summary>
        /// Risolve il primo record A. InvalidOperationException se la risposta
        /// non torna o non contiene alcun record A.
        /// </summary>
        public uint ResolveA(string hostname)
        {
            byte[] request = BuildQuery(hostname);

            byte[] response = _channel.QueryUdp(request);
            if (response != null && IsTruncated(response))
                response = _channel.QueryTcp(request);
            if (response == null)
                throw new InvalidOperationException("nessuna risposta DNS per " + hostname);
            if (response.Length < 12)
                throw new InvalidOperationException("risposta DNS non valida per " + hostname);
            if (response[0] != request[0] || response[1] != request[1])
                throw new InvalidOperationException("risposta DNS con ID estraneo per " + hostname);

            int questionCount = (response[4] << 8) | response[5];
            int answerCount = (response[6] << 8) | response[7];
            int offset = SkipName(response, 12) + 4 * questionCount;
            for (int i = 0; i < answerCount; i++)
            {
                offset = SkipName(response, offset);
                if (offset + 10 > response.Length)
                    throw new InvalidOperationException("risposta DNS tronca per " + hostname);
                int type = (response[offset] << 8) | response[offset + 1];
                int klass = (response[offset + 2] << 8) | response[offset + 3];
                int dataLength = (response[offset + 8] << 8) | response[offset + 9];
                offset += 10;
                if (offset + dataLength > response.Length)
                    throw new InvalidOperationException("risposta DNS tronca per " + hostname);
                if (type == 1 && klass == 1 && dataLength == 4)
                {
                    return (uint)((response[offset] << 24)
                                  | (response[offset + 1] << 16)
                                  | (response[offset + 2] << 8)
                                  | response[offset + 3]);
                }
                offset += dataLength;
            }
            throw new InvalidOperationException("nessun record A per " + hostname);
        }

        private static bool IsTruncated(byte[] response)
        {
            return response.Length >= 3 && (response[2] & 0x02) != 0;
        }

        private byte[] BuildQuery(string hostname)
        {
            List<byte> name = EncodeName(hostname);
            byte[] request = new byte[12 + name.Count + 4];

            byte[] idSeed = new byte[2];
            _random.NextBytes(idSeed);
            request[0] = idSeed[0];
            request[1] = idSeed[1];
            request[2] = 0x01;   // RD: recursion desired
            request[5] = 1;      // una domanda
            for (int i = 0; i < name.Count; i++) request[12 + i] = name[i];
            int tail = 12 + name.Count;
            request[tail + 1] = 1;   // tipo A
            request[tail + 3] = 1;   // classe IN
            return request;
        }

        private static List<byte> EncodeName(string hostname)
        {
            if (string.IsNullOrEmpty(hostname))
                throw new ArgumentException("serve un nome", "hostname");
            List<byte> name = new List<byte>();
            string[] labels = hostname.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                if (label.Length == 0 || label.Length > 63)
                    throw new ArgumentException("etichetta non valida in " + hostname, "hostname");
                name.Add((byte)label.Length);
                for (int j = 0; j < label.Length; j++)
                {
                    char c = label[j];
                    if (c > 0x7F)
                        throw new ArgumentException("etichetta non ASCII in " + hostname + ": serve il punycode", "hostname");
                    name.Add((byte)c);
                }
            }
            name.Add(0);
            return name;
        }

        /// <summary>Salta un nome (etichette o puntatore di compressione) e torna dopo.</summary>
        private static int SkipName(byte[] message, int offset)
        {
            int guard = 0;
            while (offset < message.Length && guard++ < 128)
            {
                int length = message[offset];
                if (length == 0) return offset + 1;
                if ((length & 0xC0) == 0xC0) return offset + 2;
                offset += length + 1;
            }
            throw new InvalidOperationException("nome DNS non valido");
        }
    }
}
