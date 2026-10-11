using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Vector da RFC 7748: §5.2 (due casi di ScalarMult), §5.2 iterativo
    /// (1 e 1000 iterazioni) e §6.1 (scambio Diffie-Hellman completo).
    /// </summary>
    public static class X25519Tests
    {
        public static void Run()
        {
            ScalarMultVector1();
            ScalarMultVector2();
            DiffieHellmanVector();
            IteratedVector();
            ClampingAndGuards();
        }

        private static void ScalarMultVector1()
        {
            byte[] scalar = T.FromHex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
            byte[] point = T.FromHex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
            byte[] expected = T.FromHex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");

            T.Eq(expected, X25519.ScalarMult(scalar, point), "x25519 rfc7748 §5.2 vettore 1");
        }

        private static void ScalarMultVector2()
        {
            byte[] scalar = T.FromHex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d");
            byte[] point = T.FromHex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493");
            byte[] expected = T.FromHex("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957");

            T.Eq(expected, X25519.ScalarMult(scalar, point), "x25519 rfc7748 §5.2 vettore 2");
        }

        private static void DiffieHellmanVector()
        {
            byte[] alicePriv = T.FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            byte[] alicePub = T.FromHex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
            byte[] bobPriv = T.FromHex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
            byte[] bobPub = T.FromHex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
            byte[] shared = T.FromHex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

            T.Eq(alicePub, X25519.PublicFromPrivate(alicePriv), "x25519 §6.1 chiave pubblica di Alice");
            T.Eq(bobPub, X25519.PublicFromPrivate(bobPriv), "x25519 §6.1 chiave pubblica di Bob");
            T.Eq(shared, X25519.ScalarMult(alicePriv, bobPub), "x25519 §6.1 segreto condiviso (lato Alice)");
            T.Eq(shared, X25519.ScalarMult(bobPriv, alicePub), "x25519 §6.1 segreto condiviso (lato Bob)");
        }

        private static void IteratedVector()
        {
            // RFC 7748 §5.2: k = u = 9; a ogni giro  result = X25519(k, u); u = k; k = result.
            byte[] k = new byte[32];
            k[0] = 9;
            byte[] u = new byte[32];
            u[0] = 9;
            byte[] result = null;

            for (int i = 0; i < 1000; i++)
            {
                result = X25519.ScalarMult(k, u);
                u = k;
                k = result;
                if (i == 0)
                {
                    T.Eq(
                        T.FromHex("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"),
                        result,
                        "x25519 rfc7748 §5.2 dopo 1 iterazione");
                }
            }

            T.Eq(
                T.FromHex("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51"),
                result,
                "x25519 rfc7748 §5.2 dopo 1000 iterazioni");
        }

        private static void ClampingAndGuards()
        {
            // Il bit piu' alto della coordinata u va ignorato (RFC 7748 §5.2, ultimo caso).
            byte[] scalar = T.FromHex("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079");
            byte[] u1 = T.FromHex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
            byte[] u2 = (byte[])u1.Clone();
            u2[31] = (byte)(u2[31] | 0x80);
            T.Eq(X25519.ScalarMult(scalar, u1), X25519.ScalarMult(scalar, u2), "x25519: bit alto di u mascherato");

            T.Eq(32, X25519.ScalarMultBase(new byte[32]).Length, "x25519: output di 32 byte");
            T.Throws(delegate { X25519.ScalarMult(new byte[31], new byte[32]); }, "x25519: scalare di lunghezza sbagliata");
            T.Throws(delegate { X25519.ScalarMult(new byte[32], new byte[31]); }, "x25519: punto di lunghezza sbagliata");

            T.True(X25519.IsZeroAll(new byte[32]), "x25519: IsZeroAll su tutti zeri");
            T.False(X25519.IsZeroAll(T.FromHex("0000000000000000000000000000000000000000000000000000000000000001")), "x25519: IsZeroAll su valore non nullo");
        }
    }
}
