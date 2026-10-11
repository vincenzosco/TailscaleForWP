using System;
using Tailscale.Core;

namespace Tailscale.Core.Tests
{
    public static class Program
    {
        public static int Main()
        {
            Console.WriteLine("Tailscale.Core test runner (.NET " + Environment.Version + ")");

            Run("smoke", SmokeTests.Run);
            Run("protocol constants", ProtocolConstantsTests.Run);
            Run("x25519", X25519Tests.Run);
            Run("chacha20poly1305", ChaCha20Poly1305Tests.Run);
            Run("blake2s", Blake2sTests.Run);
            Run("sha256", Sha256Tests.Run);
            Run("hkdf", HkdfTests.Run);

            Console.WriteLine(T.Failures == 0 ? "ALL PASS" : "FAILURES: " + T.Failures);
            return T.Failures;
        }

        /// <summary>
        /// Un'eccezione non gestita in un test deve contare come fallimento e non
        /// interrompere il run: altrimenti un errore di battitura in un test
        /// nasconde tutti i test successivi.
        /// </summary>
        private static void Run(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL " + name + ": eccezione " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Smoke test del runner. Task 1 Step 3 ha usato un fallimento deliberato per
    /// dimostrare che il runner riporta il fallimento e che check-all.cmd propaga
    /// l'exit code (osservato: "FAILURES: 1", exit 1).
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

            NullLogSink.Instance.Log("ignorata");
            T.True(true, "null log: non lancia");
        }
    }
}
