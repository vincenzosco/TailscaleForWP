namespace Tailscale.Core.Keys
{
    /// <summary>
    /// La chiave che identifica il nodo nel coordinamento (formato "nodekey:").
    /// </summary>
    public sealed class NodeKey : CryptoKey
    {
        public const string KeyPrefix = "nodekey:";

        public NodeKey(byte[] bytes) : base(bytes) { }

        protected override string Prefix { get { return KeyPrefix; } }

        public static NodeKey Parse(string text)
        {
            return new NodeKey(ParseBytes(text, KeyPrefix));
        }

        public static NodeKey FromBytes(byte[] bytes)
        {
            return new NodeKey(bytes);
        }
    }

    /// <summary>
    /// La chiave privata/pubblica della macchina, usata per cifrare verso il
    /// server di coordinamento (formato "mkey:").
    /// </summary>
    public sealed class MachineKey : CryptoKey
    {
        public const string KeyPrefix = "mkey:";

        public MachineKey(byte[] bytes) : base(bytes) { }

        protected override string Prefix { get { return KeyPrefix; } }

        public static MachineKey Parse(string text)
        {
            return new MachineKey(ParseBytes(text, KeyPrefix));
        }

        public static MachineKey FromBytes(byte[] bytes)
        {
            return new MachineKey(bytes);
        }
    }

    /// <summary>
    /// La chiave usata per la discovery e la cifratura tra peer (formato "discokey:").
    /// </summary>
    public sealed class DiscoKey : CryptoKey
    {
        public const string KeyPrefix = "discokey:";

        public DiscoKey(byte[] bytes) : base(bytes) { }

        protected override string Prefix { get { return KeyPrefix; } }

        public static DiscoKey Parse(string text)
        {
            return new DiscoKey(ParseBytes(text, KeyPrefix));
        }

        public static DiscoKey FromBytes(byte[] bytes)
        {
            return new DiscoKey(bytes);
        }
    }
}
