using System;
using Tailscale.Core.Control;
using Tailscale.Core.Protocol;

namespace Tailscale.Cli
{
    /// <summary>
    /// Controlli offline della CLI: nessuna rete, quindi eseguibili nella pipeline
    /// di verifica. Verificano il parser delle chiavi del server e la forma della
    /// richiesta di upgrade, cioe' le due cose su cui 'handshake' si appoggia.
    /// </summary>
    public static class CommandSelfTest
    {
        private const string SampleHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
        private const string MKey = "mkey:" + SampleHex;

        public static int Run()
        {
            int failures = 0;

            failures += Check("chiavi: publicKey e legacyPublicKey", delegate()
            {
                ControlServerKeys keys = ControlServerKeyLoader.Parse(
                    "{\"publicKey\":\"" + MKey + "\",\"legacyPublicKey\":\"" + MKey + "\"}");
                return keys.NoiseKey.ToString() == MKey && keys.LegacyKey.ToString() == MKey;
            });

            failures += Check("chiavi: legacy assente non e' un errore", delegate()
            {
                ControlServerKeys keys = ControlServerKeyLoader.Parse("{\"publicKey\":\"" + MKey + "\",\"v\":148}");
                return keys.NoiseKey.ToString() == MKey && keys.LegacyKey == null;
            });

            failures += Check("chiavi: campi sconosciuti ignorati", delegate()
            {
                ControlServerKeys keys = ControlServerKeyLoader.Parse(
                    "{\"publicKey\":\"" + MKey + "\",\"nuovoCampo\":{\"a\":[1,2,3]},\"altro\":true}");
                return keys.NoiseKey.ToString() == MKey;
            });

            failures += Check("chiavi: publicKey mancante viene rifiutato", delegate()
            {
                try
                {
                    ControlServerKeyLoader.Parse("{\"legacyPublicKey\":\"" + MKey + "\"}");
                    return false;
                }
                catch (ControlServerKeyException)
                {
                    return true;
                }
            });

            failures += Check("richiesta: POST /ts2021 con gli header di upgrade", delegate()
            {
                string head = ControlHttpDialer.BuildRequestHead("controlplane.tailscale.com", "QUJD");
                return head.StartsWith("POST " + ProtocolConstants.UpgradePath + " HTTP/1.1\r\n", StringComparison.Ordinal)
                    && head.IndexOf("Host: controlplane.tailscale.com\r\n", StringComparison.Ordinal) > 0
                    && head.IndexOf(ProtocolConstants.UpgradeHeaderName + ": " + ProtocolConstants.UpgradeHeaderValue + "\r\n", StringComparison.Ordinal) > 0
                    && head.IndexOf(ProtocolConstants.ConnectionHeaderName + ": " + ProtocolConstants.ConnectionHeaderValue + "\r\n", StringComparison.Ordinal) > 0
                    && head.IndexOf(ProtocolConstants.HandshakeHeaderName + ": QUJD\r\n", StringComparison.Ordinal) > 0
                    && head.EndsWith("\r\n\r\n", StringComparison.Ordinal);
            });

            failures += Check("richiesta: l'header di handshake compare una sola volta", delegate()
            {
                string head = ControlHttpDialer.BuildRequestHead("controlplane.tailscale.com", "QUJD");
                string needle = ProtocolConstants.HandshakeHeaderName + ":";
                return head.IndexOf(needle, StringComparison.Ordinal)
                     == head.LastIndexOf(needle, StringComparison.Ordinal);
            });

            Console.WriteLine(failures == 0 ? "CLI SELFTEST OK" : "CLI SELFTEST FALLITO: " + failures);
            return failures;
        }

        private static int Check(string name, Func<bool> test)
        {
            try
            {
                if (test()) return 0;
                Console.Error.WriteLine("FAIL " + name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            return 1;
        }
    }
}
