using System;
using System.Threading;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il caso che questi test difendono: il ciclo del motore esce da solo -- budget
    /// del canale esaurito, oppure un'eccezione -- e il motore deve restare
    /// riavviabile. Prima di questa classe il flag "avviato" viveva nell'app e non
    /// veniva mai spento quando il ciclo finiva: la pagina mostrava l'errore, e il
    /// pulsante che doveva ripartire non faceva niente.
    ///
    /// Due motori sullo stesso stato si contenderebbero la stessa chiave, quindi un
    /// secondo avvio mentre un ciclo gira viene rifiutato, e il rifiuto non disturba
    /// il ciclo in corso.
    /// </summary>
    public static class EngineRunStateTests
    {
        /// <summary>Quanto si aspetta un thread di prova: se scade, il test e' rotto, non lento.</summary>
        private const int JoinMs = 5000;

        public static void Run()
        {
            Case("motore: un ciclo esegue il corpo e si libera", OneCycleExecutesTheBodyAndFreesTheEngine);
            Case("motore: due tocchi, un solo ciclo", TwoThreadsRaceToOneCycle);
            Case("motore: un ciclo rifiutato non disturba quello in corso", ARefusedRunLeavesTheRunningCycleAlone);
            Case("motore: un corpo che lancia libera comunque il motore", AFailingBodyStillFreesTheEngine);
            Case("motore: lo stato si vede mentre il ciclo gira", IsRunningIsVisibleWhileTheBodyRuns);
            Case("motore: dopo l'uscita si puo' ripartire", TheEngineCanRestartAfterTheBodyReturns);
            Case("motore: con il motore libero l'avvio non aspetta", AFreeEngineStartsWithoutWaiting);
            Case("motore: l'avvio aspetta il ciclo che sta uscendo", AStartWaitsForTheCycleThatIsLeaving);
            Case("motore: l'avvio che aspetta e non trova posto lo dice", AStartGivesUpWhenTheCycleKeepsRunning);
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
                Console.WriteLine("FAIL motore/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OneCycleExecutesTheBodyAndFreesTheEngine()
        {
            EngineRunState state = new EngineRunState();
            int runs = 0;

            T.True(state.Run(delegate { runs++; }), "motore: il primo ciclo parte");
            T.Eq(1, runs, "motore: il corpo e' stato eseguito una volta");
            T.False(state.IsRunning, "motore: uscito il corpo, il motore non e' piu' in moto");
            T.True(state.Run(delegate { runs++; }), "motore: e il ciclo successivo parte");
            T.Eq(2, runs, "motore: il secondo corpo e' stato eseguito");
        }

        /// <summary>
        /// Due tocchi sul pulsante, o un tocco mentre il ciclo precedente sta ancora
        /// uscendo: deve nascere un motore solo.
        /// </summary>
        private static void TwoThreadsRaceToOneCycle()
        {
            EngineRunState state = new EngineRunState();
            int entered = 0;
            int started = 0;
            ManualResetEvent inside = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);

            Thread first = new Thread(delegate()
            {
                if (state.Run(delegate()
                    {
                        Interlocked.Increment(ref entered);
                        inside.Set();
                        release.WaitOne(JoinMs);
                    }))
                {
                    Interlocked.Increment(ref started);
                }
            });
            first.IsBackground = true;
            first.Start();
            inside.WaitOne(JoinMs);

            // Il secondo tocco arriva mentre il primo ciclo e' in moto.
            T.False(state.Run(delegate { Interlocked.Increment(ref entered); }),
                    "motore: un secondo avvio mentre il ciclo gira non parte");

            release.Set();
            first.Join(JoinMs);

            T.Eq(1, started, "motore: un solo ciclo e' partito");
            T.Eq(1, entered, "motore: e il corpo e' stato eseguito una volta sola");
            T.False(state.IsRunning, "motore: alla fine il motore e' libero");
        }

        /// <summary>
        /// Il rifiuto e' una risposta, non un arresto: il ciclo in moto deve
        /// continuare a girare, e girare una volta sola.
        /// </summary>
        private static void ARefusedRunLeavesTheRunningCycleAlone()
        {
            EngineRunState state = new EngineRunState();
            int runs = 0;
            ManualResetEvent inside = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);

            Thread worker = new Thread(delegate()
            {
                state.Run(delegate()
                {
                    runs++;
                    inside.Set();
                    release.WaitOne(JoinMs);
                });
            });
            worker.IsBackground = true;
            worker.Start();
            inside.WaitOne(JoinMs);

            T.False(state.Run(delegate { runs++; }), "motore: il secondo avvio viene rifiutato");
            T.True(state.IsRunning, "motore: il rifiuto non ferma il ciclo in corso");

            release.Set();
            worker.Join(JoinMs);
            T.Eq(1, runs, "motore: il ciclo in corso ha girato una volta sola");
        }

        /// <summary>
        /// Un errore di tipo non previsto non deve lasciare il motore bloccato per
        /// sempre: il motore si libera, e l'eccezione esce al chiamante invece di
        /// essere inghiottita per far sembrare tutto normale.
        /// </summary>
        private static void AFailingBodyStillFreesTheEngine()
        {
            EngineRunState state = new EngineRunState();
            string seen = null;

            try
            {
                state.Run(delegate { throw new InvalidOperationException("guasto del motore"); });
            }
            catch (InvalidOperationException ex)
            {
                seen = ex.Message;
            }

            T.Eq("guasto del motore", seen, "motore: l'eccezione del corpo esce al chiamante");
            T.False(state.IsRunning, "motore: e il motore resta libero, non bloccato per sempre");
            T.True(state.Run(delegate { }), "motore: si puo' ripartire dopo un corpo che ha lanciato");
        }

        /// <summary>
        /// La pagina legge lo stato dalla UI thread mentre il ciclo gira sul suo
        /// thread di lavoro: quel che legge deve essere vero durante e falso dopo.
        /// </summary>
        private static void IsRunningIsVisibleWhileTheBodyRuns()
        {
            EngineRunState state = new EngineRunState();
            ManualResetEvent inside = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);

            Thread worker = new Thread(delegate()
            {
                state.Run(delegate()
                {
                    inside.Set();
                    release.WaitOne(JoinMs);
                });
            });
            worker.IsBackground = true;
            worker.Start();
            inside.WaitOne(JoinMs);

            T.True(state.IsRunning, "motore: mentre il ciclo gira il motore risulta in moto");

            release.Set();
            worker.Join(JoinMs);
            T.False(state.IsRunning, "motore: uscito il ciclo, il motore risulta fermo");
        }

        /// <summary>
        /// Con il motore libero non c'e' niente da aspettare: l'attesa che serve al
        /// caso del telefono non deve diventare un ritardo su ogni avvio.
        /// </summary>
        private static void AFreeEngineStartsWithoutWaiting()
        {
            EngineRunState state = new EngineRunState();
            int runs = 0;
            int waits = 0;

            bool started = state.RunWhenFree(delegate { runs++; },
                                             delegate(int ms) { waits++; }, 5, 1);

            T.True(started, "motore: con il posto libero il ciclo parte");
            T.Eq(1, runs, "motore: e il corpo e' stato eseguito");
            T.Eq(0, waits, "motore: senza attendere nemmeno una volta");
            T.False(state.IsRunning, "motore: uscito il corpo, il motore e' libero");
        }

        /// <summary>
        /// Il caso del telefono, in piccolo: un ciclo tiene il posto e sta uscendo --
        /// l'arresto e' stato chiesto adesso, oppure il budget e' finito. L'avvio che
        /// aspetta deve entrare appena il posto si libera, invece di essere rifiutato
        /// e sparire.
        /// </summary>
        private static void AStartWaitsForTheCycleThatIsLeaving()
        {
            EngineRunState state = new EngineRunState();
            ManualResetEvent inside = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);
            int waits = 0;
            int runs = 0;

            Thread leaving = new Thread(delegate()
            {
                state.Run(delegate()
                {
                    inside.Set();
                    release.WaitOne(JoinMs);
                });
            });
            leaving.IsBackground = true;
            leaving.Start();
            inside.WaitOne(JoinMs);

            // L'attesa del tentativo e' il momento in cui il ciclo in uscita se ne va:
            // il join rende l'esito certo, senza dipendere dal tempo.
            bool started = state.RunWhenFree(delegate { runs++; },
                                             delegate(int ms)
                                             {
                                                 waits++;
                                                 release.Set();
                                                 leaving.Join(JoinMs);
                                             }, 5, 1);

            T.True(started, "motore: l'avvio che aspetta entra quando il posto si libera");
            T.Eq(1, runs, "motore: e il corpo e' stato eseguito una volta");
            T.Eq(1, waits, "motore: con una sola attesa");
            T.False(state.IsRunning, "motore: alla fine il motore e' libero");
        }

        /// <summary>
        /// Se il ciclo non esce entro il budget, il rifiuto resta la risposta giusta
        /// (due client sullo stesso stato si contenderebbero la stessa chiave):
        /// l'avvio si arrende, lo dice con il falso, e il ciclo in moto non viene
        /// toccato.
        /// </summary>
        private static void AStartGivesUpWhenTheCycleKeepsRunning()
        {
            EngineRunState state = new EngineRunState();
            ManualResetEvent inside = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);
            int waits = 0;
            int runs = 0;

            Thread worker = new Thread(delegate()
            {
                state.Run(delegate()
                {
                    inside.Set();
                    release.WaitOne(JoinMs);
                });
            });
            worker.IsBackground = true;
            worker.Start();
            inside.WaitOne(JoinMs);

            bool started = state.RunWhenFree(delegate { runs++; },
                                             delegate(int ms) { waits++; }, 3, 1);

            T.False(started, "motore: dopo i tentativi previsti l'avvio si arrende");
            T.Eq(0, runs, "motore: e il secondo corpo non e' mai stato eseguito");
            T.Eq(2, waits, "motore: le attese sono quelle previste, non una di piu'");
            T.True(state.IsRunning, "motore: il ciclo in moto non e' stato disturbato");

            release.Set();
            worker.Join(JoinMs);
            T.True(state.RunWhenFree(delegate { runs++; }, null, 3, 1),
                   "motore: liberato il posto, il prossimo avvio entra");
            T.Eq(1, runs, "motore: e il corpo e' stato eseguito");
        }

        /// <summary>
        /// E' il caso del telefono: il ciclo esce da solo dopo il tetto dei
        /// tentativi, e il tocco successivo deve rimettere in moto il motore.
        /// </summary>
        private static void TheEngineCanRestartAfterTheBodyReturns()
        {
            EngineRunState state = new EngineRunState();
            int runs = 0;

            state.Run(delegate { runs++; });
            T.False(state.IsRunning, "motore: uscito da solo, il motore risulta fermo");
            T.True(state.Run(delegate { runs++; }), "motore: e il tocco successivo lo rimette in moto");
            T.Eq(2, runs, "motore: il secondo giro e' stato eseguito");
        }
    }
}
