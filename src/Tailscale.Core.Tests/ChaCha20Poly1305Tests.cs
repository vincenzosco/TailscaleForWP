using System.Text;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Vector da RFC 8439: §2.3.2 e §2.4.2 (blocco ChaCha20), §2.5.2 (Poly1305
    /// puro) e §2.8.2 (AEAD completo).
    /// </summary>
    public static class ChaCha20Poly1305Tests
    {
        private static readonly byte[] Key = T.FromHex(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        public static void Run()
        {
            BlockCounterZero();
            BlockCounterOne();
            Poly1305KeyGeneration();
            Poly1305Vector();
            AeadVector();
            AeadRejectsTampering();
        }

        private static void BlockCounterZero()
        {
            // RFC 8439 Appendix A.1, Test Vector #1: chiave e nonce tutti a zero,
            // contatore 0. Si asserisce esattamente il prefisso citato dall'RFC
            // (22 byte), non il blocco intero che l'RFC non stampa per esteso.
            byte[] block = ChaCha20.Block(new byte[32], 0, new byte[12]);
            string actual = T.ToHex(block).Substring(0, 44);
            T.Eq("76b8e0ada0f13d90405d6ae55386bd28bdd219b8a08d", actual,
                 "chacha20 blocco contatore 0 (RFC 8439 A.1 #1, prefisso di 22 byte)");
        }

        private static void BlockCounterOne()
        {
            byte[] nonce = T.FromHex("000000090000004a00000000");
            string expected =
                "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e" +
                "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e";
            T.Eq(T.FromHex(expected), ChaCha20.Block(Key, 1, nonce), "chacha20 blocco (contatore 1)");
        }

        private static void Poly1305KeyGeneration()
        {
            // RFC 8439 §2.6.2: blocco ChaCha20 con contatore 0, usato come chiave
            // monouso Poli1305. E' l'unico vettore dell'RFC che verifica il
            // contatore 0 per intero (32 byte).
            byte[] key = T.FromHex(
                "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
            byte[] nonce = T.FromHex("000000000001020304050607");
            byte[] expected = T.FromHex(
                "8ad5a08b905f81cc815040274ab29471a833b637e3fd0da508dbb8e2fdd1a646");
            T.Eq(expected, ChaCha20.Poly1305Key(key, nonce), "chacha20 chiave poly1305 (RFC 8439 §2.6.2)");
        }

        private static void Poly1305Vector()
        {
            byte[] key = T.FromHex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
            byte[] message = Encoding.UTF8.GetBytes("Cryptographic Forum Research Group");
            T.Eq(T.FromHex("a8061dc1305136c6c22b8baf0c0127a9"), Poly1305.Tag(key, message), "poly1305 vettore RFC 8439 §2.5.2");

            // Messaggio piu' lungo di un blocco: copre il percorso multi-blocco e
            // e la chiusura con blocco parziale.
            byte[] long100 = new byte[100];
            for (int i = 0; i < long100.Length; i++) long100[i] = (byte)i;
            byte[] tag = Poly1305.Tag(key, long100);
            T.Eq(16, tag.Length, "poly1305: tag di 16 byte su input lungo");
            T.True(!X25519.IsZeroAll(tag), "poly1305: tag diverso da zero");
            T.Eq(tag, Poly1305.Tag(key, long100), "poly1305: deterministico sullo stesso input");
        }

        private static void AeadVector()
        {
            byte[] key = T.FromHex(
                "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
            byte[] nonce = T.FromHex("070000004041424344454647");
            byte[] aad = T.FromHex("50515253c0c1c2c3c4c5c6c7");
            byte[] plaintext = Encoding.UTF8.GetBytes(
                "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");

            string expectedCiphertext =
                "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
                "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
                "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
                "3ff4def08e4b7a9de576d26586cec64b6116";
            string expectedTag = "1ae10b594f09e26a7e902ecbd0600691";

            ChaCha20Poly1305 aead = new ChaCha20Poly1305(key);
            byte[] ciphertext = aead.Seal(nonce, aad, plaintext);

            T.Eq(T.FromHex(expectedCiphertext + expectedTag), ciphertext, "chacha20poly1305: vettore RFC 8439 §2.8.2");

            byte[] opened = aead.Open(nonce, aad, ciphertext);
            T.Eq(plaintext, opened, "chacha20poly1305: decifratura del vettore");
        }

        private static void AeadRejectsTampering()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7);
            byte[] nonce = T.FromHex("000000000000000000000002");
            byte[] aad = T.FromHex("aabbcc");
            byte[] plaintext = Encoding.UTF8.GetBytes("messaggio da autenticare");

            ChaCha20Poly1305 aead = new ChaCha20Poly1305(key);
            byte[] ciphertext = aead.Seal(nonce, aad, plaintext);
            T.Eq(plaintext, aead.Open(nonce, aad, ciphertext), "aead: round-trip");

            byte[] tamperedCiphertext = (byte[])ciphertext.Clone();
            tamperedCiphertext[0] ^= 0x01;
            T.Throws(delegate { aead.Open(nonce, aad, tamperedCiphertext); }, "aead: ciphertext alterato rifiutato");

            byte[] tamperedTag = (byte[])ciphertext.Clone();
            tamperedTag[tamperedTag.Length - 1] ^= 0x01;
            T.Throws(delegate { aead.Open(nonce, aad, tamperedTag); }, "aead: tag alterato rifiutato");

            T.Throws(delegate { aead.Open(nonce, T.FromHex("aabbcd"), ciphertext); }, "aead: AAD alterato rifiutato");

            byte[] otherNonce = T.FromHex("000000000000000000000003");
            T.Throws(delegate { aead.Open(otherNonce, aad, ciphertext); }, "aead: nonce diverso rifiutato");

            T.Throws(delegate { aead.Open(nonce, aad, new byte[8]); }, "aead: input piu' corto del tag rifiutato");
            T.Throws(delegate { new ChaCha20Poly1305(new byte[16]); }, "aead: chiave di lunghezza sbagliata rifiutata");
        }
    }
}
