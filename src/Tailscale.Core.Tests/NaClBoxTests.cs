using Tailscale.Core.Crypto;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// crypto_box (NaCl box), verificato con vector generati da TweetNaCl
    /// (implementazione indipendente, controllata con node/tweetnacl 1.0.3).
    ///
    /// Il vettore della chiave condivisa verifica la derivazione HSalsa20, quello
    /// del ciphertext verifica XSalsa20 + il layout Poly1305.
    ///
    /// Nota di correttezza: crypto_box e' simmetrico in Diffie-Hellman, quindi
    /// A-&gt;B e B-&gt;A con lo stesso nonce producono lo STESSO ciphertext (lo dice
    /// il vettore, non un'ipotesi). Il test verifica quindi la dipendenza dal nonce,
    /// non l'uguaglianza.
    /// </summary>
    public static class NaClBoxTests
    {
        // Chiavi private e pubbliche fisse.
        private const string AliceSk = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
        private const string AlicePk = "07a37cbc142093c8b755dc1b10e86cb426374ad16aa853ed0bdfc0b2b86d1c7c";
        private const string BobSk = "404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f";
        private const string BobPk = "79a631eede1bf9c98f12032cdeadd0e7a079398fc786b88cc846ec89af85a51a";
        private const string SharedAtoB = "9e37e3406bf2f4e3027022945e0f1e159739ceae2cca4d45aa3592ea30e07183";
        private const string Nonce = "1700000000000000000000000000000000000000000000ab";
        private const string Message = "6d657373616767696f207065722063727970746f5f626f78";
        private const string CtAToB = "c4498b31ba4da102b17f0472082f561c63eb42e130c5903dc09f1b82f8b48ecff4e945f724a721e8";
        private const string CtEmpty = "84739affaa7ddf5abf91301e4b406f66";

        public static void Run()
        {
            SharedKeyDerivation();
            SealMatchesVector();
            OpenRoundTrip();
            OverheadAndFailures();
            NonceAndKeyDependence();
        }

        private static void SharedKeyDerivation()
        {
            T.Eq(
                T.FromHex(SharedAtoB),
                NaClBox.SharedKey(T.FromHex(AliceSk), T.FromHex(BobPk)),
                "naclbox: chiave condivisa A->B (HSalsa20)");
            T.Eq(
                T.FromHex(SharedAtoB),
                NaClBox.SharedKey(T.FromHex(BobSk), T.FromHex(AlicePk)),
                "naclbox: chiave condivisa B->A identica");
        }

        private static void SealMatchesVector()
        {
            T.Eq(
                T.FromHex(CtAToB),
                NaClBox.Seal(T.FromHex(Nonce), T.FromHex(Message), T.FromHex(AliceSk), T.FromHex(BobPk)),
                "naclbox: ciphertext a->b");

            T.Eq(
                T.FromHex(CtAToB),
                NaClBox.Seal(T.FromHex(Nonce), T.FromHex(Message), T.FromHex(BobSk), T.FromHex(AlicePk)),
                "naclbox: crypto_box e' simmetrico, stesso ciphertext b->a");

            T.Eq(
                T.FromHex(CtEmpty),
                NaClBox.Seal(T.FromHex(Nonce), new byte[0], T.FromHex(AliceSk), T.FromHex(BobPk)),
                "naclbox: ciphertext di un messaggio vuoto (solo tag)");
        }

        private static void OpenRoundTrip()
        {
            byte[] nonce = T.FromHex(Nonce);
            byte[] message = T.FromHex(Message);

            byte[] sealed1 = NaClBox.Seal(nonce, message, T.FromHex(AliceSk), T.FromHex(BobPk));
            T.Eq(message, NaClBox.Open(nonce, sealed1, T.FromHex(AlicePk), T.FromHex(BobSk)),
                  "naclbox: open a->b recupera il testo");

            byte[] sealed2 = NaClBox.Seal(nonce, message, T.FromHex(BobSk), T.FromHex(AlicePk));
            T.Eq(message, NaClBox.Open(nonce, sealed2, T.FromHex(BobPk), T.FromHex(AliceSk)),
                  "naclbox: open b->a recupera il testo");

            T.Eq(
                message,
                NaClBox.Open(nonce, T.FromHex(CtAToB), T.FromHex(AlicePk), T.FromHex(BobSk)),
                "naclbox: open del vettore esatto");
        }

        private static void OverheadAndFailures()
        {
            byte[] nonce = T.FromHex(Nonce);
            for (int len = 0; len <= 3; len++)
            {
                byte[] message = new byte[len];
                byte[] ct = NaClBox.Seal(nonce, message, T.FromHex(AliceSk), T.FromHex(BobPk));
                T.Eq(len + 16, ct.Length, "naclbox: overhead di 16 byte a lunghezza " + len);
            }

            byte[] tampered = T.FromHex(CtAToB);
            tampered[tampered.Length - 1] ^= 0x01;
            T.Throws(delegate
            {
                NaClBox.Open(nonce, tampered, T.FromHex(AlicePk), T.FromHex(BobSk));
            }, "naclbox: ciphertext manomesso rifiutato");

            // Attenzione: (BobPk, AliceSk) e' la STESSA chiave condivisa di
            // (AliceSk, BobPk), quindi non e' una coppia sbagliata. Serve una
            // coppia che non condivide il segreto originale.
            T.Throws(delegate
            {
                NaClBox.Open(nonce, T.FromHex(CtAToB), T.FromHex(AlicePk), T.FromHex(AliceSk));
            }, "naclbox: chiave pubblica sbagliata rifiutata");

            T.Throws(delegate
            {
                NaClBox.Open(nonce, new byte[8], T.FromHex(AlicePk), T.FromHex(BobSk));
            }, "naclbox: ciphertext piu' corto del tag rifiutato");

            T.Throws(delegate
            {
                NaClBox.Seal(new byte[23], new byte[0], T.FromHex(AliceSk), T.FromHex(BobPk));
            }, "naclbox: nonce di lunghezza sbagliata rifiutato");

            T.Throws(delegate
            {
                NaClBox.Seal(nonce, new byte[0], new byte[31], T.FromHex(BobPk));
            }, "naclbox: chiave privata di lunghezza sbagliata rifiutata");
        }

        private static void NonceAndKeyDependence()
        {
            byte[] message = T.FromHex(Message);
            byte[] nonceA = T.FromHex(Nonce);
            byte[] nonceB = (byte[])nonceA.Clone();
            nonceB[23] ^= 0x01;

            T.False(
                T.ToHex(NaClBox.Seal(nonceA, message, T.FromHex(AliceSk), T.FromHex(BobPk)))
                    == T.ToHex(NaClBox.Seal(nonceB, message, T.FromHex(AliceSk), T.FromHex(BobPk))),
                "naclbox: nonce diverso -> ciphertext diverso");

            byte[] otherKey = new byte[32];
            for (int i = 0; i < 32; i++) otherKey[i] = (byte)(0x80 + i);

            T.False(
                T.ToHex(NaClBox.Seal(nonceA, message, T.FromHex(AliceSk), T.FromHex(BobPk)))
                    == T.ToHex(NaClBox.Seal(nonceA, message, otherKey, T.FromHex(BobPk))),
                "naclbox: chiave diversa -> ciphertext diverso");

            T.Eq(
                NaClBox.Seal(nonceA, message, T.FromHex(AliceSk), T.FromHex(BobPk)),
                NaClBox.Seal(nonceA, message, T.FromHex(AliceSk), T.FromHex(BobPk)),
                "naclbox: deterministico");
        }
    }
}
