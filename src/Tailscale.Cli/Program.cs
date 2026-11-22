using System;
using System.IO;
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

            try
            {
                return Dispatch(args, log);
            }
            catch (Exception ex)
            {
                // Rete di sicurezza: un'eccezione non prevista deve lasciare una
                // traccia diagnostica completa invece di un messaggio del runtime.
                Console.Error.WriteLine("ECCEZIONE NON GESTITA: " + ex.GetType().FullName);
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("CAUSA: " + ex.InnerException.GetType().FullName
                                            + ": " + ex.InnerException.Message);
                }
                return 70;
            }
        }

        private static int Dispatch(string[] args, ILogSink log)
        {
            log.Log("tailscale-cli " + (args.Length == 0 ? "help" : string.Join(" ", args))
                    + " | .NET " + Environment.Version + " | " + Environment.OSVersion);

            string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();

            switch (command)
            {
                case "handshake":
                    return CommandHandshake.Run(log);
                case "login":
                    return CommandLogin.Run(log);
                case "probe":
                    return CommandProbe.Run(log);
                case "selftest":
                    return CommandSelfTest.Run();
                case "provision":
                    // provision [manifest] [cartella]: senza argomenti usa il
                    // manifest integrato e scrive in provisioning\out.
                    return CommandProvision.Run(log,
                        args.Length > 1 ? args[1] : null,
                        args.Length > 2 ? args[2] : Path.Combine("provisioning", "out"));
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
            Console.WriteLine("  login       registra il nodo e attende l'autorizzazione (stampa l'URL di login)");
            Console.WriteLine("  selftest    controlli offline (nessuna rete): parser delle chiavi e richiesta");
            Console.WriteLine("  provision   genera provision.bat e deprovision.bat per CMD Injector");
            Console.WriteLine("              (manifest opzionale come argomento, cartella di uscita opzionale)");
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
