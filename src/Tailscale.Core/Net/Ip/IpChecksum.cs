using System;

namespace Tailscale.Core.Net.Ip
{
    /// <summary>
    /// Il checksum di Internet (RFC 1071): somma a complemento a uno dei
    /// word a 16 bit big-endian, poi complemento. La somma di un blocco che
    /// include il proprio checksum fa 0xFFFF quando torna.
    /// </summary>
    public static class IpChecksum
    {
        /// <summary>La somma a complemento a uno, senza complementarla.</summary>
        public static int Sum(byte[] buffer, int offset, int length)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            int sum = 0;
            int i = 0;
            for (; i + 1 < length; i += 2)
                sum += (buffer[offset + i] << 8) | buffer[offset + i + 1];
            if (i < length) sum += buffer[offset + i] << 8;
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return sum;
        }

        /// <summary>Il checksum da scrivere nel campo: complemento della somma.</summary>
        public static int Compute(byte[] buffer, int offset, int length)
        {
            return ~Sum(buffer, offset, length) & 0xFFFF;
        }

        /// <summary>
        /// La somma dell'header IPv4 (IHL * 4 byte, checksum compreso):
        /// 0xFFFF quando il pacchetto e' integro.
        /// </summary>
        public static int SumOfHeader(byte[] packet)
        {
            if (packet == null) throw new ArgumentNullException("packet");
            int headerLength = (packet[0] & 0x0F) * 4;
            return Sum(packet, 0, headerLength);
        }
    }
}
