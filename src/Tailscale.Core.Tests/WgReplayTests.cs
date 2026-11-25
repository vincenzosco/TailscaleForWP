using System;
using Tailscale.Core.Wg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// La finestra anti-replay RFC 6479, come replay/replay.go del pin:
    /// anello di 128 blocchi da 64 bit, finestra di 8256 contatori.
    /// </summary>
    public static class WgReplayTests
    {
        public static void Run()
        {
            Case("wg: stesso contatore due volte, la seconda rifiuta", SameCounterTwice);
            Case("wg: fuori ordine entro la finestra, accettato", OutOfOrderAccepted);
            Case("wg: contatore troppo vecchio, rifiutato", TooOldRejected);
            Case("wg: salto in avanti, la finestra avanza", JumpAdvances);
            Case("wg: il limite di contatore rifiuta sempre", LimitAlwaysRejected);
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

        private static void SameCounterTwice()
        {
            ReplayWindow window = new ReplayWindow();
            T.True(window.CheckAndUpdate(5), "prima volta: accettato");
            T.False(window.CheckAndUpdate(5), "seconda volta: rifiutato");
        }

        private static void OutOfOrderAccepted()
        {
            ReplayWindow window = new ReplayWindow();
            T.True(window.CheckAndUpdate(100), "contatore alto");
            T.True(window.CheckAndUpdate(40), "piu' basso ma entro la finestra");
            T.False(window.CheckAndUpdate(40), "ripetuto: rifiutato");
            T.True(window.CheckAndUpdate(99), "ancora entro la finestra");
        }

        private static void TooOldRejected()
        {
            ReplayWindow window = new ReplayWindow();
            T.True(window.CheckAndUpdate(10000), "contatore alto");
            // Finestra = 8256: sotto 10000 - 8256 il contatore e' fuori.
            T.False(window.CheckAndUpdate(10000 - 8256 - 1), "troppo indietro: rifiutato");
        }

        private static void JumpAdvances()
        {
            ReplayWindow window = new ReplayWindow();
            T.True(window.CheckAndUpdate(10), "inizio");
            T.True(window.CheckAndUpdate(10 + 100000), "salto enorme: la finestra avanza");
            T.False(window.CheckAndUpdate(10), "il vecchio valore e' ora fuori e gia' visto");
            T.True(window.CheckAndUpdate(10 + 100000 - 1), "accanto al salto: accettato");
        }

        private static void LimitAlwaysRejected()
        {
            ReplayWindow window = new ReplayWindow();
            T.False(window.CheckAndUpdate(ulong.MaxValue), "limite raggiunto: rifiutato");
        }
    }
}
