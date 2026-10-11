using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Vector dell'appendice A di RFC 5869, letti dal testo dell'RFC e
    /// ricontrollati con un'implementazione indipendente in Python: PRK e OKM
    /// di entrambi i lati.
    /// </summary>
    public static class HkdfTests
    {
        public static void Run()
        {
            TestCase1();
            TestCase2();
            TestCase3();
            Guards();
        }

        private static void TestCase1()
        {
            byte[] ikm = Repeat(0x0b, 22);
            byte[] salt = T.FromHex("000102030405060708090a0b0c");
            byte[] info = T.FromHex("f0f1f2f3f4f5f6f7f8f9");

            T.Eq(
                T.FromHex("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5"),
                Hkdf.ExtractSha256(salt, ikm),
                "hkdf rfc5869 A.1: PRK");

            T.Eq(
                T.FromHex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"),
                Hkdf.Sha256(ikm, salt, info, 42),
                "hkdf rfc5869 A.1: OKM di 42 byte");
        }

        private static void TestCase2()
        {
            byte[] ikm = Sequence(0x00, 80);
            byte[] salt = Sequence(0x60, 80);
            byte[] info = Sequence(0xb0, 80);

            T.Eq(
                T.FromHex("06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244"),
                Hkdf.ExtractSha256(salt, ikm),
                "hkdf rfc5869 A.2: PRK con input lunghi");

            T.Eq(
                T.FromHex("b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c" +
                          "59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71" +
                          "cc30c58179ec3e87c14c01d5c1f3434f1d87"),
                Hkdf.Sha256(ikm, salt, info, 82),
                "hkdf rfc5869 A.2: OKM di 82 byte (piu' blocchi)");
        }

        private static void TestCase3()
        {
            byte[] ikm = Repeat(0x0b, 22);

            T.Eq(
                T.FromHex("19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04"),
                Hkdf.ExtractSha256(null, ikm),
                "hkdf rfc5869 A.3: PRK con salt assente (zero di HashLen)");

            T.Eq(
                T.FromHex("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"),
                Hkdf.Sha256(ikm, new byte[0], new byte[0], 42),
                "hkdf rfc5869 A.3: OKM con salt e info vuoti");

            // Un salt esplicito di 32 zeri e uno assente devono coincidere: HMAC
            // porta comunque la chiave a 64 byte con zeri.
            T.Eq(
                Hkdf.ExtractSha256(null, ikm),
                Hkdf.ExtractSha256(new byte[32], ikm),
                "hkdf: salt assente equivale a salt di HashLen zeri");
        }

        private static void Guards()
        {
            byte[] ikm = Repeat(0x0b, 22);
            T.Eq(32, Hkdf.ExpandSha256(Repeat(0x01, 32), null, 32).Length, "hkdf: expand di 32 byte");
            T.Eq(Hkdf.Sha256(ikm, null, null, 16), Hkdf.Sha256(ikm, null, null, 16), "hkdf: deterministico");
            T.Throws(delegate { Hkdf.ExpandSha256(Repeat(0x01, 32), null, -1); }, "hkdf: lunghezza negativa rifiutata");
            T.Throws(delegate { Hkdf.ExpandSha256(Repeat(0x01, 32), null, 255 * 32 + 1); }, "hkdf: oltre 255*HashLen rifiutato");
        }

        private static byte[] Repeat(byte value, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < count; i++) result[i] = value;
            return result;
        }

        private static byte[] Sequence(byte start, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < count; i++) result[i] = (byte)(start + i);
            return result;
        }
    }
}
