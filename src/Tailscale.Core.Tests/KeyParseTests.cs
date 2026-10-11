using System;
using Tailscale.Core.Crypto;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Chiavi tipizzate: formati di testo, rifiuto dell'input malformato e
    /// generazione di una coppia coerente.
    ///
    /// L'impossibilita' di assegnare un NodeKey a un MachineKey e' garantita dal
    /// sistema di tipi e quindi verificata dalla compilazione, non a runtime.
    /// </summary>
    public static class KeyParseTests
    {
        private const string SampleHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

        public static void Run()
        {
            RoundTrip();
            Rejections();
            Generation();
        }

        private static void RoundTrip()
        {
            T.Eq("nodekey:" + SampleHex, NodeKey.Parse("nodekey:" + SampleHex).ToString(),
                 "nodekey: round trip parse/ToString");
            T.Eq("mkey:" + SampleHex, MachineKey.Parse("mkey:" + SampleHex).ToString(),
                 "mkey: round trip parse/ToString");
            T.Eq("discokey:" + SampleHex, DiscoKey.Parse("discokey:" + SampleHex).ToString(),
                 "discokey: round trip parse/ToString");

            T.Eq(T.FromHex(SampleHex), NodeKey.Parse("nodekey:" + SampleHex).Bytes,
                 "nodekey: i byte sono esattamente l'hex");
            T.Eq(T.FromHex(SampleHex), MachineKey.Parse("mkey:" + SampleHex).Bytes,
                 "mkey: i byte sono esattamente l'hex");

            NodeKey a = NodeKey.Parse("nodekey:" + SampleHex);
            NodeKey b = NodeKey.Parse("nodekey:" + SampleHex.ToUpperInvariant());
            T.True(a.Equals(b), "nodekey: maiuscole accettate e uguali");
            T.Eq(a.ToString(), b.ToString(), "nodekey: normalizzato in minuscolo");

            // Bytes e' una copia difensiva: mutarlo non cambia la chiave.
            NodeKey c = NodeKey.Parse("nodekey:" + SampleHex);
            c.Bytes[0] = 0xFF;
            T.Eq("nodekey:" + SampleHex, c.ToString(), "nodekey: Bytes restituisce una copia");
        }

        private static void Rejections()
        {
            Reject(delegate { NodeKey.Parse("nodekey:zz"); }, "nodekey: hex non valido rifiutato");
            Reject(delegate { NodeKey.Parse("nodekey:" + SampleHex.Substring(0, 62)); },
                   "nodekey: hex di 31 byte rifiutato");
            Reject(delegate { NodeKey.Parse("nodekey:" + SampleHex + "00"); },
                   "nodekey: hex di 33 byte rifiutato");
            Reject(delegate { NodeKey.Parse(SampleHex); }, "nodekey: prefisso mancante rifiutato");
            Reject(delegate { NodeKey.Parse(null); }, "nodekey: null rifiutato");
            Reject(delegate { NodeKey.Parse(""); }, "nodekey: stringa vuota rifiutata");
            Reject(delegate { MachineKey.Parse("nodekey:" + SampleHex); },
                   "mkey: prefisso nodekey rifiutato");
            Reject(delegate { DiscoKey.Parse("mkey:" + SampleHex); },
                   "discokey: prefisso mkey rifiutato");
            Reject(delegate { MachineKey.Parse("mkey:" + SampleHex.Substring(0, 64).Replace("0", "g")); },
                   "mkey: cifra non esadecimale rifiutata");
        }

        private static void Generation()
        {
            KeyPair pair = KeyPair.Generate(new FixedRandomSource());

            T.Eq(32, pair.PrivateKey.Length, "keypair: chiave privata di 32 byte");
            T.Eq(32, pair.PublicKey.Length, "keypair: chiave pubblica di 32 byte");
            T.Eq(X25519.PublicFromPrivate(pair.PrivateKey), pair.PublicKey,
                 "keypair: la pubblica e' la base per la privata");

            KeyPair other = KeyPair.Generate(new FixedRandomSource());
            T.Eq(pair.PrivateKey, other.PrivateKey, "keypair: deterministico con la stessa sorgente");

            byte[] random = new byte[32];
            new FixedRandomSource().NextBytes(random);
            T.Eq(T.FromHex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
                 random, "sorgente casuale di test: sequenza nota");
        }

        private static void Reject(Action action, string name)
        {
            T.Throws(action, name);
        }

        /// <summary>Sorgente deterministica per i test: byte 0,1,2,...</summary>
        private sealed class FixedRandomSource : IRandomSource
        {
            private byte _next;

            public void NextBytes(byte[] buffer)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = _next++;
            }
        }
    }
}
