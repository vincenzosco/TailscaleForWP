using System;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Le costanti di WireGuard hanno la forma che il sorgente pinnato dice:
    /// nomi alla lunghezza giusta, formati dei messaggi che tornano con i
    /// campi, timer e limiti agli esatti numeri trascritti.
    ///
    /// Il controllo contro refs/wireguard-go sta nel gate
    /// (tools/check-protocol-pin.py); qui la coerenza interna, che e' quella
    /// che un refactoring rompe.
    /// </summary>
    public static class WgConstantsTests
    {
        public static void Run()
        {
            Case("wg: costruzione, identita' e label hanno la lunghezza giusta", Names);
            Case("wg: i formati dei messaggi tornano con i campi", Layout);
            Case("wg: timer e limiti sono i numeri del sorgente", Timers);
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

        private static void Names()
        {
            T.Eq(37, WgConstants.Construction.Length, "Construction: 37 byte");
            T.Eq(34, WgConstants.Identifier.Length, "Identifier: 34 byte");
            T.Eq(8, WgConstants.LabelMac1.Length, "label mac1: 8 byte");
            T.Eq(8, WgConstants.LabelCookie.Length, "label cookie: 8 byte");
            T.Eq("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s", WgConstants.Construction,
                 "costruzione esatta");
            T.Eq("WireGuard v1 zx2c4 Jason@zx2c4.com", WgConstants.Identifier,
                 "identita' esatta");
        }

        private static void Layout()
        {
            // type(4) sender(4) ephemeral(32) static(32+16) timestamp(12+16)
            // mac1(16) mac2(16)
            T.Eq(4 + 4 + 32 + (WgConstants.KeySize + WgConstants.TagSize)
                 + (WgConstants.TimestampSize + WgConstants.TagSize)
                 + WgConstants.MacSize + WgConstants.MacSize,
                 WgConstants.MessageInitiationSize, "initiation: 148 byte");
            // type(4) sender(4) receiver(4) ephemeral(32) empty(16) mac1(16) mac2(16)
            T.Eq(4 + 4 + 4 + 32 + WgConstants.TagSize
                 + WgConstants.MacSize + WgConstants.MacSize,
                 WgConstants.MessageResponseSize, "response: 92 byte");
            // type(4) receiver(4) nonce(24) cookie(16+16)
            T.Eq(4 + 4 + WgConstants.CookieNonceSize
                 + WgConstants.MacSize + WgConstants.TagSize,
                 WgConstants.MessageCookieReplySize, "cookie reply: 64 byte");
            // type(4) receiver(4) counter(8)
            T.Eq(4 + 4 + 8, WgConstants.MessageTransportHeaderSize, "header transport: 16 byte");
            T.Eq(WgConstants.MessageTransportHeaderSize, WgConstants.MessageTransportOffsetContent,
                 "il contenuto comincia dove finisce l'header");
            T.Eq(WgConstants.MessageTransportHeaderSize + WgConstants.TagSize,
                 WgConstants.MessageKeepaliveSize, "keepalive: header + tag");
            T.Eq(1, WgConstants.MessageInitiationType, "tipo initiation");
            T.Eq(2, WgConstants.MessageResponseType, "tipo response");
            T.Eq(3, WgConstants.MessageCookieReplyType, "tipo cookie reply");
            T.Eq(4, WgConstants.MessageTransportType, "tipo transport");
        }

        private static void Timers()
        {
            T.Eq(120, WgConstants.RekeyAfterSeconds, "rekey dopo 120 s");
            T.Eq(180, WgConstants.RejectAfterSeconds, "rifiuto dopo 180 s");
            T.Eq(10, WgConstants.KeepaliveSeconds, "keepalive a 10 s");
            T.Eq(5, WgConstants.RekeyTimeoutSeconds, "ritentativo handshake a 5 s");
            T.Eq(90, WgConstants.RekeyAttemptSeconds, "finestra di ritentativi a 90 s");
            T.Eq("1152921504606846976", WgConstants.RekeyAfterMessages.ToString(),
                 "rekey dopo 2^60 messaggi");
            T.Eq("18446744073709543423", WgConstants.RejectAfterMessages.ToString(),
                 "rifiuto a 2^64 - 2^13 - 1");
            T.Eq("4611686018427387914", ((ulong)WgConstants.Tai64Base).ToString(),
                 "base TAI64 a 0x400000000000000a");
            T.Eq(0xFFFFFF, (int)WgConstants.NanosecondWhitenerMask, "maschera nanosecondi");
        }
    }
}
