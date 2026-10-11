namespace Tailscale.Core.Protocol
{
    /// <summary>
    /// Costanti del protocollo di controllo Tailscale (ts2021).
    ///
    /// Ogni valore qui e' stato letto dal sorgente di riferimento pinnato
    /// (refs/tailscale@v1.104.1); file e riga sono in docs/protocol-notes.md e
    /// tools/check-protocol-pin.py fallisce se questo file e il tree divergono.
    /// Non modificare questi valori a mano: si aggiorna il pin e si rieseguono
    /// i controlli (vedi agents/skills/wp81-tailscale-protocol-pin).
    /// </summary>
    public static class ProtocolConstants
    {
        public const string DefaultControlUrl = "https://controlplane.tailscale.com";
        public const string ControlServerHost = "controlplane.tailscale.com";

        // Trasporto HTTP del protocollo di controllo.
        public const string UpgradePath = "/ts2021";
        public const string UpgradeHeaderName = "Upgrade";
        public const string UpgradeHeaderValue = "tailscale-control-protocol";
        public const string ConnectionHeaderName = "Connection";
        public const string ConnectionHeaderValue = "upgrade";
        // Contiene il messaggio 1 dell'handshake in base64, per risparmiare un RTT.
        public const string HandshakeHeaderName = "X-Tailscale-Handshake";

        // Istanza Noise del canale di controllo.
        public const string NoiseInstanceName = "Noise_IK_25519_ChaChaPoly_BLAKE2s";
        // Prologo: "Tailscale Control Protocol v" + versione decimale.
        public const string ProloguePrefix = "Tailscale Control Protocol v";
        // Versione negoziata: tailcfg.CurrentCapabilityVersion, inviata come uint16.
        public const ushort CapabilityVersion = 148;

        // Tipi di messaggio di controlbase.
        public const byte MsgTypeInitiation = 1;
        public const byte MsgTypeResponse = 2;
        public const byte MsgTypeError = 3;
        public const byte MsgTypeRecord = 4;

        // Lunghezze. headerLen vale per tutti i messaggi tranne l'initiation,
        // che ha un header piu' lungo perche' porta la versione del protocollo.
        public const int HeaderLen = 3;
        public const int InitiationHeaderLen = 5;

        // initiationMessage [101]byte = 2 versione + 1 tipo + 2 lunghezza
        //                             + 32 effimera in chiaro + 48 statica cifrata + 16 tag
        public const int InitiationMessageLen = 101;
        // responseMessage [51]byte = 1 tipo + 2 lunghezza + 32 effimera + 16 tag
        public const int ResponseMessageLen = 51;

        public const int MaxMessageSize = 4096;
        public const int MaxCiphertextSize = MaxMessageSize - 3;
        public const int AeadTagLen = 16;
        public const int MaxPlaintextSize = MaxCiphertextSize - AeadTagLen;

        // Header aggiunto alle richieste di registrazione e map con il node key:
        // tailcfg.LBHeader = "Ts-Lb" (tailcfg/tailcfg.go:3109). Serve al bilanciatore
        // per instradare tutte le richieste dello stesso nodo allo stesso server.
        public const string LbHeaderName = "Ts-Lb";

        // Endpoint del control plane.
        public const string RegisterPath = "/machine/register";
        public const string MapPath = "/machine/map";
        // GET {0}/key?v={1} -> chiave noise (ts2021) e chiave legacy (naclbox).
        public const string ServerKeyPathFormat = "{0}/key?v={1}";

        // Watchdog dello stream map lato server: senza un MapResponse in questo
        // intervallo la connessione va chiusa e rifatta.
        public const int MapStreamWatchdogSeconds = 120;
    }
}
