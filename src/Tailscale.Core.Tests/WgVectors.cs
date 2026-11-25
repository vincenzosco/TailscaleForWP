using System;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Generato da tools/wg-vectors.py con un'implementazione indipendente
    /// (BoringSSL): non modificare a mano.
    /// </summary>
    public static class WgVectors
    {
        public static readonly byte[] HandshakeHash = T.FromHex("1373946d6b5d0038e69ac1a72a59e42700e8b48e48144b78722e9336380578ab");
        public static readonly byte[] InitEphPriv = T.FromHex("4142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f60");
        public static readonly byte[] InitStaticPriv = T.FromHex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        public static readonly byte[] InitStaticPub = T.FromHex("07a37cbc142093c8b755dc1b10e86cb426374ad16aa853ed0bdfc0b2b86d1c7c");
        public static readonly byte[] Initiation = T.FromHex("010000004433221164b101b1d0be5a8704bd078f9895001fc03e8e9f9522f188dd128d9846d48466158a0e4ca242d151ca97ab90159a98b67e616625e68b4065d357376b6598e644ad7d678c0295d22de4cb43d5135581ed36346bfa7cf42a122639f89315e67f2b43de40f99f8e6be9135e1632d45237be41c55cffa3e7b390a525fe6500000000000000000000000000000000");
        public static readonly byte[] InitiationMac1 = T.FromHex("d45237be41c55cffa3e7b390a525fe65");
        public static readonly byte[] InitiatorRecvKey = T.FromHex("df0e53eac779a4c1ab023907c8dda549efdbf8ca9a4ce931ba1d27a2a8be73f9");
        public static readonly byte[] InitiatorSendKey = T.FromHex("c5f330b6dc92c8bd4124800733b33cd9d7e5dd2ac8c782e1b558f9b2b60804d8");
        public static readonly byte[] Psk = T.FromHex("0000000000000000000000000000000000000000000000000000000000000000");
        public static readonly byte[] RespEphPriv = T.FromHex("6162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80");
        public static readonly byte[] RespStaticPriv = T.FromHex("2122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40");
        public static readonly byte[] RespStaticPub = T.FromHex("5869aff450549732cbaaed5e5df9b30a6da31cb0e5742bad5ad4a1a768f1a67b");
        public static readonly byte[] Response = T.FromHex("020000008877665544332211244fe3b963e899dd295baffce248d3530f3a9a7479ba063002680ebfe7adad496b48bcfdb2fa6808c058f0c9ef1c288f61de79709cc8330de3922e40997dbf4a00000000000000000000000000000000");
        public static readonly byte[] ResponseMac1 = T.FromHex("61de79709cc8330de3922e40997dbf4a");
        public static readonly byte[] Timestamp = T.FromHex("400000006553f10a07000000");
    }
}
