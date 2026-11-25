using System;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Wg
{
    /// <summary>
    /// La sessione di trasporto WireGuard: incapsula pacchetti IP nei
    /// messaggi di tipo 4 e li estrae, con il contatore di nonce, la finestra
    /// anti-replay e i timer.
    ///
    /// Formato (device/noise-protocol.go:82-106): type(4) receiver(4)
    /// counter(8) ciphertext. Il nonce AEAD e' il contatore little-endian in
    /// nonce[4:12] (send.go:599, receive.go:250), senza AAD. Nessun padding:
    /// il layer IP ritaglia con totalLength.
    ///
    /// Istante per istante il tempo arriva dal chiamante (il core non legge
    /// l'orologio) e i contatori sono protetti da lock: piu' connessioni
    /// possono condividere la stessa sessione.
    /// </summary>
    public sealed class WgSession
    {
        private readonly object _gate = new object();
        private readonly ChaCha20Poly1305 _send;
        private readonly ChaCha20Poly1305 _receive;
        private readonly ReplayWindow _replay = new ReplayWindow();
        private readonly WgTimers _timers;
        private ulong _sendCounter;

        public uint LocalIndex { get; private set; }
        public uint RemoteIndex { get; private set; }

        public WgSession(WgSessionKeys keys, DateTime createdAt)
            : this(keys, createdAt, 0)
        {
        }

        /// <summary>
        /// Come il costruttore semplice, con il contatore di invio da cui
        /// ripartire: serve a ripristinare una sessione, non solo ai test.
        /// </summary>
        public WgSession(WgSessionKeys keys, DateTime createdAt, ulong nextSendCounter)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            if (keys.SendKey == null || keys.SendKey.Length != WgConstants.KeySize)
                throw new ArgumentException("la chiave di invio ha 32 byte", "keys");
            if (keys.RecvKey == null || keys.RecvKey.Length != WgConstants.KeySize)
                throw new ArgumentException("la chiave di ricezione ha 32 byte", "keys");

            _send = new ChaCha20Poly1305(keys.SendKey);
            _receive = new ChaCha20Poly1305(keys.RecvKey);
            _timers = new WgTimers(createdAt);
            LocalIndex = keys.LocalIndex;
            RemoteIndex = keys.RemoteIndex;
            _sendCounter = nextSendCounter;
        }

        /// <summary>
        /// Incapsula un pacchetto IP (o vuoto, per un keepalive) nel messaggio
        /// di trasporto. Eccezione se la sessione e' scaduta o il contatore e'
        /// oltre il limite del protocollo: un invio non riuscito non e' mai
        /// un messaggio mezzo fatto.
        /// </summary>
        public byte[] Encapsulate(byte[] ipPacket, DateTime now)
        {
            if (ipPacket == null) throw new ArgumentNullException("ipPacket");
            if (_timers.IsExpired(now))
                throw new InvalidOperationException("sessione scaduta dopo RejectAfterTime");

            byte[] message;
            lock (_gate)
            {
                if (_sendCounter >= WgConstants.RejectAfterMessages)
                    throw new InvalidOperationException("contatore oltre RejectAfterMessages");

                ulong counter = _sendCounter;
                _sendCounter++;

                byte[] nonce = NonceFor(counter);
                byte[] ciphertext = _send.Seal(nonce, new byte[0], ipPacket);

                message = new byte[WgConstants.MessageTransportHeaderSize + ciphertext.Length];
                message[0] = WgConstants.MessageTransportType;
                PutUInt32(message, 4, RemoteIndex);
                PutUInt64(message, 8, counter);
                Array.Copy(ciphertext, 0, message, WgConstants.MessageTransportHeaderSize, ciphertext.Length);
            }
            _timers.NoteSent(now);
            return message;
        }

        /// <summary>
        /// Estrae il payload da un messaggio di trasporto. Null = rifiuto:
        /// formato, indice, replay, o tag che non torna. Un messaggio vuoto
        /// (0 byte di payload) e' un keepalive.
        /// </summary>
        public byte[] Decapsulate(byte[] message, DateTime now)
        {
            if (message == null) throw new ArgumentNullException("message");
            if (message.Length < WgConstants.MessageKeepaliveSize) return null;
            if (message[0] != WgConstants.MessageTransportType) return null;
            if (GetUInt32(message, 4) != LocalIndex) return null;

            ulong counter = GetUInt64(message, 8);
            lock (_gate)
            {
                if (!_replay.CheckAndUpdate(counter)) return null;

                byte[] ciphertext = new byte[message.Length - WgConstants.MessageTransportHeaderSize];
                Array.Copy(message, WgConstants.MessageTransportHeaderSize, ciphertext, 0, ciphertext.Length);
                try
                {
                    return _receive.Open(NonceFor(counter), new byte[0], ciphertext);
                }
                catch (Exception)
                {
                    // Tag errato: il messaggio non e' nostro, non si consegna.
                    return null;
                }
            }
        }

        /// <summary>Vero se la chiave e' vecchia oltre RejectAfterTime.</summary>
        public bool IsExpired(DateTime now)
        {
            return _timers.IsExpired(now);
        }

        /// <summary>Vero se il silenzio in invio supera KeepaliveTimeout.</summary>
        public bool NeedsKeepalive(DateTime now)
        {
            return _timers.NeedsKeepalive(now);
        }

        /// <summary>Vero se serve una nuova chiave (tempo o contatori).</summary>
        public bool NeedsRekey(DateTime now)
        {
            return _timers.NeedsRekey(now);
        }

        /// <summary>Il nonce AEAD: contatore little-endian in [4:12].</summary>
        private static byte[] NonceFor(ulong counter)
        {
            byte[] nonce = new byte[12];
            PutUInt64(nonce, 4, counter);
            return nonce;
        }

        internal static void PutUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static void PutUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (8 * i));
        }

        internal static uint GetUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | buffer[offset + 1] << 8
                          | buffer[offset + 2] << 16
                          | buffer[offset + 3] << 24);
        }

        internal static ulong GetUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--)
                value = (value << 8) | buffer[offset + i];
            return value;
        }
    }
}
