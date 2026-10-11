using System.Text;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// SHA-256 e HMAC-SHA256.
    ///
    /// SHA-256 e' verificato con i vettori classici di FIPS 180-4 (stringa vuota,
    /// "abc", blocco di 64 byte, stringa di 56 byte che forza un blocco di padding
    /// in piu') e con digest generati da hashlib di Python; HMAC e' verificato con
    /// hmac di Python, compreso il caso di chiave piu' lunga del blocco.
    /// </summary>
    public static class Sha256Tests
    {
        public static void Run()
        {
            FipsVectors();
            PaddingBoundaries();
            HmacVectors();
        }

        private static void FipsVectors()
        {
            T.Eq(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                T.ToHex(Sha256.Hash(new byte[0])),
                "sha256: stringa vuota");

            T.Eq(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                T.ToHex(Sha256.Hash(Ascii("abc"))),
                "sha256: \"abc\"");

            T.Eq(
                "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
                T.ToHex(Sha256.Hash(Ascii("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"))),
                "sha256: vettore di 56 byte");
        }

        private static void PaddingBoundaries()
        {
            // Blocco intero di zeri: esercita il padding con lunghezza multipla di 64.
            T.Eq(
                "f5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b",
                T.ToHex(Sha256.Hash(new byte[64])),
                "sha256: 64 byte di zeri");

            T.Eq(32, Sha256.Hash(new byte[0]).Length, "sha256: digest di 32 byte");
            T.Eq(Sha256.Hash(Ascii("determinismo")), Sha256.Hash(Ascii("determinismo")), "sha256: deterministico");
        }

        private static void HmacVectors()
        {
            byte[] msg = Ascii("messaggio da autenticare");

            byte[] key32 = new byte[32];
            for (int i = 0; i < key32.Length; i++) key32[i] = (byte)i;
            T.Eq(
                "7c7ce731c6459c47e9d4ae31e31894c5d6d82ba7d8a4292ad8385d9851236ac5",
                T.ToHex(Sha256.Hmac(key32, msg)),
                "hmac-sha256: chiave di 32 byte");

            T.Eq(
                "ec99a695481537a4e6513d478f6234785ccd71569f4725a791ca7ed1b00a32a2",
                T.ToHex(Sha256.Hmac(new byte[] { 1, 2, 3 }, msg)),
                "hmac-sha256: chiave corta");

            // Chiave piu' lunga del blocco: va sostituita con il suo digest.
            byte[] longKey = new byte[100];
            for (int i = 0; i < longKey.Length; i++) longKey[i] = (byte)i;
            T.Eq(
                "e5b188ecd3432df558ed3c64c0ebf08d402d3dbdc8f78ac773293df7d912cf7a",
                T.ToHex(Sha256.Hmac(longKey, msg)),
                "hmac-sha256: chiave piu' lunga del blocco");

            T.Eq(
                Sha256.Hmac(key32, new byte[0]),
                Sha256.Hmac(key32, new byte[0]),
                "hmac-sha256: deterministico");
        }

        private static byte[] Ascii(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }
    }
}
