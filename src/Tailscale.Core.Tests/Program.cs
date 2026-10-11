using System;
using Tailscale.Core;

namespace Tailscale.Core.Tests
{
    public static class Program
    {
        public static int Main()
        {
            Console.WriteLine("Tailscale.Core test runner (.NET " + Environment.Version + ")");

            SmokeTests.Run();

            Console.WriteLine(T.Failures == 0 ? "ALL PASS" : "FAILURES: " + T.Failures);
            return T.Failures;
        }
    }

    /// <summary>
    /// Smoke test del runner. Task 1 Step 3 ha usato un fallimento deliberato per
    /// dimostrare che il runner riporta il fallimento e che check-all.cmd propaga
    /// l'exit code (osservato: "FAILURES: 1", exit 1). Ora asserisce codice reale
    /// di Core: la finestra scorrevole del log che la UI del telefono userà.
    /// </summary>
    public static class SmokeTests
    {
        public static void Run()
        {
            RingLogSink ring = new RingLogSink(2);
            ring.Log("uno");
            ring.Log("due");
            ring.Log("tre");

            string[] snapshot = ring.Snapshot();
            T.Eq(2, snapshot.Length, "ring log: tiene solo le ultime N righe");
            T.Eq("due", snapshot[0], "ring log: scarta la riga più vecchia");
            T.Eq("tre", snapshot[1], "ring log: l'ultima riga è la più recente");

            DelegateLogSink actor = new DelegateLogSink(delegate(string line) { /* nessuna eccezione con input nullo */ });
            actor.Log(null);
            T.True(true, "delegate log: accetta null senza lanciare");

            NullLogSink.Instance.Log("ignorata");
            T.True(true, "null log: non lancia");
        }
    }
}
