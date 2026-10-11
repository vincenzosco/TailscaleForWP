using System;
using System.IO;
using System.Text;
using System.Threading;
using Tailscale.Core.Keys;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Handshake Noise IK di controlbase.
    ///
    /// Cosa verifica questo file: che i due ruoli dello stato simmetrico
    /// concordano, che il layout dei messaggi sia quello del sorgente, che il
    /// prologo conti, e che OGNI byte dei due messaggi sia autenticato (basta
    /// invertire un bit e l'handshake deve fallire da entrambi i lati).
    ///
    /// Cosa NON verifica: l'accordo con il server reale. Non c'e' un Vector
    /// indipendente ottenibile senza Go (noiseexplorer_test.go non e' eseguibile
    /// qui e noise-handshake di npm usa BLAKE2b, non BLAKE2s). Quel confronto e' il
    /// dialogo con controlplane.tailscale.com in Task 9.
    /// </summary>
    public static class NoiseHandshakeTests
    {
        private const ushort Version = 148;

        public static void Run()
        {
            MessageLayout();
            PrologueMatters();
            RolesAgree();
            TransportInteroperates();
            TamperedInitiationRejected();
            TamperedResponseRejected();
            RejectsReuse();
        }

        private static void MessageLayout()
        {
            KeyPair machine = new KeyPairGenerator(1).Generate();
            KeyPair control = new KeyPairGenerator(2).Generate();

            NoiseHandshakeStart start = NoiseHandshake.Deferred(
                machine, MachineKey.FromBytes(control.PublicKey), Version, new SequenceRandomSource());

            byte[] init = start.InitialMessage;
            T.Eq(ProtocolConstants.InitiationMessageLen, init.Length, "controlbase: l'initiation e' di 101 byte");
            T.Eq(Version, (ushort)((init[0] << 8) | init[1]), "controlbase: la versione e' nei primi 2 byte, big-endian");
            T.Eq((int)ProtocolConstants.MsgTypeInitiation, (int)init[2], "controlbase: il tipo e' 1");
            T.Eq(96, (init[3] << 8) | init[4], "controlbase: la lunghezza del payload e' 96");

            // L'effimera e' in chiaro, la chiave della macchina no.
            byte[] ephemeral = Slice(init, 5, 32);
            T.False(Equal(ephemeral, new byte[32]), "controlbase: l'effimera non e' tutto zero");
            T.False(
                Equal(Slice(init, 37, 32), machine.PublicKey),
                "controlbase: la chiave della macchina e' cifrata, non in chiaro");

            // La risposta ha un layout diverso: nessuna versione, header di 3 byte.
            Stream clientStream, serverStream;
            InMemoryDuplex.Create(out clientStream, out serverStream);
            byte[] response = CaptureResponse(clientStream, serverStream, machine, control);

            T.Eq(ProtocolConstants.ResponseMessageLen, response.Length, "controlbase: la risposta e' di 51 byte");
            T.Eq((int)ProtocolConstants.MsgTypeResponse, (int)response[0], "controlbase: il tipo della risposta e' 2");
            T.Eq(48, (response[1] << 8) | response[2], "controlbase: la lunghezza del payload della risposta e' 48");
            T.Eq(51, ProtocolConstants.HeaderLen + 48, "controlbase: 3 + 48 = 51");
        }

        private static void PrologueMatters()
        {
            KeyPair machine = new KeyPairGenerator(3).Generate();
            KeyPair control = new KeyPairGenerator(4).Generate();
            MachineKey controlPublic = MachineKey.FromBytes(control.PublicKey);

            NoiseHandshakeStart version1 = NoiseHandshake.Deferred(machine, controlPublic, 1, new SequenceRandomSource());
            NoiseHandshakeStart version2 = NoiseHandshake.Deferred(machine, controlPublic, 2, new SequenceRandomSource());

            T.False(
                Equal(Slice(version1.InitialMessage, 85, 16), Slice(version2.InitialMessage, 85, 16)),
                "controlbase: la versione nel prologo cambia il tag");

            T.Eq(
                Encoding.ASCII.GetBytes("Tailscale Control Protocol v148"),
                NoiseHandshake.ProtocolVersionPrologue(148),
                "controlbase: prologo = prefisso + versione decimale");
        }

        private static void RolesAgree()
        {
            KeyPair machine = new KeyPairGenerator(5).Generate();
            KeyPair control = new KeyPairGenerator(6).Generate();

            NoiseSession client, server;
            Exception clientError, serverError;
            Exchange(machine, control, out client, out clientError, out server, out serverError);

            T.True(clientError == null, "controlbase: il client completa l'handshake");
            T.True(serverError == null, "controlbase: il server completa l'handshake");
            if (client == null || server == null) return;

            T.Eq(client.HandshakeHash, server.HandshakeHash, "controlbase: i due lati concordano sull'handshake hash");
            T.Eq(MachineKey.FromBytes(control.PublicKey).ToString(), client.PeerKey.ToString(),
                 "controlbase: il client vede la chiave del server");
            T.Eq(MachineKey.FromBytes(machine.PublicKey).ToString(), server.PeerKey.ToString(),
                 "controlbase: il server vede la chiave del client");
            T.Eq(Version, client.ProtocolVersion, "controlbase: la versione concordata e' quella richiesta");
        }

        private static void TransportInteroperates()
        {
            KeyPair machine = new KeyPairGenerator(7).Generate();
            KeyPair control = new KeyPairGenerator(8).Generate();

            NoiseSession client, server;
            Exception clientError, serverError;
            Exchange(machine, control, out client, out clientError, out server, out serverError);
            if (client == null || server == null) return;

            byte[] fromClient = Encoding.ASCII.GetBytes("registrazione del nodo");
            T.Eq(fromClient, server.Rx.Open(client.Tx.Seal(fromClient)),
                 "controlbase: client->server cifra e decifra");

            byte[] fromServer = Encoding.ASCII.GetBytes("risposta del server");
            T.Eq(fromServer, client.Rx.Open(server.Tx.Seal(fromServer)),
                 "controlbase: server->client cifra e decifra");

            byte[] second = new byte[0];
            T.Eq(second, server.Rx.Open(client.Tx.Seal(second)),
                 "controlbase: anche un frame vuoto passa, e il contatore avanza");
            T.Eq(2, (int)client.Tx.Counter, "controlbase: il contatore del client e' a 2");
        }

        private static void TamperedInitiationRejected()
        {
            int survived = 0;
            for (int i = 0; i < ProtocolConstants.InitiationMessageLen; i++)
            {
                KeyPair machine = new KeyPairGenerator(9).Generate();
                KeyPair control = new KeyPairGenerator(10).Generate();

                Stream clientStream, serverStream;
                InMemoryDuplex.Create(out clientStream, out serverStream);
                Stream tampered = new TamperingStream(serverStream, i);

                NoiseSession client, server;
                Exception clientError, serverError;
                Exchange(clientStream, tampered, machine, control, out client, out clientError, out server, out serverError);

                if (clientError == null || serverError == null) survived++;
            }
            T.Eq(0, survived, "controlbase: invertire un bit dell'initiation fa fallire l'handshake (101 posizioni)");
        }

        private static void TamperedResponseRejected()
        {
            int survived = 0;
            for (int i = 0; i < ProtocolConstants.ResponseMessageLen; i++)
            {
                KeyPair machine = new KeyPairGenerator(11).Generate();
                KeyPair control = new KeyPairGenerator(12).Generate();

                Stream clientStream, serverStream;
                InMemoryDuplex.Create(out clientStream, out serverStream);
                Stream tampered = new TamperingStream(clientStream, i);

                NoiseSession client, server;
                Exception clientError, serverError;
                Exchange(tampered, serverStream, machine, control, out client, out clientError, out server, out serverError);

                if (clientError == null) survived++;
            }
            T.Eq(0, survived, "controlbase: invertire un bit della risposta fa fallire il client (51 posizioni)");
        }

        private static void RejectsReuse()
        {
            KeyPair machine = new KeyPairGenerator(13).Generate();
            KeyPair control = new KeyPairGenerator(14).Generate();
            MachineKey controlPublic = MachineKey.FromBytes(control.PublicKey);

            NoiseHandshakeStart start = NoiseHandshake.Deferred(machine, controlPublic, Version, new SequenceRandomSource());

            Stream clientStream, serverStream;
            InMemoryDuplex.Create(out clientStream, out serverStream);
            NoiseHandshake.WriteAll(clientStream, start.InitialMessage);
            ControlbaseResponder.Accept(serverStream, control, new SequenceRandomSource(), null);

            // Il seguito si prova a riusare: il controllo e' prima di ogni lettura,
            // quindi il secondo tentativo non resta in attesa di byte che non arrivano.
            start.Continuation.Complete(clientStream);
            T.Throws(delegate { start.Continuation.Complete(clientStream); },
                     "controlbase: il seguito dell'handshake e' monouso");
        }

        /// <summary>Esegue un handshake completo in memoria su due thread.</summary>
        private static void Exchange(
            KeyPair machineKey, KeyPair controlKey,
            out NoiseSession clientSession, out Exception clientError,
            out NoiseSession serverSession, out Exception serverError)
        {
            Stream clientStream, serverStream;
            InMemoryDuplex.Create(out clientStream, out serverStream);
            Exchange(clientStream, serverStream, machineKey, controlKey,
                     out clientSession, out clientError, out serverSession, out serverError);
        }

        private static void Exchange(
            Stream clientStream, Stream serverStream,
            KeyPair machineKey, KeyPair controlKey,
            out NoiseSession clientSession, out Exception clientError,
            out NoiseSession serverSession, out Exception serverError)
        {
            NoiseSession server = null;
            Exception serverFailure = null;

            Thread serverThread = new Thread(delegate()
            {
                try
                {
                    server = ControlbaseResponder.Accept(serverStream, controlKey, new SequenceRandomSource(0x80), null);
                }
                catch (Exception ex)
                {
                    serverFailure = ex;
                    try { serverStream.Close(); }
                    catch (Exception) { }
                }
            });
            serverThread.IsBackground = true;
            serverThread.Start();

            NoiseSession client = null;
            Exception clientFailure = null;
            try
            {
                NoiseHandshakeStart start = NoiseHandshake.Deferred(
                    machineKey, MachineKey.FromBytes(controlKey.PublicKey), Version, new SequenceRandomSource());
                NoiseHandshake.WriteAll(clientStream, start.InitialMessage);
                client = start.Continuation.Complete(clientStream);
            }
            catch (Exception ex)
            {
                clientFailure = ex;
                try { clientStream.Close(); }
                catch (Exception) { }
            }

            if (!serverThread.Join(10000))
            {
                clientFailure = new TimeoutException("il lato server non ha completato l'handshake (deadlock)");
            }

            clientSession = client;
            clientError = clientFailure;
            serverSession = server;
            serverError = serverFailure;
        }

        /// <summary>Esegue solo il lato server e restituisce i byte della risposta.</summary>
        private static byte[] CaptureResponse(Stream clientStream, Stream serverStream, KeyPair machineKey, KeyPair controlKey)
        {
            MachineKey controlPublic = MachineKey.FromBytes(controlKey.PublicKey);
            NoiseHandshakeStart start = NoiseHandshake.Deferred(machineKey, controlPublic, Version, new SequenceRandomSource());
            NoiseHandshake.WriteAll(clientStream, start.InitialMessage);

            ControlbaseResponder.Accept(serverStream, controlKey, new SequenceRandomSource(0x80), null);
            byte[] header = NoiseHandshake.ReadExact(clientStream, 3);
            byte[] body = NoiseHandshake.ReadExact(clientStream, (header[1] << 8) | header[2]);

            byte[] response = new byte[3 + body.Length];
            Array.Copy(header, response, 3);
            Array.Copy(body, 0, response, 3, body.Length);
            return response;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>Genera coppie deterministiche con un seme diverso per ruolo.</summary>
    public sealed class KeyPairGenerator
    {
        private readonly SequenceRandomSource _random;

        public KeyPairGenerator(byte seed)
        {
            _random = new SequenceRandomSource(seed);
        }

        public KeyPair Generate()
        {
            return KeyPair.Generate(_random);
        }
    }
}
