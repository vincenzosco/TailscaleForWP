using System;
using Tailscale.Core;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Le chiavi devono poter essere salvate e rilette: sul telefono finiscono in
    /// `PasswordVault`, sulla CLI in un file. Se la rilettura non restituisse la
    /// stessa coppia, il nodo cambierebbe identita' a ogni avvio.
    /// </summary>
    public static class KeyMaterialTests
    {
        public static void Run()
        {
            KeyPair original = KeyPair.Generate(new SequenceRandomSource());
            string saved = KeyMaterial.SavePrivate(original);

            T.Eq(64, saved.Length, "la chiave salvata e' esadecimale di 32 byte");
            T.False(saved.Contains(" "), "la chiave salvata non ha spazi");

            KeyPair back = KeyMaterial.LoadPrivate(saved);
            T.Eq(T.ToHex(original.PrivateKey), T.ToHex(back.PrivateKey), "la privata si rilegge identica");
            T.Eq(T.ToHex(original.PublicKey), T.ToHex(back.PublicKey),
                 "la pubblica e' derivata dalla privata, non salvata a parte");

            // Spazi ai bordi: un file scritto a mano o una copia-incolla li ha.
            KeyPair trimmed = KeyMaterial.LoadPrivate("  " + saved + "\r\n");
            T.Eq(T.ToHex(original.PrivateKey), T.ToHex(trimmed.PrivateKey), "gli spazi ai bordi non contano");

            // Un testo sbagliato deve fallire, non produrre una coppia degenere:
            // una chiave vuota o troncata darebbe un nodo che il server rifiuta.
            T.Throws(delegate { KeyMaterial.LoadPrivate("non-una-chiave"); }, "testo non esadecimale: eccezione");
            T.Throws(delegate { KeyMaterial.LoadPrivate(""); }, "stringa vuota: eccezione");
            T.Throws(delegate { KeyMaterial.LoadPrivate("aabb"); }, "chiave troncata: eccezione");
        }
    }
}
