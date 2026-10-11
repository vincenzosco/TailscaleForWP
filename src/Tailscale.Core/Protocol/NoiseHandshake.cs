using System;
using System.IO;
using System.Text;
using Tailscale.Core.Crypto;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Protocol
{
    /// <summary>Risultato di un handshake completato: chiavi di trasporto e identita' del peer.</summary>
    public sealed class NoiseSession
    {
        public byte[] HandshakeHash;
        public MachineKey PeerKey;
        public ushort ProtocolVersion;
        public CipherState Tx;
        public CipherState Rx;
    }

    /// <summary>Il seguito di un handshake avviato in modo differito.</summary>
    public interface INoiseContinued
    {
        /// <summary>Legge la risposta del server e restituisce la sessione cifrata.</summary>
        NoiseSession Complete(Stream stream);
    }

    /// <summary>Primo messaggio piu' il seguito, per risparmiare un giro di rete.</summary>
    public sealed class NoiseHandshakeStart
    {
        public byte[] InitialMessage;
        public INoiseContinued Continuation;
    }

    /// <summary>
    /// Handshake Noise IK del canale di controllo Tailscale (controlbase).
    ///
    /// Trascritto da refs/tailscale/control/controlbase/handshake.go, funzione
    /// ClientDeferred e continueClientHandshake. L'ordine dei MixHash e dei MixDH
    /// e' quello del sorgente: il prologo, poi la chiave pubblica del server, poi
    /// l'effimera, poi es, s, ss in scrittura e ee, se in lettura.
    ///
    /// Il messaggio iniziale viene restituito separatamente perche' il dialer lo
    /// puo' spedire dentro la richiesta di upgrade HTTP, evitando un RTT.
    /// </summary>
    public static class NoiseHandshake
    {
        /// <summary>Avvia l'handshake e restituisce il primo messaggio e il seguito.</summary>
        public static NoiseHandshakeStart Deferred(
            KeyPair machineKey, MachineKey controlKey, ushort protocolVersion, IRandomSource random)
        {
            if (machineKey == null) throw new ArgumentNullException("machineKey");
            if (controlKey == null) throw new ArgumentNullException("controlKey");
            if (random == null) throw new ArgumentNullException("random");

            SymmetricState s = new SymmetricState();
            s.Initialize();
            s.MixHash(ProtocolVersionPrologue(protocolVersion));
            s.MixHash(controlKey.Bytes);

            byte[] initiation = new byte[ProtocolConstants.InitiationMessageLen];
            PutUInt16(initiation, 0, protocolVersion);
            initiation[2] = ProtocolConstants.MsgTypeInitiation;
            PutUInt16(initiation, 3, (ushort)(ProtocolConstants.InitiationMessageLen - ProtocolConstants.InitiationHeaderLen));

            KeyPair ephemeral = KeyPair.Generate(random);
            byte[] ephemeralPublic = ephemeral.PublicKey;
            Array.Copy(ephemeralPublic, 0, initiation, 5, 32);
            s.MixHash(ephemeralPublic);

            byte[] key = s.MixDH(ephemeral.PrivateKey, controlKey.Bytes);
            byte[] encryptedMachine = s.EncryptAndHash(key, machineKey.PublicKey);
            Array.Copy(encryptedMachine, 0, initiation, 37, encryptedMachine.Length);

            key = s.MixDH(machineKey.PrivateKey, controlKey.Bytes);
            byte[] tag = s.EncryptAndHash(key, new byte[0]);
            Array.Copy(tag, 0, initiation, 85, tag.Length);

            NoiseHandshakeStart start = new NoiseHandshakeStart();
            start.InitialMessage = initiation;
            start.Continuation = new ClientContinuation(s, machineKey, ephemeral, controlKey, protocolVersion);
            return start;
        }

        /// <summary>"Tailscale Control Protocol v" + versione in decimale.</summary>
        public static byte[] ProtocolVersionPrologue(ushort version)
        {
            return Tailscale.Core.Ascii.GetBytes(ProtocolConstants.ProloguePrefix + version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private sealed class ClientContinuation : INoiseContinued
        {
            private readonly SymmetricState _state;
            private readonly KeyPair _machineKey;
            private readonly KeyPair _ephemeral;
            private readonly MachineKey _controlKey;
            private readonly ushort _protocolVersion;
            private bool _used;

            public ClientContinuation(
                SymmetricState state, KeyPair machineKey, KeyPair ephemeral, MachineKey controlKey, ushort protocolVersion)
            {
                _state = state;
                _machineKey = machineKey;
                _ephemeral = ephemeral;
                _controlKey = controlKey;
                _protocolVersion = protocolVersion;
            }

            public NoiseSession Complete(Stream stream)
            {
                if (stream == null) throw new ArgumentNullException("stream");
                if (_used) throw new InvalidOperationException("handshake gia' completato: il seguito e' monouso");
                _used = true;

                byte[] header = ReadExact(stream, ProtocolConstants.HeaderLen);
                byte type = header[0];
                int length = ReadUInt16(header, 1);

                if (type == ProtocolConstants.MsgTypeError)
                {
                    // Il messaggio di errore non e' autenticato: va trattato come
                    // un suggerimento, non come una prova.
                    byte[] message = ReadExact(stream, length);
                    throw new NoiseHandshakeException(
                        "il server ha rifiutato l'handshake: " + Sanitize(message));
                }
                if (type != ProtocolConstants.MsgTypeResponse)
                    throw new NoiseHandshakeException("tipo di messaggio inatteso " + type + ", atteso " + ProtocolConstants.MsgTypeResponse);

                int payloadLength = ProtocolConstants.ResponseMessageLen - ProtocolConstants.HeaderLen;
                if (length != payloadLength)
                    throw new NoiseHandshakeException("lunghezza " + length + " per la risposta di handshake, attesa " + payloadLength);

                byte[] payload = ReadExact(stream, length);
                byte[] controlEphemeralPublic = Slice(payload, 0, 32);
                byte[] tag = Slice(payload, 32, payload.Length - 32);

                _state.MixHash(controlEphemeralPublic);
                _state.MixDH(_ephemeral.PrivateKey, controlEphemeralPublic); // ee
                byte[] key = _state.MixDH(_machineKey.PrivateKey, controlEphemeralPublic); // se
                _state.DecryptAndHash(key, tag);

                byte[] txKey, rxKey;
                _state.Split(out txKey, out rxKey);

                NoiseSession session = new NoiseSession();
                session.HandshakeHash = _state.HandshakeHash;
                session.PeerKey = _controlKey;
                session.ProtocolVersion = _protocolVersion;
                session.Tx = new CipherState(txKey);
                session.Rx = new CipherState(rxKey);
                return session;
            }
        }

        /// <summary>Legge esattamente count byte, o lancia se lo stream si chiude prima.</summary>
        public static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("stream chiuso dopo " + offset + " di " + count + " byte");
                offset += read;
            }
            return buffer;
        }

        /// <summary>Scrive tutti i byte; WriteAll perche' Stream.Write puo' scrivere meno.</summary>
        public static void WriteAll(Stream stream, byte[] data)
        {
            stream.Write(data, 0, data.Length);
        }

        private static string Sanitize(byte[] message)
        {
            StringBuilder sb = new StringBuilder(message.Length);
            foreach (byte b in message)
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            return sb.ToString();
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static void PutUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static int ReadUInt16(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }
    }

    /// <summary>Errore di handshake: protocollo violato o autenticazione fallita.</summary>
    public class NoiseHandshakeException : Exception
    {
        public NoiseHandshakeException(string message) : base(message) { }
    }
}
