using System;
using Tailscale.Core.Control;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il caso che questi test difendono: l'app e' collegata ("Connesso") o si sta
    /// collegando, e il pulsante principale **non** deve comportarsi come un
    /// "Avvia" -- cioe' non deve fermare il motore -- ne' dire una cosa mentre ne fa
    /// un'altra. Il 2026-10-12 sul telefono l'app finiva in "Fermo" senza nessun
    /// errore nel registro proprio cosi': il pulsante che l'utente toccava per
    /// collegarsi chiamava un arresto, mentre l'etichetta diceva "Avvia".
    ///
    /// La tabella qui sotto e' l'intera decisione: una riga per ogni stato in cui il
    /// motore puo' trovarsi.
    /// </summary>
    public static class PrimaryButtonPolicyTests
    {
        public static void Run()
        {
            Case("pulsante: fermo o in errore, il pulsante avvia", APageThatIsNotConnectedStarts);
            Case("pulsante: collegato, il pulsante ferma", AConnectedPageStops);
            Case("pulsante: mentre si collega, il pulsante ferma", AConnectingPageStops);
            Case("pulsante: in Accesso richiesto apre il browser", ALoginPageWithARunningEngineOpensTheBrowser);
            Case("pulsante: in Accesso richiesto con motore fermo riavvia", ALoginPageWithAStoppedEngineRestarts);
            Case("pulsante: nessuno stato avvia mentre e' collegato", NoConnectedStateStarts);
        }

        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void APageThatIsNotConnectedStarts()
        {
            T.Eq(PrimaryAction.Start.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.Stopped, false).ToString(),
                 "pulsante: con il motore fermo si avvia");
            T.Eq(PrimaryAction.Start.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.Error, false).ToString(),
                 "pulsante: dopo un errore si riavvia");
        }

        private static void AConnectedPageStops()
        {
            T.Eq(PrimaryAction.Stop.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.Authorized, true).ToString(),
                 "pulsante: collegato, il tocco ferma il motore");
        }

        private static void AConnectingPageStops()
        {
            T.Eq(PrimaryAction.Stop.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.Connecting, true).ToString(),
                 "pulsante: mentre si collega, il tocco ferma il motore");
        }

        private static void ALoginPageWithARunningEngineOpensTheBrowser()
        {
            T.Eq(PrimaryAction.Login.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.NeedLogin, true).ToString(),
                 "pulsante: in Accesso richiesto si apre il browser");
        }

        private static void ALoginPageWithAStoppedEngineRestarts()
        {
            T.Eq(PrimaryAction.Restart.ToString(),
                 PrimaryButtonPolicy.Decide(ControlState.NeedLogin, false).ToString(),
                 "pulsante: in Accesso richiesto con il motore fermo si riavvia");
        }

        /// <summary>
        /// La guardia che descrive il guasto: nessuno degli stati che significano
        /// "collegato, o in corso di collegamento" deve decidere un avvio, perche'
        /// l'avvio e' l'azione che l'utente si aspetta da "Avvia" e sarebbe la
        /// decisione opposta a quella presa.
        /// </summary>
        private static void NoConnectedStateStarts()
        {
            ControlState[] connected = new ControlState[]
            {
                ControlState.Connecting, ControlState.Authorized
            };

            for (int i = 0; i < connected.Length; i++)
            {
                PrimaryAction action = PrimaryButtonPolicy.Decide(connected[i], true);
                T.False(action == PrimaryAction.Start,
                         "pulsante: " + connected[i] + " non deve decidere un avvio");
            }
        }
    }
}
