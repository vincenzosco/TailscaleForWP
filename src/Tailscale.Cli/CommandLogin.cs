using System;
using System.Diagnostics;
using System.Text;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Cli
{
    /// <summary>
    /// Registra un nodo e attende l'autorizzazione.
    ///
    /// L'URL di login lo apre il sistema, non il client: il client espone solo
    /// l'URL e resta in NeedLogin. Se il browser non c'e' (o l'utente chiude la
    /// pagina) il comando continua a stampare l'URL e resta in attesa invece di
    /// dichiarare un errore: e' lo stesso comportamento che serve all'app del
    /// telefono, dove il browser si apre con Launcher.LaunchUriAsync.
    /// </summary>
    public static class CommandLogin
    {
        public static int Run(ILogSink log)
        {
            IRandomSource random = new DesktopRandomSource();

            ControlClientOptions options = new ControlClientOptions();
            options.Hostname = Environment.MachineName;
            options.MachineKeyPair = KeyPair.Generate(random);
            options.Random = random;
            options.Connector = new TcpStreamConnector();
            options.Fetcher = new DesktopHttpFetcher();
            options.Log = log;
            options.OSVersion = Environment.OSVersion.Version.ToString();
            options.Sleep = delegate(int milliseconds) { System.Threading.Thread.Sleep(milliseconds); };
            options.LoginTimeoutSeconds = KnownLoginSeconds;
            options.BrowserOpener = OpenInSystemBrowser;

            bool reported = false;
            bool sawLoginUrl = false;
            ControlClient client = new ControlClient(options);

            client.StatusChanged += delegate()
            {
                ControlStatus status = client.Status;
                if (status.State == ControlState.NeedLogin && status.LoginUrl != null && !sawLoginUrl)
                {
                    sawLoginUrl = true;
                    Console.WriteLine();
                    Console.WriteLine("Apri questo URL per accedere: " + status.LoginUrl);
                    if (!status.BrowserOpened)
                        Console.WriteLine("(il browser non si e' aperto: apri l'URL a mano, resta valido)");
                    Console.WriteLine();
                }
                else if (status.State == ControlState.Error)
                {
                    Console.Error.WriteLine("ERRORE: " + status.ErrorMessage);
                }
            };

            client.NetmapChanged += delegate()
            {
                if (reported || !client.Netmap.IsAuthorized) return;
                reported = true;

                ControlStatus status = client.Status;
                Console.WriteLine("authorized: " + status.PeerCount + " peer, dns=" + status.DnsSuffix);
                Console.WriteLine("nodo: " + status.SelfHostname);
                Console.WriteLine();

                foreach (NodeInfo peer in client.Netmap.Peers)
                    Console.WriteLine("  " + peer.ToString()
                                      + (peer.Addresses.Count > 0 ? "  " + peer.Addresses[0] : ""));

                client.Stop();
            };

            client.Start();

            ControlStatus final = client.Status;
            if (final.State == ControlState.Authorized)
            {
                Console.WriteLine();
                Console.WriteLine("login completato; lo stato e' persistito dal client se un IStateStore e' configurato");
                return 0;
            }
            if (final.State == ControlState.Error) return 1;

            // Nessun errore: il login e' semplicemente ancora in sospeso.
            if (final.LoginUrl != null)
            {
                Console.WriteLine("login ancora in sospeso; URL valido: " + final.LoginUrl);
                return 3;
            }
            return 1;
        }

        private const int KnownLoginSeconds = 300;

        /// <summary>Apre l'URL con il browser predefinito del sistema.</summary>
        private static bool OpenInSystemBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception)
            {
                // Nessun browser associato o ambiente senza sessione grafica: non e' un
                // errore, l'URL resta stampato e riapribile.
                return false;
            }
        }
    }
}
