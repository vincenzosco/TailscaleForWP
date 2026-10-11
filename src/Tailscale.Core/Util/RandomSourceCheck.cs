using System;
using Tailscale.Core.Keys;

namespace Tailscale.Core
{
    /// <summary>
    /// La decisione su una sorgente casuale che il core, da solo, non puo' prendere.
    ///
    /// Una sorgente che restituisce la **stessa sequenza** a ogni estrazione non e'
    /// degenere: i byte fra loro sono diversi, quindi il core la accetta. Ma la
    /// machine key e la node key calcolate da quella sequenza sono identiche a ogni
    /// avvio: il server rifiuta la node key perche' gia' nota, il client rigenera
    /// l'identita' con la stessa sorgente, e dopo i tentativi concessi resta un
    /// errore definitivo che non dice perche'.
    ///
    /// Sta qui e non nell'app perche' e' una decisione: cosi' si prova senza
    /// telefono, e l'app puo' solo obbedire al risultato.
    /// </summary>
    public static class RandomSourceCheck
    {
        /// <summary>Trentadue byte per estrazione: la misura di una chiave privata.</summary>
        private const int ProbeBytes = 32;

        /// <summary>
        /// Vero se due estrazioni consecutive differiscono. Su falso scrive nel
        /// registro il motivo, con la conseguenza: chi legge deve capire che l'app
        /// non partira', non solo che il generatore e' strano.
        /// </summary>
        public static bool IsUsable(IRandomSource random, ILogSink log)
        {
            if (random == null) throw new ArgumentNullException("random");
            if (log == null) log = NullLogSink.Instance;

            byte[] first = new byte[ProbeBytes];
            byte[] second = new byte[ProbeBytes];
            random.NextBytes(first);
            random.NextBytes(second);

            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i]) return true;
            }

            log.Log("ERRORE: la sorgente casuale del sistema restituisce sempre gli stessi byte; "
                    + "le chiavi sarebbero identiche a ogni avvio");
            return false;
        }
    }
}
