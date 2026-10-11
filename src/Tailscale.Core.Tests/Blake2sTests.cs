using System.Text;
using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Vector di BLAKE2s.
    ///
    /// L'appendice B di RFC 7693 non e' stata recuperata per intero, quindi i
    /// valori attesi non vengono da memoria: sono stati calcolati con
    /// <c>hashlib.blake2s</c> di Python (implementazione OpenSSL, indipendente da
    /// questa) e confrontati con il digest di "abc" verificato dal testo dell'RFC.
    /// Coprono i casi che il protocollo usa davvero: input vuoto, multiplo esatto
    /// del blocco, oltre il blocco, output ridotto e modalita' con chiave.
    /// </summary>
    public static class Blake2sTests
    {
        public static void Run()
        {
            UnkeyedVectors();
            KeyedVectors();
            Guards();
        }

        private static void UnkeyedVectors()
        {
            T.Eq(
                T.FromHex("508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982"),
                Blake2s.Hash(Encoding.UTF8.GetBytes("abc"), 32),
                "blake2s('abc') a 32 byte");

            T.Eq(
                T.FromHex("69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9"),
                Blake2s.Hash(new byte[0], 32),
                "blake2s input vuoto (blocco finale vuoto)");

            T.Eq(
                T.FromHex("ae09db7cd54f42b490ef09b6bc541af688e4959bb8c53f359a6f56e38ab454a3"),
                Blake2s.Hash(new byte[64], 32),
                "blake2s blocco esatto di 64 byte");

            byte[] sixtySix = Encoding.UTF8.GetBytes("abcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabc");
            T.Eq(66, sixtySix.Length, "blake2s: l'input di prova e' di 66 byte");
            T.Eq(
                T.FromHex("7ce4ab6c2ea9587cfffe66d8966318e2b1d831c541b594e704c3dedd2487f12a"),
                Blake2s.Hash(sixtySix, 32),
                "blake2s input oltre il blocco (66 byte)");

            T.Eq(
                T.FromHex("aa4938119b1dc7b87cbad0ffd200d0ae"),
                Blake2s.Hash(Encoding.UTF8.GetBytes("abc"), 16),
                "blake2s output ridotto a 16 byte");
        }

        private static void KeyedVectors()
        {
            T.Eq(
                T.FromHex("a281f725754969a702f6fe36fc591b7def866e4b70173ece402fc01c064d6b65"),
                Blake2s.KeyedHash(Key(0, 32), Encoding.UTF8.GetBytes("abc"), 32),
                "blake2s con chiave di 32 byte");

            T.Eq(
                T.FromHex("cb4dca8c03826606aff0649e4cdfd8be4ee5c948f663bd19d335f6cd7cde5223"),
                Blake2s.KeyedHash(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes("abc"), 32),
                "blake2s con chiave di 3 byte");

            T.Eq(
                T.FromHex("23f6c104967143ff2fe862cdb21c24d3aab0eec7f420ab12915f3eb1c8ff3f8f"),
                Blake2s.KeyedHash(Key(0, 16), Key(0, 100), 32),
                "blake2s con chiave da 16 byte e input da 100 byte");

            // La chiave cambia il risultato: non e' un hash non-keyed travestito.
            T.False(
                T.ToHex(Blake2s.Hash(Encoding.UTF8.GetBytes("abc"), 32))
                    == T.ToHex(Blake2s.KeyedHash(Key(0, 32), Encoding.UTF8.GetBytes("abc"), 32)),
                "blake2s: la modalita' con chiave differisce da quella senza");
        }

        private static void Guards()
        {
            T.Eq(32, Blake2s.Hash(Key(0, 64), 32).Length, "blake2s: digest di 32 byte su input di 64 byte");
            T.Eq(Blake2s.Hash(Key(0, 10), 32), Blake2s.Hash(Key(0, 10), 32), "blake2s: deterministico");

            T.Throws(delegate { Blake2s.Hash(new byte[4], 0); }, "blake2s: output di lunghezza zero rifiutato");
            T.Throws(delegate { Blake2s.Hash(new byte[4], 33); }, "blake2s: output oltre 32 byte rifiutato");
            T.Throws(delegate { Blake2s.KeyedHash(new byte[33], new byte[4], 32); }, "blake2s: chiave oltre 32 byte rifiutata");
        }

        private static byte[] Key(int start, int length)
        {
            byte[] key = new byte[length];
            for (int i = 0; i < length; i++) key[i] = (byte)((start + i) & 0xff);
            return key;
        }
    }
}
