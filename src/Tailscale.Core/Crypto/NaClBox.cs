using System;

namespace Tailscale.Core.Crypto
{
    /// <summary>
    /// crypto_box di NaCl: cifratura autenticata a chiave pubblica.
    ///
    /// Costruzione: chiave condivisa = HSalsa20(X25519(privateKey, peerPublicKey)),
    /// poi crypto_secretbox con XSalsa20 e Poly1305.
    ///
    /// Layout del ciphertext, allineato a golang.org/x/crypto/nacl/box (quello che
    /// usa Tailscale): 16 byte di tag Poly1305 seguiti dai byte cifrati. I 32 byte
    /// di keystream iniziali servono solo come chiave Poly1305 e NON compaiono
    /// nell'output, quindi l'overhead e' esattamente 16 byte.
    ///
    /// crypto_box e' simmetrico in Diffie-Hellman: Seal con (privA, pubB) e con
    /// (privB, pubA) e lo stesso nonce producono lo stesso ciphertext. Non e' un
    /// difetto, e' la proprieta' di uno schema a chiave condivisa; sta a chi
    /// chiama usare il nonce una sola volta per coppia di chiavi.
    /// </summary>
    public static class NaClBox
    {
        public const int KeySize = 32;
        public const int NonceSize = 24;
        public const int TagSize = 16;

        /// <summary>
        /// Chiave condivisa precalcolata: HSalsa20 sul segreto X25519.
        /// E' il valore che crypto_box_beforenm di NaCl calcola.
        /// </summary>
        public static byte[] SharedKey(byte[] privateKey, byte[] peerPublicKey)
        {
            RequireLength(privateKey, KeySize, "privateKey");
            RequireLength(peerPublicKey, KeySize, "peerPublicKey");

            byte[] shared = X25519.ScalarMult(privateKey, peerPublicKey);
            return Salsa20.HSalsa20(shared, new byte[16]);
        }

        /// <summary>Cifra e autentica. Restituisce tag(16) || ciphertext.</summary>
        public static byte[] Seal(byte[] nonce, byte[] plaintext, byte[] senderPrivateKey, byte[] recipientPublicKey)
        {
            RequireLength(nonce, NonceSize, "nonce");
            if (plaintext == null) throw new ArgumentNullException("plaintext");

            byte[] key = SharedKey(senderPrivateKey, recipientPublicKey);
            return SealWithKey(key, nonce, plaintext);
        }

        /// <summary>Verifica e decifra. Lancia se il tag non e' valido.</summary>
        public static byte[] Open(byte[] nonce, byte[] ciphertext, byte[] senderPublicKey, byte[] recipientPrivateKey)
        {
            RequireLength(nonce, NonceSize, "nonce");
            if (ciphertext == null) throw new ArgumentNullException("ciphertext");

            byte[] key = SharedKey(recipientPrivateKey, senderPublicKey);
            return OpenWithKey(key, nonce, ciphertext);
        }

        /// <summary>Come Seal, ma con una chiave gia' derivata (piu' messaggi, stessa coppia).</summary>
        public static byte[] SealWithKey(byte[] sharedKey, byte[] nonce, byte[] plaintext)
        {
            RequireLength(sharedKey, KeySize, "sharedKey");
            RequireLength(nonce, NonceSize, "nonce");
            if (plaintext == null) throw new ArgumentNullException("plaintext");

            // Keystream: i primi 32 byte sono la chiave Poly1305, i successivi
            // cifrano il messaggio.
            byte[] stream = Salsa20.XSalsa20Keystream(sharedKey, nonce, 32 + plaintext.Length);
            byte[] polyKey = Slice(stream, 0, 32);

            byte[] output = new byte[TagSize + plaintext.Length];
            for (int i = 0; i < plaintext.Length; i++)
                output[TagSize + i] = (byte)(plaintext[i] ^ stream[32 + i]);

            byte[] tag = Poly1305.Tag(polyKey, Slice(output, TagSize, plaintext.Length));
            Array.Copy(tag, 0, output, 0, TagSize);
            return output;
        }

        /// <summary>Come Open, ma con una chiave gia' derivata.</summary>
        public static byte[] OpenWithKey(byte[] sharedKey, byte[] nonce, byte[] ciphertext)
        {
            RequireLength(sharedKey, KeySize, "sharedKey");
            RequireLength(nonce, NonceSize, "nonce");
            if (ciphertext == null) throw new ArgumentNullException("ciphertext");
            if (ciphertext.Length < TagSize)
                throw new BoxException("ciphertext troppo corto: " + ciphertext.Length + " byte, attesi almeno " + TagSize);

            int payloadLength = ciphertext.Length - TagSize;
            byte[] payload = Slice(ciphertext, TagSize, payloadLength);

            byte[] stream = Salsa20.XSalsa20Keystream(sharedKey, nonce, 32 + payloadLength);
            byte[] polyKey = Slice(stream, 0, 32);

            byte[] expected = Poly1305.Tag(polyKey, payload);
            if (!ConstantTimeEquals(expected, Slice(ciphertext, 0, TagSize)))
                throw new BoxException("autenticazione fallita: tag Poly1305 non valido");

            byte[] plaintext = new byte[payloadLength];
            for (int i = 0; i < payloadLength; i++)
                plaintext[i] = (byte)(payload[i] ^ stream[32 + i]);
            return plaintext;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        private static void RequireLength(byte[] value, int length, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value.Length != length)
                throw new ArgumentException(name + ": attesi " + length + " byte, ricevuti " + value.Length, name);
        }
    }

    /// <summary>Errore di autenticazione o di formato in NaClBox.</summary>
    public class BoxException : Exception
    {
        public BoxException(string message) : base(message) { }
    }
}
