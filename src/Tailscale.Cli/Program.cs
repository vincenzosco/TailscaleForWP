using System;
using System.Security.Cryptography;
using Tailscale.Core;
using Tailscale.Core.Keys;

namespace Tailscale.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            ILogSink log = new DelegateLogSink(delegate(string line)
            {
                Console.WriteLine("  " + line);
            });

            string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();

            switch (command)
            {
                case "handshake":
                    return CommandHandshake.Run(log);
                case "selftest":
                    return CommandSelfTest.Run();
                case "help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine("comando sconosciuto: " + args[0]);
                    PrintHelp();
                    return 2;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("tailscale-cli <comando>");
            Console.WriteLine();
            Console.WriteLine("  handshake   contatta il control server, complete l'handshake Noise ts2021");
            Console.WriteLine("  selftest    controlli offline (nessuna rete): parser delle chiavi e richiesta");
            Console.WriteLine("  help        questo messaggio");
            Console.WriteLine();
            Console.WriteLine("Le chiavi private non vengono mai stampate.");
        }
    }

    /// <summary>
    /// Sorgente casuale del desktop. Sul telefono la stessa interfaccia e' fornita
    /// da CryptographicBuffer: e' il motivo per cui IRandomSource e' iniettata
    /// invece di essere chiamata direttamente dal core.
    /// </summary>
    public sealed class DesktopRandomSource : IRandomSource
    {
        private static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        public void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            Rng.GetBytes(buffer);
        }
    }
}
