namespace Tailscale.Core.Wg
{
    /// <summary>
    /// Le costanti del protocollo WireGuard, trascritte dal sorgente pinnato
    /// (refs/wireguard-go, ref 417aef361226): vedi docs/wireguard-notes.md per
    /// il file e la riga di ogni valore.
    ///
    /// Il gate `tools/check-protocol-pin.py` verifica questi valori contro il
    /// tree vendored, come per ProtocolConstants: se il pin cambia e upstream
    /// cambia qualcosa, il controllo cade prima dei test.
    /// </summary>
    public static class WgConstants
    {
        /// <summary>Nome Noise della costruzione (37 byte).</summary>
        public const string Construction = "Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s";

        /// <summary>Prologo Noise (34 byte).</summary>
        public const string Identifier = "WireGuard v1 zx2c4 Jason@zx2c4.com";

        /// <summary>Label per la chiave di mac1.</summary>
        public const string LabelMac1 = "mac1----";

        /// <summary>Label per la chiave di cifratura del cookie.</summary>
        public const string LabelCookie = "cookie--";

        public const byte MessageInitiationType = 1;
        public const byte MessageResponseType = 2;
        public const byte MessageCookieReplyType = 3;
        public const byte MessageTransportType = 4;

        /// <summary>type(4) sender(4) ephemeral(32) static(48) timestamp(28) mac1(16) mac2(16).</summary>
        public const int MessageInitiationSize = 148;

        /// <summary>type(4) sender(4) receiver(4) ephemeral(32) empty(16) mac1(16) mac2(16).</summary>
        public const int MessageResponseSize = 92;

        /// <summary>type(4) receiver(4) nonce(24) cookie(32).</summary>
        public const int MessageCookieReplySize = 64;

        /// <summary>type(4) receiver(4) counter(8), prima del ciphertext.</summary>
        public const int MessageTransportHeaderSize = 16;

        /// <summary>Dimensione del transport senza contenuto: header + tag AEAD.</summary>
        public const int MessageKeepaliveSize = 32;

        public const int MessageTransportOffsetReceiver = 4;
        public const int MessageTransportOffsetCounter = 8;
        public const int MessageTransportOffsetContent = 16;

        /// <summary>Chiave e tag AEAD a 32 e 16 byte, mac da 16.</summary>
        public const int KeySize = 32;
        public const int TagSize = 16;
        public const int MacSize = 16;

        /// <summary>Timestamp TAI64N e nonce del cookie reply a 12 e 24 byte.</summary>
        public const int TimestampSize = 12;
        public const int CookieNonceSize = 24;

        /// <summary>MTU del tunnel: vincolo del progetto, non una costante upstream.</summary>
        public const int PeerMaxMtu = 1280;

        /// <summary>Base TAI64: secondi + 0x400000000000000a, big-endian.</summary>
        public const ulong Tai64Base = 0x400000000000000A;

        /// <summary>I 24 bit bassi dei nanosecondi vengono azzerati (2^24 ns di granularita').</summary>
        public const uint NanosecondWhitenerMask = 0x1000000 - 1;

        /// <summary>Timer e limiti (device/constants.go): dopo 120 s o 2^60
        /// messaggi si cerca una nuova chiave; dopo 180 s la chiave muore.</summary>
        public const int RekeyAfterSeconds = 120;
        public const int RejectAfterSeconds = 180;
        public const int KeepaliveSeconds = 10;
        public const int RekeyTimeoutSeconds = 5;
        public const int RekeyAttemptSeconds = 90;
        public const ulong RekeyAfterMessages = 1UL << 60;
        public const ulong RejectAfterMessages = ulong.MaxValue - 8192;
    }
}
