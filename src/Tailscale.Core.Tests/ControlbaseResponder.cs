using System;
using System.IO;
using System.Text;
using Tailscale.Core.Keys;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il lato server di controlbase, per i test.
    ///
    /// E' la trascrizione di `Server` in
    /// refs/tailscale/control/controlbase/handshake.go. Esiste solo per poter
    /// esercitare l'handshake completo in memoria: il vero test di
    /// interoperabilita' e' il dialogo con controlplane.tailscale.com (Task 9),
    /// questo verifica che i due ruoli dello stato simmetrico non divergono.
    /// </summary>
    public static class ControlbaseResponder
    {
        private const int InitiationLen = 101;
        private const int ResponseLen = 51;

        /// <summary>Accetta un handshake e restituisce la sessione del server.</summary>
        public static NoiseSession Accept(Stream stream, KeyPair controlKey, IRandomSource random, byte[] optionalInit)
        {
            SymmetricState s = new SymmetricState();
            s.Initialize();

            byte[] init = new byte[InitiationLen];
            if (optionalInit != null)
            {
                if (optionalInit.Length != InitiationLen) throw SendError(stream, "wrong handshake initiation size");
                Array.Copy(optionalInit, init, InitiationLen);
            }
            else
            {
                byte[] header = NoiseHandshake.ReadExact(stream, 3 + 2);
                Array.Copy(header, init, header.Length);
            }

            ushort clientVersion = (ushort)((init[0] << 8) | init[1]);
            if (init[2] != ProtocolConstants.MsgTypeInitiation) throw SendError(stream, "unexpected handshake message type");
            if ((ushort)((init[3] << 8) | init[4]) != InitiationLen - 5) throw SendError(stream, "wrong handshake initiation length");

            if (optionalInit == null)
            {
                byte[] payload = NoiseHandshake.ReadExact(stream, InitiationLen - 5);
                Array.Copy(payload, 0, init, 5, payload.Length);
            }

            s.MixHash(NoiseHandshake.ProtocolVersionPrologue(clientVersion));

            byte[] controlPublic = controlKey.PublicKey;
            s.MixHash(controlPublic);

            byte[] machineEphemeralPublic = Slice(init, 5, 32);
            s.MixHash(machineEphemeralPublic);
            byte[] key = s.MixDH(controlKey.PrivateKey, machineEphemeralPublic); // es
            byte[] machinePublic = s.DecryptAndHash(key, Slice(init, 37, 48));

            key = s.MixDH(controlKey.PrivateKey, machinePublic); // ss
            s.DecryptAndHash(key, Slice(init, 85, 16));

            byte[] response = new byte[ResponseLen];
            response[0] = ProtocolConstants.MsgTypeResponse;
            response[1] = (byte)((ResponseLen - 3) >> 8);
            response[2] = (byte)(ResponseLen - 3);

            KeyPair controlEphemeral = KeyPair.Generate(random);
            byte[] controlEphemeralPublic = controlEphemeral.PublicKey;
            Array.Copy(controlEphemeralPublic, 0, response, 3, 32);
            s.MixHash(controlEphemeralPublic);
            s.MixDH(controlEphemeral.PrivateKey, machineEphemeralPublic); // ee
            key = s.MixDH(controlEphemeral.PrivateKey, machinePublic); // se
            byte[] tag = s.EncryptAndHash(key, new byte[0]);
            Array.Copy(tag, 0, response, 35, tag.Length);

            byte[] txKey, rxKey;
            s.Split(out txKey, out rxKey);

            NoiseHandshake.WriteAll(stream, response);

            NoiseSession session = new NoiseSession();
            session.HandshakeHash = s.HandshakeHash;
            session.PeerKey = MachineKey.FromBytes(machinePublic);
            session.ProtocolVersion = clientVersion;
            // Nel server le due chiavi sono invertite rispetto al client.
            session.Tx = new CipherState(rxKey);
            session.Rx = new CipherState(txKey);
            return session;
        }

        /// <summary>
        /// Errore non autenticato sul filo, come sendErr del sorgente, seguito dalla
        /// chiusura dello stream per non lasciare il client in attesa.
        /// </summary>
        private static NoiseHandshakeException SendError(Stream stream, string message)
        {
            byte[] body = Encoding.ASCII.GetBytes(message);
            byte[] header = new byte[3];
            header[0] = ProtocolConstants.MsgTypeError;
            header[1] = (byte)(body.Length >> 8);
            header[2] = (byte)body.Length;

            try
            {
                NoiseHandshake.WriteAll(stream, header);
                NoiseHandshake.WriteAll(stream, body);
            }
            catch (IOException)
            {
                // Il client e' gia' andato via: l'errore da riportare e' comunque quello sotto.
            }
            stream.Close();
            return new NoiseHandshakeException("handshake rifiutato: " + message);
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }
    }
}
