using System;

namespace Tailscale.Core.Net.Ip
{
    /// <summary>
    /// Un datagramma UDP e il suo encoding/parsing (RFC 768): header a 8
    /// byte, checksum con pseudo-header. In invio il checksum si calcola
    /// sempre (se viene zero si incide 0xFFFF); in ricezione il campo a zero
    /// si accetta, come dice la RFC.
    /// </summary>
    public sealed class UdpSegment
    {
        public ushort SrcPort;
        public ushort DstPort;
        public byte[] Payload = new byte[0];

        public const int HeaderSize = 8;

        /// <summary>Il datagramma completo con il checksum calcolato.</summary>
        public static byte[] Encode(UdpSegment segment, uint srcIp, uint dstIp)
        {
            if (segment == null) throw new ArgumentNullException("segment");
            byte[] payload = segment.Payload ?? new byte[0];
            int length = HeaderSize + payload.Length;

            byte[] buffer = new byte[length];
            buffer[0] = (byte)(segment.SrcPort >> 8);
            buffer[1] = (byte)segment.SrcPort;
            buffer[2] = (byte)(segment.DstPort >> 8);
            buffer[3] = (byte)segment.DstPort;
            buffer[4] = (byte)(length >> 8);
            buffer[5] = (byte)length;
            buffer[6] = 0;
            buffer[7] = 0;
            Array.Copy(payload, 0, buffer, HeaderSize, payload.Length);

            int checksum = PseudoSum(buffer, srcIp, dstIp);
            checksum = ~checksum & 0xFFFF;
            if (checksum == 0) checksum = 0xFFFF;
            buffer[6] = (byte)(checksum >> 8);
            buffer[7] = (byte)checksum;
            return buffer;
        }

        /// <summary>
        /// Legge il datagramma. False = rifiuto: nullo, corto, lunghezza che
        /// non torna o checksum sbagliato (il campo a zero pero' si accetta).
        /// </summary>
        public static bool TryParse(byte[] buffer, uint srcIp, uint dstIp, out UdpSegment segment)
        {
            segment = null;
            if (buffer == null) return false;
            if (buffer.Length < HeaderSize) return false;

            int length = (buffer[4] << 8) | buffer[5];
            if (length != buffer.Length) return false;

            if ((buffer[6] | buffer[7]) != 0 && PseudoSum(buffer, srcIp, dstIp) != 0xFFFF)
                return false;

            UdpSegment parsed = new UdpSegment();
            parsed.SrcPort = (ushort)((buffer[0] << 8) | buffer[1]);
            parsed.DstPort = (ushort)((buffer[2] << 8) | buffer[3]);
            parsed.Payload = new byte[length - HeaderSize];
            Array.Copy(buffer, HeaderSize, parsed.Payload, 0, parsed.Payload.Length);
            segment = parsed;
            return true;
        }

        private static int PseudoSum(byte[] buffer, uint srcIp, uint dstIp)
        {
            byte[] pseudo = new byte[12 + buffer.Length];
            pseudo[0] = (byte)(srcIp >> 24);
            pseudo[1] = (byte)(srcIp >> 16);
            pseudo[2] = (byte)(srcIp >> 8);
            pseudo[3] = (byte)srcIp;
            pseudo[4] = (byte)(dstIp >> 24);
            pseudo[5] = (byte)(dstIp >> 16);
            pseudo[6] = (byte)(dstIp >> 8);
            pseudo[7] = (byte)dstIp;
            pseudo[8] = 0;
            pseudo[9] = 17;
            pseudo[10] = (byte)(buffer.Length >> 8);
            pseudo[11] = (byte)buffer.Length;
            Array.Copy(buffer, 0, pseudo, 12, buffer.Length);
            return IpChecksum.Sum(pseudo, 0, pseudo.Length);
        }
    }
}
