using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Verifica che le costanti di protocollo siano quelle lette dal tree
    /// vendored e che l'aritmetica delle lunghezze torni. Il confronto col
    /// sorgente Go lo fa tools/check-protocol-pin.py; qui si blocca l'aritmetica
    /// che uno script di grep non puo' controllare.
    /// </summary>
    public static class ProtocolConstantsTests
    {
        public static void Run()
        {
            T.Eq("/ts2021", ProtocolConstants.UpgradePath, "path di upgrade");
            T.Eq("tailscale-control-protocol", ProtocolConstants.UpgradeHeaderValue, "valore header Upgrade");
            T.Eq("X-Tailscale-Handshake", ProtocolConstants.HandshakeHeaderName, "header di handshake");
            T.Eq("Noise_IK_25519_ChaChaPoly_BLAKE2s", ProtocolConstants.NoiseInstanceName, "istanza Noise");
            T.Eq("Tailscale Control Protocol v", ProtocolConstants.ProloguePrefix, "prologo");
            T.Eq(148, ProtocolConstants.CapabilityVersion, "capability version");
            T.Eq("/machine/register", ProtocolConstants.RegisterPath, "endpoint register");
            T.Eq("/machine/map", ProtocolConstants.MapPath, "endpoint map");

            // Layout dei messaggi di handshake (controlbase/messages.go).
            T.Eq(101, ProtocolConstants.InitiationMessageLen, "lunghezza messaggio di initiation");
            T.Eq(51, ProtocolConstants.ResponseMessageLen, "lunghezza messaggio di response");

            // Aritmetica delle lunghezze: initiation = 5 + 32 + 48 + 16,
            // response = 3 + 32 + 16. Se cambia una parte, deve tornare il totale.
            int initiation = ProtocolConstants.InitiationHeaderLen + 32 + 48 + ProtocolConstants.AeadTagLen;
            T.Eq(ProtocolConstants.InitiationMessageLen, initiation, "aritmetica dell'initiation (5+32+48+16)");
            int response = ProtocolConstants.HeaderLen + 32 + ProtocolConstants.AeadTagLen;
            T.Eq(ProtocolConstants.ResponseMessageLen, response, "aritmetica della response (3+32+16)");

            // Framing del transport: tipo + lunghezza, poi ciphertext (plaintext + tag).
            T.Eq(4, ProtocolConstants.MsgTypeRecord, "tipo record");
            T.Eq(4093, ProtocolConstants.MaxCiphertextSize, "ciphertext massimo = 4096 - 3 header");
            T.Eq(4077, ProtocolConstants.MaxPlaintextSize, "plaintext massimo = 4093 - 16 tag");
            T.True(ProtocolConstants.MsgTypeInitiation != ProtocolConstants.MsgTypeRecord, "i tipi di messaggio sono distinti");
        }
    }
}
