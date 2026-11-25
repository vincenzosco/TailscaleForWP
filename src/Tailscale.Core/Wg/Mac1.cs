using System;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Wg
{
    /// <summary>
    /// Il mac1 dei messaggi di handshake WireGuard, trascritto da
    /// refs/wireguard-go/device/cookie.go (Init e CheckMAC1):
    ///
    ///   chiave = BLAKE2s("mac1----" || static_del_destinatario)
    ///   mac1   = BLAKE2s keyed(chiave) su msg[0:len-32]
    ///
    /// Il destinatario e' il titolare della chiave statica con cui il messaggio
    /// e' stato spedito: l'initiation usa la statica del responder, la response
    /// quella dell'initiatore. Senza cookie il mac2 resta tutto zeri.
    /// </summary>
    public static class Mac1
    {
        /// <summary>La chiave di mac1 per un destinatario.</summary>
        public static byte[] KeyFor(byte[] destinationStaticPublic)
        {
            if (destinationStaticPublic == null) throw new ArgumentNullException("destinationStaticPublic");
            byte[] label = Tailscale.Core.Ascii.GetBytes(WgConstants.LabelMac1);
            byte[] input = new byte[label.Length + destinationStaticPublic.Length];
            Array.Copy(label, 0, input, 0, label.Length);
            Array.Copy(destinationStaticPublic, 0, input, label.Length, destinationStaticPublic.Length);
            return Blake2s.Hash(input, 32);
        }

        /// <summary>Il mac1 sui primi <paramref name="coveredLength"/> byte del messaggio.</summary>
        public static byte[] Compute(byte[] destinationStaticPublic, byte[] message, int coveredLength)
        {
            if (message == null) throw new ArgumentNullException("message");
            if (coveredLength < 0 || coveredLength > message.Length)
                throw new ArgumentOutOfRangeException("coveredLength");
            byte[] covered = new byte[coveredLength];
            Array.Copy(message, 0, covered, 0, coveredLength);
            return Blake2s.KeyedHash(KeyFor(destinationStaticPublic), covered, WgConstants.MacSize);
        }

        /// <summary>Vero se il mac1 nel messaggio copre esattamente i byte attesi.</summary>
        public static bool Verify(byte[] destinationStaticPublic, byte[] message, int macOffset)
        {
            if (message == null) throw new ArgumentNullException("message");
            if (macOffset < 0 || macOffset + WgConstants.MacSize > message.Length) return false;
            byte[] expected = Compute(destinationStaticPublic, message, macOffset);
            int diff = 0;
            for (int i = 0; i < WgConstants.MacSize; i++)
                diff |= message[macOffset + i] ^ expected[i];
            return diff == 0;
        }
    }
}
