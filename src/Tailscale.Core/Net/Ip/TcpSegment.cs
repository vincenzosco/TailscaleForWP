using System;
using Tailscale.Core.Net.Ip;

namespace Tailscale.Core.Net.Ip
{
    /// <summary>
    /// Un segmento TCP e il suo encoding/parsing, RFC 793 essenziale:
    /// header a 20 byte, opzioni allineate a 4 byte, checksum con
    /// pseudo-header IPv4. Niente SACK, niente timestamp: il mini-stack e'
    /// initiator-only e parla con servizi del tailnet, non con il mondo.
    /// </summary>
    public sealed class TcpSegment
    {
        public const byte FlagFin = 0x01;
        public const byte FlagSyn = 0x02;
        public const byte FlagRst = 0x04;
        public const byte FlagPsh = 0x08;
        public const byte FlagAck = 0x10;

        public const int HeaderSize = 20;

        public ushort SrcPort;
        public ushort DstPort;
        public uint Seq;
        public uint Ack;
        public byte Flags;
        public ushort Window;
        public byte[] Options = new byte[0];
        public byte[] Payload = new byte[0];

        /// <summary>L'opzione MSS (tipo 2, lunghezza 4) del SYN.</summary>
        public static byte[] MssOption(int mss)
        {
            return new byte[]
            {
                2, 4, (byte)(mss >> 8), (byte)mss
            };
        }

        /// <summary>Il segmento completo, opzioni e checksum compresi.</summary>
        public static byte[] Encode(TcpSegment segment, uint srcIp, uint dstIp)
        {
            if (segment == null) throw new ArgumentNullException("segment");
            byte[] options = segment.Options ?? new byte[0];
            if (options.Length % 4 != 0)
                throw new ArgumentException("le opzioni TCP si allineano a 4 byte", "segment");
            byte[] payload = segment.Payload ?? new byte[0];

            int headerLength = HeaderSize + options.Length;
            byte[] buffer = new byte[headerLength + payload.Length];
            PutUInt16(buffer, 0, segment.SrcPort);
            PutUInt16(buffer, 2, segment.DstPort);
            PutUInt32(buffer, 4, segment.Seq);
            PutUInt32(buffer, 8, segment.Ack);
            buffer[12] = (byte)((headerLength / 4) << 4);
            buffer[13] = segment.Flags;
            PutUInt16(buffer, 14, segment.Window);
            PutUInt16(buffer, 16, 0); // checksum, sotto
            PutUInt16(buffer, 18, 0); // urgent pointer: non usato
            Array.Copy(options, 0, buffer, HeaderSize, options.Length);
            Array.Copy(payload, 0, buffer, headerLength, payload.Length);

            int checksum = PseudoChecksum(buffer, srcIp, dstIp);
            PutUInt16(buffer, 16, (ushort)checksum);
            return buffer;
        }

        /// <summary>
        /// Legge un segmento e ne verifica il checksum. False = rifiuto:
        /// nullo, corto, header non valido o checksum rotto.
        /// </summary>
        public static bool TryParse(byte[] buffer, uint srcIp, uint dstIp, out TcpSegment segment)
        {
            segment = null;
            if (buffer == null) return false;
            if (buffer.Length < HeaderSize) return false;

            int headerLength = (buffer[12] >> 4) * 4;
            if (headerLength < HeaderSize || headerLength > buffer.Length) return false;

            // Il checksum include il proprio campo: la somma totale fa
            // 0xFFFF quando il segmento e' integro.
            if (SumWithChecksum(buffer, srcIp, dstIp) != 0xFFFF) return false;

            TcpSegment parsed = new TcpSegment();
            parsed.SrcPort = GetUInt16(buffer, 0);
            parsed.DstPort = GetUInt16(buffer, 2);
            parsed.Seq = GetUInt32(buffer, 4);
            parsed.Ack = GetUInt32(buffer, 8);
            parsed.Flags = buffer[13];
            parsed.Window = GetUInt16(buffer, 14);
            parsed.Options = new byte[headerLength - HeaderSize];
            Array.Copy(buffer, HeaderSize, parsed.Options, 0, parsed.Options.Length);
            parsed.Payload = new byte[buffer.Length - headerLength];
            Array.Copy(buffer, headerLength, parsed.Payload, 0, parsed.Payload.Length);
            segment = parsed;
            return true;
        }

        /// <summary>La somma che include il campo checksum: 0xFFFF se torna.</summary>
        private static int SumWithChecksum(byte[] buffer, uint srcIp, uint dstIp)
        {
            byte[] pseudo = PseudoHeader(buffer, srcIp, dstIp);
            return IpChecksum.Sum(pseudo, 0, pseudo.Length);
        }

        /// <summary>Il checksum da scrivere, col campo azzerato.</summary>
        private static int PseudoChecksum(byte[] buffer, uint srcIp, uint dstIp)
        {
            byte[] pseudo = PseudoHeader(buffer, srcIp, dstIp);
            return IpChecksum.Compute(pseudo, 0, pseudo.Length);
        }

        /// <summary>
        /// Il pseudo-header di RFC 793 (sorgente, destinazione, zero,
        /// protocollo 6, lunghezza) seguito dal segmento con checksum azzerato.
        /// </summary>
        private static byte[] PseudoHeader(byte[] buffer, uint srcIp, uint dstIp)
        {
            byte[] full = new byte[12 + buffer.Length];
            PutUInt32(full, 0, srcIp);
            PutUInt32(full, 4, dstIp);
            full[8] = 0;
            full[9] = 6;
            PutUInt16(full, 10, (ushort)buffer.Length);
            Array.Copy(buffer, 0, full, 12, buffer.Length);
            return full;
        }

        private static void PutUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static void PutUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static ushort GetUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint GetUInt32(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24)
                          | (buffer[offset + 1] << 16)
                          | (buffer[offset + 2] << 8)
                          | buffer[offset + 3]);
        }
    }
}
