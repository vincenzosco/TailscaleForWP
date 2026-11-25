using System;
using Tailscale.Core.Crypto;
using Tailscale.Core.Keys;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Wg
{
    /// <summary>Il ruolo di un lato dell'handshake.</summary>
    public enum WgRole
    {
        Initiator,
        Responder
    }

    /// <summary>Le chiavi di sessione derivate da un handshake completato.</summary>
    public sealed class WgSessionKeys
    {
        public byte[] SendKey;
        public byte[] RecvKey;

        /// <summary>h finale: identico sui due lati, impronta dell'handshake.</summary>
        public byte[] HandshakeHash;

        public uint LocalIndex;
        public uint RemoteIndex;
        public bool IsInitiator;
    }

    /// <summary>
    /// Lo stato di un handshake in corso, dall'initiation alla derivazione
    /// delle chiavi. Un'istanza serve un solo handshake.
    /// </summary>
    public sealed class WgHandshakeState
    {
        internal readonly SymmetricState Sym = new SymmetricState();
        internal KeyPair LocalEphemeral;
        internal byte[] RemoteStatic;
        internal byte[] RemoteEphemeral;

        public WgRole Role;
        public uint LocalIndex;
        public uint RemoteIndex;

        /// <summary>h corrente: l'impronta condivisa dai due lati.</summary>
        public byte[] HandshakeHash
        {
            get { return Sym.HandshakeHash; }
        }
    }

    /// <summary>
    /// L'handshake WireGuard (Noise IKpsk2), trascritto da
    /// refs/wireguard-go/device/noise-protocol.go: l'ordine dei mix e' quello
    /// del sorgente e delle note di docs/wireguard-notes.md, comprese le
    /// differenze dal Noise standard (il token `e` fa anche MixKey, i DH della
    /// response sono KDF1, il psk e' KDF3).
    ///
    /// Entrambi i ruoli servono: l'app initia, il test dei due lati usa anche
    /// il responder (che e' la stessa macchina vista dall'altra parte). Il PSK
    /// e' tutto zeri, come in Tailscale. Il mac2 resta zero: i cookie servono
    /// solo sotto carico e non sono in questa fase.
    /// </summary>
    public static class WgHandshake
    {
        /// <summary>
        /// Initiatore: produce il messaggio di initiation da 148 byte.
        /// Il timestamp TAI64N lo fornisce il chiamante (il core non legge
        /// l'orologio); la sorgente casuale e' iniettata per lo stesso motivo.
        /// </summary>
        public static byte[] Initiate(
            KeyPair localStatic, byte[] remoteStaticPublic, uint senderIndex,
            byte[] timestamp12, IRandomSource random, out WgHandshakeState state)
        {
            if (localStatic == null) throw new ArgumentNullException("localStatic");
            if (remoteStaticPublic == null) throw new ArgumentNullException("remoteStaticPublic");
            if (timestamp12 == null) throw new ArgumentNullException("timestamp12");
            if (timestamp12.Length != WgConstants.TimestampSize)
                throw new ArgumentException("il timestamp TAI64N ha 12 byte", "timestamp12");
            if (random == null) throw new ArgumentNullException("random");

            WgHandshakeState s = new WgHandshakeState();
            s.Role = WgRole.Initiator;
            s.RemoteStatic = (byte[])remoteStaticPublic.Clone();
            s.LocalIndex = senderIndex;

            s.Sym.Initialize(Common.ConstructionBytes());
            s.Sym.MixHash(Common.IdentifierBytes());
            s.Sym.MixHash(remoteStaticPublic);

            byte[] message = new byte[WgConstants.MessageInitiationSize];
            message[0] = WgConstants.MessageInitiationType;
            PutUInt32(message, 4, senderIndex);

            s.LocalEphemeral = KeyPair.Generate(random);
            Array.Copy(s.LocalEphemeral.PublicKey, 0, message, 8, 32);
            s.Sym.MixKey(s.LocalEphemeral.PublicKey);
            s.Sym.MixHash(s.LocalEphemeral.PublicKey);

            // es, ss: KDF2 sul DH, la chiave cifra subito.
            byte[] key = s.Sym.MixDH(s.LocalEphemeral.PrivateKey, remoteStaticPublic);
            byte[] encryptedStatic = s.Sym.EncryptAndHash(key, localStatic.PublicKey);
            Array.Copy(encryptedStatic, 0, message, 40, encryptedStatic.Length);

            key = s.Sym.MixDH(localStatic.PrivateKey, remoteStaticPublic);
            byte[] encryptedTimestamp = s.Sym.EncryptAndHash(key, timestamp12);
            Array.Copy(encryptedTimestamp, 0, message, 88, encryptedTimestamp.Length);

            byte[] mac1 = Mac1.Compute(remoteStaticPublic, message, message.Length - 32);
            Array.Copy(mac1, 0, message, message.Length - 32, WgConstants.MacSize);
            // mac2 resta zero: niente cookie.

            state = s;
            return message;
        }

        /// <summary>
        /// Responder: consuma un'initiation accettabile e restituisce lo stato
        /// da cui produrre la response. Null = rifiuto (formato, mac1, chiave
        /// sconosciuta, AEAD che non apre, timestamp in replay).
        ///
        /// Il controllo anti-flood sugli initiation ravvicinati resta al
        /// chiamante: qui il tempo non si legge (regola del core).
        /// </summary>
        public static WgHandshakeState AcceptInitiation(
            byte[] initiation, KeyPair localStatic, byte[] previousTimestamp12)
        {
            if (initiation == null) throw new ArgumentNullException("initiation");
            if (localStatic == null) throw new ArgumentNullException("localStatic");
            if (initiation.Length != WgConstants.MessageInitiationSize) return null;
            if (initiation[0] != WgConstants.MessageInitiationType) return null;

            // mac1 verificato con la statica propria: il destinatario siamo noi.
            if (!Mac1.Verify(localStatic.PublicKey, initiation, initiation.Length - 32)) return null;

            WgHandshakeState s = new WgHandshakeState();
            s.Role = WgRole.Responder;
            s.RemoteIndex = GetUInt32(initiation, 4);

            try
            {
                s.Sym.Initialize(Common.ConstructionBytes());
                s.Sym.MixHash(Common.IdentifierBytes());
                s.Sym.MixHash(localStatic.PublicKey);

                byte[] remoteEphemeral = new byte[32];
                Array.Copy(initiation, 8, remoteEphemeral, 0, 32);
                s.RemoteEphemeral = remoteEphemeral;
                s.Sym.MixHash(remoteEphemeral);
                s.Sym.MixKey(remoteEphemeral);

                byte[] encryptedStatic = Slice(initiation, 40, 48);
                byte[] key = s.Sym.MixDH(localStatic.PrivateKey, remoteEphemeral);
                byte[] remoteStatic = s.Sym.DecryptAndHash(key, encryptedStatic);
                if (remoteStatic.Length != 32) return null;

                byte[] encryptedTimestamp = Slice(initiation, 88, 28);
                key = s.Sym.MixDH(localStatic.PrivateKey, remoteStatic);
                byte[] timestamp = s.Sym.DecryptAndHash(key, encryptedTimestamp);
                if (timestamp.Length != WgConstants.TimestampSize) return null;

                // Replay: il timestamp deve essere strettamente maggiore.
                if (previousTimestamp12 != null
                    && Compare(timestamp, previousTimestamp12) <= 0) return null;

                s.RemoteStatic = remoteStatic;
            }
            catch (Exception)
            {
                // Un AEAD che non apre e' un rifiuto, non un guasto: non si
                // scrive niente e non si consegna niente al chiamante.
                return null;
            }
            return s;
        }

        /// <summary>
        /// Responder: produce la response da 92 byte dallo stato accettato.
        /// </summary>
        public static byte[] Respond(WgHandshakeState state, uint senderIndex, IRandomSource random)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (random == null) throw new ArgumentNullException("random");
            if (state.Role != WgRole.Responder)
                throw new InvalidOperationException("la response si crea solo dal lato responder");

            state.LocalIndex = senderIndex;
            state.LocalEphemeral = KeyPair.Generate(random);

            byte[] message = new byte[WgConstants.MessageResponseSize];
            message[0] = WgConstants.MessageResponseType;
            PutUInt32(message, 4, senderIndex);
            PutUInt32(message, 8, state.RemoteIndex);
            Array.Copy(state.LocalEphemeral.PublicKey, 0, message, 12, 32);

            state.Sym.MixHash(state.LocalEphemeral.PublicKey);
            state.Sym.MixKey(state.LocalEphemeral.PublicKey);
            // ee, se: KDF1, il secondo blocco non serve (la chiave viene dal psk).
            state.Sym.MixKey(X25519.ScalarMult(state.LocalEphemeral.PrivateKey, state.RemoteEphemeral));
            state.Sym.MixKey(X25519.ScalarMult(state.LocalEphemeral.PrivateKey, state.RemoteStatic));

            byte[] tau;
            byte[] key = state.Sym.MixPsk(ZeroPsk(), out tau);
            state.Sym.MixHash(tau);
            byte[] encryptedEmpty = state.Sym.EncryptAndHash(key, new byte[0]);
            Array.Copy(encryptedEmpty, 0, message, 44, encryptedEmpty.Length);

            // mac1 sulla statica dell'initiatore: il destinatario e' lui.
            byte[] mac1 = Mac1.Compute(state.RemoteStatic, message, message.Length - 32);
            Array.Copy(mac1, 0, message, message.Length - 32, WgConstants.MacSize);
            return message;
        }

        /// <summary>
        /// Initiatore: consuma la response e deriva le chiavi di sessione.
        /// Null = rifiuto (formato, mac1, indice, AEAD che non apre).
        /// </summary>
        public static WgSessionKeys Complete(
            WgHandshakeState state, KeyPair localStatic, byte[] response)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (localStatic == null) throw new ArgumentNullException("localStatic");
            if (response == null) throw new ArgumentNullException("response");
            if (state.Role != WgRole.Initiator)
                throw new InvalidOperationException("la response si consuma solo dal lato initiatore");
            if (response.Length != WgConstants.MessageResponseSize) return null;
            if (response[0] != WgConstants.MessageResponseType) return null;
            if (GetUInt32(response, 8) != state.LocalIndex) return null;

            // mac1 verificato con la statica propria: il destinatario siamo noi.
            if (!Mac1.Verify(localStatic.PublicKey, response, response.Length - 32)) return null;

            try
            {
                byte[] remoteEphemeral = new byte[32];
                Array.Copy(response, 12, remoteEphemeral, 0, 32);
                state.Sym.MixHash(remoteEphemeral);
                state.Sym.MixKey(remoteEphemeral);
                state.Sym.MixKey(X25519.ScalarMult(state.LocalEphemeral.PrivateKey, remoteEphemeral));
                state.Sym.MixKey(X25519.ScalarMult(localStatic.PrivateKey, remoteEphemeral));

                byte[] tau;
                byte[] key = state.Sym.MixPsk(ZeroPsk(), out tau);
                state.Sym.MixHash(tau);
                byte[] encryptedEmpty = Slice(response, 44, 16);
                byte[] empty = state.Sym.DecryptAndHash(key, encryptedEmpty);
                if (empty.Length != 0) return null;

                state.RemoteEphemeral = remoteEphemeral;
                state.RemoteIndex = GetUInt32(response, 4);
            }
            catch (Exception)
            {
                return null;
            }
            return Derive(state);
        }

        /// <summary>
        /// Deriva le chiavi di sessione: l'initiatore dopo
        /// <see cref="Complete"/>, il responder dopo <see cref="Respond"/>.
        /// L'initiatore manda con il primo blocco HKDF, il responder riceve.
        /// </summary>
        public static WgSessionKeys Derive(WgHandshakeState state)
        {
            if (state == null) throw new ArgumentNullException("state");

            byte[] first;
            byte[] second;
            state.Sym.Split(out first, out second);

            WgSessionKeys keys = new WgSessionKeys();
            keys.IsInitiator = state.Role == WgRole.Initiator;
            if (keys.IsInitiator)
            {
                keys.SendKey = first;
                keys.RecvKey = second;
            }
            else
            {
                keys.SendKey = second;
                keys.RecvKey = first;
            }
            keys.HandshakeHash = state.Sym.HandshakeHash;
            keys.LocalIndex = state.LocalIndex;
            keys.RemoteIndex = state.RemoteIndex;
            return keys;
        }

        /// <summary>Il PSK di Tailscale: tutto zeri.</summary>
        private static byte[] ZeroPsk()
        {
            return new byte[WgConstants.KeySize];
        }

        /// <summary>Confronto lessicografico dei byte, come bytes.Compare di Go.</summary>
        internal static int Compare(byte[] a, byte[] b)
        {
            int length = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        internal static void PutUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static uint GetUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | buffer[offset + 1] << 8
                          | buffer[offset + 2] << 16
                          | buffer[offset + 3] << 24);
        }

        private static byte[] Slice(byte[] buffer, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(buffer, offset, result, 0, length);
            return result;
        }
    }

    /// <summary>Le stringhe di costruzione gia' in byte, calcolate una volta.</summary>
    internal static class Common
    {
        private static readonly byte[] Construction =
            Tailscale.Core.Ascii.GetBytes(WgConstants.Construction);
        private static readonly byte[] Identifier =
            Tailscale.Core.Ascii.GetBytes(WgConstants.Identifier);

        internal static byte[] ConstructionBytes() { return Construction; }
        internal static byte[] IdentifierBytes() { return Identifier; }
    }
}
