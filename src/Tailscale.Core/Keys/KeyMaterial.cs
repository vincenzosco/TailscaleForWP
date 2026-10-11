using System;

namespace Tailscale.Core.Keys
{
    /// <summary>
    /// Trasforma una coppia di chiavi in testo e viceversa, per poterla salvare.
    ///
    /// Si salva **solo la chiave privata**: la pubblica si ricalcola. Salvare
    /// entrambe permetterebbe di rileggere una coppia incoerente (privata e
    /// pubblica che non si corrispondono) e il server rifiuterebbe il nodo con un
    /// errore che non dice nulla su quale delle due sia sbagliata.
    ///
    /// Questo testo e' un segreto: sul telefono va in `PasswordVault`, e non deve
    /// mai finire nei log.
    /// </summary>
    public static class KeyMaterial
    {
        /// <summary>La chiave privata in esadecimale (64 caratteri).</summary>
        public static string SavePrivate(KeyPair pair)
        {
            if (pair == null) throw new ArgumentNullException("pair");
            return Hex.Encode(pair.PrivateKey);
        }

        /// <summary>Ricostruisce la coppia dalla sola privata.</summary>
        public static KeyPair LoadPrivate(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            string trimmed = text.Trim();
            if (trimmed.Length != 64)
                throw new FormatException("una chiave privata salvata ha 64 caratteri esadecimali, non "
                    + trimmed.Length);

            byte[] bytes;
            try
            {
                bytes = Hex.Decode(trimmed);
            }
            catch (Exception ex)
            {
                throw new FormatException("la chiave salvata non e' esadecimale: " + ex.Message, ex);
            }
            return KeyPair.FromPrivateKey(bytes);
        }
    }
}
