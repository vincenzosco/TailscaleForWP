using Tailscale.Core.Crypto;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// HMAC-BLAKE2s e HKDF-BLAKE2s, i derivatori che MixDH e Split usano davvero
    /// (HKDF-SHA256 non compare nel canale di controllo).
    ///
    /// Vector generati con hashlib.blake2s e hmac di Python, che sono
    /// un'implementazione indipendente. Il primo controllo e' che l'hash del nome
    /// dell'istanza Noise sia quello con cui si inizializza lo stato simmetrico:
    /// un errore li' farebbe fallire l'handshake in modo silenzioso.
    /// </summary>
    public static class HkdfBlake2sTests
    {
        private const string Ck1 = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

        public static void Run()
        {
            ProtocolNameHash();
            ExtractVectors();
            SplitVector();
            Guards();
        }

        private static void ProtocolNameHash()
        {
            T.Eq(
                "bbea022b948cf3bc5857d70804229179e1116bc40cb8cc074835349c464bca36",
                Hex.Encode(Blake2s.Hash(System.Text.Encoding.ASCII.GetBytes(ProtocolConstants.NoiseInstanceName), 32)),
                "hkdf-blake2s: BLAKE2s(\"Noise_IK_25519_ChaChaPoly_BLAKE2s\")");

            SymmetricState state = new SymmetricState();
            state.Initialize();
            T.Eq(
                Hex.Encode(Blake2s.Hash(System.Text.Encoding.ASCII.GetBytes(ProtocolConstants.NoiseInstanceName), 32)),
                Hex.Encode(state.HandshakeHash),
                "hkdf-blake2s: lo stato simmetrico parte da h = BLAKE2s(protocolName)");
            T.Eq(Hex.Encode(state.HandshakeHash), Hex.Encode(state.ChainingKey),
                "hkdf-blake2s: all'inizio h e ck sono uguali");
        }

        private static void ExtractVectors()
        {
            byte[] ck1 = T.FromHex(Ck1);
            byte[] ikm1 = T.FromHex("404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f");

            T.Eq(
                T.FromHex("2c89e64cda4681bf3a4112a300cca0ddf04db1f9dd452568b639e88baeadf8ef"),
                HkdfBlake2s.Extract(ck1, ikm1),
                "hkdf-blake2s: PRK del vettore 1");

            byte[] derived1 = HkdfBlake2s.Derive(ikm1, ck1, null, 64);
            T.Eq(
                T.FromHex("a308e29d3a10567bd1ac089e27310bd54ec6522ca2118240c3dd2416f7dc23fa"),
                T.FromHex(Hex.Encode(derived1).Substring(0, 64)),
                "hkdf-blake2s: ck' del vettore 1");
            T.Eq(
                T.FromHex("c68caa124476385ed5f83d7aa98ebe58c9b636f48586d36061cf3781cc84b9a7"),
                T.FromHex(Hex.Encode(derived1).Substring(64, 64)),
                "hkdf-blake2s: k del vettore 1");

            byte[] ck2 = T.FromHex("b0900b9acbd7e1356759f0ef25eda779378625f7813a3b2716eb769279bc266c");
            byte[] ikm2 = T.FromHex("8696248bddc94bc09f668570d982fe3752025f917d70cab0015c3ee98c39827f");

            T.Eq(
                T.FromHex("23bb8b3ee47379348ca31b14aa0e4d1fa1a1b1e9b7ba214884125eb597334ed3"),
                HkdfBlake2s.Extract(ck2, ikm2),
                "hkdf-blake2s: PRK del vettore 2");

            byte[] derived2 = HkdfBlake2s.Derive(ikm2, ck2, null, 64);
            T.Eq(
                T.FromHex("2b75b466e2726f51459819120385fa43785f95de29bfff72af993915a5442b81"),
                T.FromHex(Hex.Encode(derived2).Substring(0, 64)),
                "hkdf-blake2s: ck' del vettore 2");
            T.Eq(
                T.FromHex("3a36b426efdb364a66355aaeb64423c8f1f0af910242172c2d9dd1cc6b02ec31"),
                T.FromHex(Hex.Encode(derived2).Substring(64, 64)),
                "hkdf-blake2s: k del vettore 2");
        }

        private static void SplitVector()
        {
            SymmetricState state = new SymmetricState();
            state.Initialize();

            // Split deriva dallo stato iniziale: ck = BLAKE2s(protocolName), non dal
            // vettore 1. Per confrontare con il vettore indipendente serve uno stato
            // con ck noto, quindi si verifica direttamente l'HKDF come fa Split.
            byte[] tx, rx;
            state.Split(out tx, out rx);
            T.Eq(32, tx.Length, "hkdf-blake2s: Split produce due chiavi da 32 byte");
            T.False(Hex.Encode(tx) == Hex.Encode(rx), "hkdf-blake2s: le due chiavi di Split sono diverse");

            byte[] expected = HkdfBlake2s.Derive(new byte[0], T.FromHex(Ck1), null, 64);
            T.Eq(
                T.FromHex("7a38c34bf2b8738d7374ca77c44ecb309a11d2b2528304fa86052c365e722624"),
                T.FromHex(Hex.Encode(expected).Substring(0, 64)),
                "hkdf-blake2s: k1 del vettore di Split");
            T.Eq(
                T.FromHex("1a17621f346abdc520746a9fb64d8eb03231d674fc15687537b0780fd9c060fd"),
                T.FromHex(Hex.Encode(expected).Substring(64, 64)),
                "hkdf-blake2s: k2 del vettore di Split");
        }

        private static void Guards()
        {
            byte[] key = new byte[32];
            T.Eq(HkdfBlake2s.Hmac(key, new byte[0]), HkdfBlake2s.Hmac(key, new byte[0]),
                 "hkdf-blake2s: HMAC deterministico");
            T.Eq(32, HkdfBlake2s.Expand(new byte[32], null, 32).Length, "hkdf-blake2s: expand di 32 byte");
            T.Eq(48, HkdfBlake2s.Expand(new byte[32], new byte[0], 48).Length, "hkdf-blake2s: expand di 48 byte");
            T.Throws(delegate { HkdfBlake2s.Expand(new byte[32], null, -1); },
                     "hkdf-blake2s: lunghezza negativa rifiutata");
            T.Throws(delegate { HkdfBlake2s.Expand(new byte[32], null, 255 * 32 + 1); },
                     "hkdf-blake2s: oltre 255*HashLen rifiutato");

            // Una chiave piu' lunga del blocco viene prima ridotta con l'hash.
            byte[] longKey = new byte[100];
            T.Eq(HkdfBlake2s.Hmac(longKey, new byte[] { 1, 2, 3 }).Length, 32,
                 "hkdf-blake2s: chiave piu' lunga del blocco gestita");
        }
    }
}
