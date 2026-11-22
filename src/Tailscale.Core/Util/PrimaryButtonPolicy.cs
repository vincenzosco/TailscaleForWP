using Tailscale.Core.Control;

namespace Tailscale.Core
{
    /// <summary>
    /// Cosa fa il pulsante principale della pagina, dato lo stato del motore.
    ///
    /// Esiste per un guasto preciso, visto sul telefono: l'etichetta e l'azione del
    /// pulsante erano due condizioni scritte **due volte**, in due punti della
    /// pagina. L'etichetta diceva "Avvia" in ogni stato tranne quello del login,
    /// l'azione in <c>Connecting</c> e <c>Authorized</c> era invece un arresto, e il
    /// motore parte da solo all'apertura della pagina: il tocco che l'utente faceva
    /// per collegarsi era quello che lo scollegava, e l'app restava "Fermo" senza
    /// nessun errore nel registro. Le stringhe per far combaciare le due cose
    /// ("Ferma", "Avvia", "Accedi") c'erano gia' nei file delle risorse, e nessuno
    /// le leggeva.
    ///
    /// Qui la decisione e' una sola, e chi chiama la usa sia per l'etichetta sia per
    /// cio' che esegue: non possono piu' divergere. Sta nel core, come la regola del
    /// ciclo del motore, perche' cosi' si prova senza telefono.
    /// </summary>
    public enum PrimaryAction
    {
        /// <summary>Aprire l'URL di login nel browser (il motore sta girando).</summary>
        Login,

        /// <summary>Rimettere in moto un motore fermo che aspetta ancora il login.</summary>
        Restart,

        /// <summary>Fermare il motore: la pagina e' collegata, o si sta collegando.</summary>
        Stop,

        /// <summary>Avviare il motore.</summary>
        Start
    }

    public static class PrimaryButtonPolicy
    {
        /// <summary>
        /// Che cosa deve fare il pulsante. `engineStarted` e' la verita' sul ciclo
        /// del motore (<c>EngineRunState.IsRunning</c>), non un secondo stato tenuto
        /// dalla pagina: serve solo a distinguere, in "Accesso richiesto", fra un
        /// motore che aspetta il login nel browser e un motore fermo.
        /// </summary>
        public static PrimaryAction Decide(ControlState state, bool engineStarted)
        {
            if (state == ControlState.NeedLogin)
                return engineStarted ? PrimaryAction.Login : PrimaryAction.Restart;

            // Collegato o in corso: l'azione e' fermare, e l'etichetta deve dirlo.
            if (state == ControlState.Connecting || state == ControlState.Authorized)
                return PrimaryAction.Stop;

            return PrimaryAction.Start;
        }
    }
}
