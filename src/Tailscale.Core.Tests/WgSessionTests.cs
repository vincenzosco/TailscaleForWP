using System;
using Tailscale.Core.Keys;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// La sessione di trasporto WireGuard: incapsulamento, replay, timer.
    ///
    /// I valori temporali arrivano dal chiamante (il core non legge
    /// l'orologio): i test muovono il tempo a mano e verificano le soglie di
    /// WgConstants. Il round-trip e' byte-per-byte: niente padding, come da
    /// docs/wireguard-notes.md.
    /// </summary>
    public static class WgSessionTests
    {
        public static void Run()
        {
            Case("wg: round-trip di 1000 byte, identici", RoundTrip);
            Case("wg: formato del messaggio di trasporto", TransportFormat);
            Case("wg: replay dello stesso contatore rifiutato", ReplayRejected);
            Case("wg: keepalive e' un messaggio vuoto", KeepaliveIsEmpty);
            Case("wg: timer di scadenza, keepalive e rekey", Timers);
            Case("wg: contatore oltre il limite rifiuta l'invio", RejectAfterMessages);
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

        private static WgSessionKeys TestKeys()
        {
            WgSessionKeys keys = new WgSessionKeys();
            keys.SendKey = new byte[32];
            keys.RecvKey = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                keys.SendKey[i] = (byte)(i + 1);
                keys.RecvKey[i] = (byte)(0x40 + i);
            }
            keys.LocalIndex = 0x11111111;
            keys.RemoteIndex = 0x22222222;
            keys.IsInitiator = true;
            return keys;
        }

        /// <summary>Le due sessioni di un link: quella di A e lo specchio di B.</summary>
        private static void MakePair(out WgSession a, out WgSession b)
        {
            WgSessionKeys aKeys = TestKeys();
            WgSessionKeys bKeys = new WgSessionKeys();
            bKeys.SendKey = aKeys.RecvKey;
            bKeys.RecvKey = aKeys.SendKey;
            bKeys.LocalIndex = aKeys.RemoteIndex;
            bKeys.RemoteIndex = aKeys.LocalIndex;
            bKeys.IsInitiator = false;

            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            a = new WgSession(aKeys, now);
            b = new WgSession(bKeys, now);
        }

        private static void RoundTrip()
        {
            WgSession a;
            WgSession b;
            MakePair(out a, out b);
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            byte[] packet = new byte[1000];
            for (int i = 0; i < packet.Length; i++) packet[i] = (byte)(i * 7);

            byte[] message = a.Encapsulate(packet, now);
            byte[] opened = b.Decapsulate(message, now);

            T.True(opened != null, "il pacchetto si apre");
            if (opened == null) return;
            T.Eq(packet, opened, "round-trip byte-per-byte");
        }

        private static void TransportFormat()
        {
            WgSession a;
            WgSession b;
            MakePair(out a, out b);
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            byte[] message = a.Encapsulate(new byte[100], now);
            T.Eq(16 + 100 + 16, message.Length, "header + contenuto + tag");
            T.Eq(4, (int)message[0], "tipo 4");
            T.Eq(0x22222222, (int)UInt32At(message, 4), "indice del ricevente");
            T.Eq(0, (int)UInt32At(message, 8), "primo contatore a zero");
            T.Eq(0, (int)UInt32At(message, 12), "seconda meta' del contatore");

            byte[] second = a.Encapsulate(new byte[100], now);
            T.Eq(1, (int)UInt32At(second, 8), "il contatore avanza");
        }

        private static uint UInt32At(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | buffer[offset + 1] << 8
                          | buffer[offset + 2] << 16
                          | buffer[offset + 3] << 24);
        }

        private static void ReplayRejected()
        {
            WgSession a;
            WgSession b;
            MakePair(out a, out b);
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            byte[] message = a.Encapsulate(new byte[50], now);
            T.True(b.Decapsulate(message, now) != null, "prima volta: accettato");
            T.True(b.Decapsulate(message, now) == null, "seconda volta: replay rifiutato");
        }

        private static void KeepaliveIsEmpty()
        {
            WgSession a;
            WgSession b;
            MakePair(out a, out b);
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            byte[] message = a.Encapsulate(new byte[0], now);
            T.Eq(32, message.Length, "keepalive: solo header e tag");
            byte[] opened = b.Decapsulate(message, now);
            T.True(opened != null, "il keepalive si apre");
            if (opened == null) return;
            T.Eq(0, opened.Length, "keepalive: contenuto vuoto");
        }

        private static void Timers()
        {
            WgSession a;
            WgSession b;
            MakePair(out a, out b);
            DateTime start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            T.False(a.IsExpired(start), "non scaduta alla nascita");
            T.False(a.IsExpired(start.AddSeconds(WgConstants.RejectAfterSeconds - 1)),
                    "non scaduta a RejectAfterTime - 1");
            T.True(a.IsExpired(start.AddSeconds(WgConstants.RejectAfterSeconds + 1)),
                   "scaduta dopo RejectAfterTime");

            T.False(a.NeedsKeepalive(start.AddSeconds(WgConstants.KeepaliveSeconds - 1)),
                    "niente keepalive prima di KeepaliveTimeout");
            T.True(a.NeedsKeepalive(start.AddSeconds(WgConstants.KeepaliveSeconds + 1)),
                   "keepalive dopo il silenzio");

            T.False(a.NeedsRekey(start.AddSeconds(WgConstants.RekeyAfterSeconds - 1)),
                    "niente rekey prima di RekeyAfterTime");
            T.True(a.NeedsRekey(start.AddSeconds(WgConstants.RekeyAfterSeconds + 1)),
                   "rekey dopo RekeyAfterTime");

            // L'attivita' azzera il keepalive.
            a.Encapsulate(new byte[10], start.AddSeconds(WgConstants.KeepaliveSeconds - 1));
            T.False(a.NeedsKeepalive(start.AddSeconds(WgConstants.KeepaliveSeconds - 1)),
                    "l'invio recente azzera il keepalive");
        }

        private static void RejectAfterMessages()
        {
            WgSessionKeys aKeys = TestKeys();
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            WgSession a = new WgSession(aKeys, now, WgConstants.RejectAfterMessages);

            bool refused = false;
            try
            {
                a.Encapsulate(new byte[10], now);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            T.True(refused, "invio oltre RejectAfterMessages rifiutato");
        }
    }
}
