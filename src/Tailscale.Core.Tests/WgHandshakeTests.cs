using System;
using Tailscale.Core.Keys;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// L'handshake WireGuard (Noise IKpsk2) contro i vettori generati da
    /// tools/wg-vectors.py con un'implementazione indipendente (BoringSSL):
    /// gli stessi byte devono uscire dal core C#, senza condividere una riga.
    ///
    /// Copre anche i rifiuti: mac1 errato, timestamp in replay, risposta
    /// manomessa. Un handshake non deve mai cadere in silenzio.
    /// </summary>
    public static class WgHandshakeTests
    {
        public static void Run()
        {
            Case("wg: initiation ai byte del vettore", InitiationMatchesVector);
            Case("wg: response ai byte del vettore", ResponseMatchesVector);
            Case("wg: chiavi di sessione e hash del vettore", SessionKeysMatchVector);
            Case("wg: mac1 errato rifiuta l'initiation", BadMac1Rejected);
            Case("wg: timestamp in replay rifiuta", ReplayRejected);
            Case("wg: risposta manomessa rifiuta", TamperedResponseRejected);
            Case("wg: handshake a due lati, chiavi simmetriche", TwoSidedAgreement);
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

        /// <summary>Casualità fissa: restituisce sempre lo stesso blocco di 32 byte.</summary>
        private sealed class FixedRandomSource : IRandomSource
        {
            private readonly byte[] _fill;

            public FixedRandomSource(byte[] fill) { _fill = fill; }

            public void NextBytes(byte[] buffer)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = _fill[i % _fill.Length];
            }
        }

        private static KeyPair InitStatic
        {
            get { return KeyPair.FromPrivateKey(WgVectors.InitStaticPriv); }
        }

        private static KeyPair RespStatic
        {
            get { return KeyPair.FromPrivateKey(WgVectors.RespStaticPriv); }
        }

        private static void InitiationMatchesVector()
        {
            WgHandshakeState state;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out state);

            T.Eq(WgVectors.Initiation, initiation, "initiation ai byte del vettore");
            T.Eq(148, initiation.Length, "initiation: 148 byte");
        }

        private static void ResponseMatchesVector()
        {
            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out initiator);

            WgHandshakeState responder = WgHandshake.AcceptInitiation(initiation, RespStatic, null);
            T.True(responder != null, "il responder accetta l'initiation");
            if (responder == null) return;

            byte[] response = WgHandshake.Respond(
                responder, 0x55667788, new FixedRandomSource(WgVectors.RespEphPriv));

            T.Eq(WgVectors.Response, response, "response ai byte del vettore");
            T.Eq(92, response.Length, "response: 92 byte");
        }

        private static void SessionKeysMatchVector()
        {
            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out initiator);

            WgHandshakeState responder = WgHandshake.AcceptInitiation(initiation, RespStatic, null);
            T.True(responder != null, "il responder accetta");
            if (responder == null) return;
            byte[] response = WgHandshake.Respond(
                responder, 0x55667788, new FixedRandomSource(WgVectors.RespEphPriv));

            WgSessionKeys keys = WgHandshake.Complete(initiator, InitStatic, response);
            T.True(keys != null, "l'initiatore completa");
            if (keys == null) return;

            T.Eq(WgVectors.InitiatorSendKey, keys.SendKey, "send key dell'initiatore");
            T.Eq(WgVectors.InitiatorRecvKey, keys.RecvKey, "recv key dell'initiatore");
            T.Eq(WgVectors.HandshakeHash, keys.HandshakeHash, "hash finale dell'handshake");

            WgSessionKeys responderKeys = WgHandshake.Derive(responder);
            T.Eq(keys.SendKey, responderKeys.RecvKey, "il recv del responder e' il send dell'altro");
            T.Eq(keys.RecvKey, responderKeys.SendKey, "il send del responder e' il recv dell'altro");
        }

        private static void BadMac1Rejected()
        {
            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out initiator);
            initiation[116] ^= 0x01; // un bit di mac1

            T.True(WgHandshake.AcceptInitiation(initiation, RespStatic, null) == null,
                   "mac1 errato: rifiuto");
        }

        private static void ReplayRejected()
        {
            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out initiator);

            T.True(WgHandshake.AcceptInitiation(initiation, RespStatic, WgVectors.Timestamp) == null,
                   "timestamp uguale al precedente: rifiuto");
        }

        private static void TamperedResponseRejected()
        {
            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                InitStatic, WgVectors.RespStaticPub, 0x11223344,
                WgVectors.Timestamp, new FixedRandomSource(WgVectors.InitEphPriv),
                out initiator);
            WgHandshakeState responder = WgHandshake.AcceptInitiation(initiation, RespStatic, null);
            if (responder == null) { T.Failures++; return; }
            byte[] response = WgHandshake.Respond(
                responder, 0x55667788, new FixedRandomSource(WgVectors.RespEphPriv));
            response[44] ^= 0x01; // il tag AEAD del payload vuoto

            T.True(WgHandshake.Complete(initiator, InitStatic, response) == null,
                   "risposta manomessa: rifiuto");
        }

        private static void TwoSidedAgreement()
        {
            KeyPair a = KeyPair.Generate(new SequenceRandomSource(1));
            KeyPair b = KeyPair.Generate(new SequenceRandomSource(2));

            WgHandshakeState initiator;
            byte[] initiation = WgHandshake.Initiate(
                a, b.PublicKey, 7, WgVectors.Timestamp,
                new SequenceRandomSource(3), out initiator);
            WgHandshakeState responder = WgHandshake.AcceptInitiation(initiation, b, null);
            T.True(responder != null, "due lati noti: accettata");
            if (responder == null) return;
            byte[] response = WgHandshake.Respond(responder, 8, new SequenceRandomSource(4));

            WgSessionKeys initiatorKeys = WgHandshake.Complete(initiator, a, response);
            WgSessionKeys responderKeys = WgHandshake.Derive(responder);
            T.True(initiatorKeys != null && responderKeys != null, "entrambi i lati completano");
            if (initiatorKeys == null || responderKeys == null) return;

            T.Eq(initiatorKeys.SendKey, responderKeys.RecvKey, "send/recv incrociati");
            T.Eq(initiatorKeys.RecvKey, responderKeys.SendKey, "recv/send incrociati");
            T.Eq(7, (int)initiatorKeys.LocalIndex, "indice locale dell'initiatore");
            T.Eq(8, (int)initiatorKeys.RemoteIndex, "indice remoto dell'initiatore");
        }
    }
}
