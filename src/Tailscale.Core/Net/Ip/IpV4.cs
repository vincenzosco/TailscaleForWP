using System;

namespace Tailscale.Core.Net.Ip
{
    /// <summary>I campi dell'header IPv4 che il mini-stack guarda.</summary>
    public sealed class IpV4Header
    {
        public byte[] Src;
        public byte[] Dst;
        public byte Protocol;
        public byte Ttl;
        public int TotalLength;
        public int Identification;
    }

    /// <summary>
    /// Il livello IPv4 del mini-stack userspace: costruisce e legge pacchetti
    /// a header 20 byte (IHL 5, niente opzioni), IPv4 soltanto.
    ///
    /// Regole del piano 2: il tunnel non muove piu' di 1280 byte e un
    /// pacchetto piu' grande viene rifiutato con un'eccezione, mai troncato;
    /// i frammenti in ingresso si scartano (niente reassembly); un
    /// totalLength che non torna con il buffer e' corruzione e si scarta.
    /// </summary>
    public static class IpV4
    {
        /// <summary>MTU del tunnel (vincolo del piano 2, non una costante upstream).</summary>
        public const int TunnelMtu = 1280;

        public const int HeaderSize = 20;

        private const byte DefaultTtl = 64;

        /// <summary>Un indirizzo da quattro ottetti.</summary>
        public static byte[] Address(byte a, byte b, byte c, byte d)
        {
            return new byte[] { a, b, c, d };
        }

        /// <summary>L'indirizzo come intero in ordine di rete.</summary>
        public static uint ToUInt32(byte[] address)
        {
            if (address == null || address.Length != 4)
                throw new ArgumentException("un indirizzo IPv4 ha 4 byte", "address");
            return (uint)((address[0] << 24) | (address[1] << 16) | (address[2] << 8) | address[3]);
        }

        /// <summary>L'intero di rete come indirizzo a quattro ottetti.</summary>
        public static byte[] ToBytes(uint address)
        {
            return new byte[]
            {
                (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address
            };
        }

        /// <summary>
        /// Costruisce il pacchetto. Eccezione esplicita se il pacchetto
        /// supera l'MTU del tunnel.
        /// </summary>
        public static byte[] Build(byte[] src, byte[] dst, byte protocol, byte[] payload)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (dst == null) throw new ArgumentNullException("dst");
            if (src.Length != 4) throw new ArgumentException("un indirizzo IPv4 ha 4 byte", "src");
            if (dst.Length != 4) throw new ArgumentException("un indirizzo IPv4 ha 4 byte", "dst");
            if (payload == null) throw new ArgumentNullException("payload");

            int totalLength = HeaderSize + payload.Length;
            if (totalLength > TunnelMtu)
                throw new ArgumentException("pacchetto da " + totalLength
                                            + " byte oltre l'MTU del tunnel (" + TunnelMtu + ")", "payload");

            byte[] packet = new byte[totalLength];
            packet[0] = 0x45;             // versione 4, IHL 5
            packet[1] = 0;                // DSCP/ECN
            packet[2] = (byte)(totalLength >> 8);
            packet[3] = (byte)totalLength;
            packet[4] = 0;                // identification: il tunnel non frammenta
            packet[5] = 0;
            packet[6] = 0x40;             // DF: non frammentare
            packet[7] = 0;
            packet[8] = DefaultTtl;
            packet[9] = protocol;
            Array.Copy(src, 0, packet, 12, 4);
            Array.Copy(dst, 0, packet, 16, 4);
            int checksum = IpChecksum.Compute(packet, 0, HeaderSize);
            packet[10] = (byte)(checksum >> 8);
            packet[11] = (byte)checksum;
            Array.Copy(payload, 0, packet, HeaderSize, payload.Length);
            return packet;
        }

        /// <summary>
        /// Legge un pacchetto. False = scarto: null, corto, non IPv4, con
        /// opzioni, frammentato, con checksum rotto o con totalLength che non
        /// torna con il buffer. Il frammento si scarta senza reassembly.
        /// </summary>
        public static bool Parse(byte[] packet, out IpV4Header header, out byte[] payload)
        {
            header = null;
            payload = null;
            if (packet == null) return false;
            if (packet.Length < HeaderSize) return false;
            if (packet[0] != 0x45) return false;   // solo IPv4 con IHL 5

            int totalLength = (packet[2] << 8) | packet[3];
            if (totalLength != packet.Length) return false;

            // Frammenti: flag MF (bit 2 del sesto byte) o offset non nullo.
            if ((packet[6] & 0x3F) != 0 || packet[7] != 0) return false;

            if (IpChecksum.SumOfHeader(packet) != 0xFFFF) return false;

            IpV4Header parsed = new IpV4Header();
            parsed.TotalLength = totalLength;
            parsed.Identification = (packet[4] << 8) | packet[5];
            parsed.Ttl = packet[8];
            parsed.Protocol = packet[9];
            parsed.Src = new byte[4];
            parsed.Dst = new byte[4];
            Array.Copy(packet, 12, parsed.Src, 0, 4);
            Array.Copy(packet, 16, parsed.Dst, 0, 4);

            byte[] body = new byte[totalLength - HeaderSize];
            Array.Copy(packet, HeaderSize, body, 0, body.Length);
            header = parsed;
            payload = body;
            return true;
        }
    }
}
