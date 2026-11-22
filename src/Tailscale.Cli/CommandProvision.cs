using System;
using System.IO;
using Tailscale.Core;

namespace Tailscale.Cli
{
    /// <summary>
    /// Genera il payload per CMD Injector: `provision.bat` e `deprovision.bat`,
    /// dallo stesso manifest che l'app usa sul posto.
    ///
    /// E' il canale manuale, quello "via telnet cmdinjector": il telefono
    /// interop-unlocked esegue i comandi come SYSTEM, e il batch non e' altro che
    /// `reg query`, `reg add` e `reg delete` nell'ordine giusto, con il backup dei
    /// valori presenti prima di ogni scrittura e la verifica finale. Nel log
    /// vanno i **nomi** delle voci, mai i valori.
    /// </summary>
    public static class CommandProvision
    {
        public static int Run(ILogSink log, string manifestPath, string outDirectory)
        {
            if (log == null) throw new ArgumentNullException("log");
            if (outDirectory == null) throw new ArgumentNullException("outDirectory");

            string text;
            if (manifestPath == null)
            {
                text = InteropManifest.Default;
                log.Log("manifest: quello integrato nell'app");
            }
            else
            {
                text = File.ReadAllText(manifestPath);
                log.Log("manifest: " + manifestPath);
            }

            InteropPlan plan;
            try
            {
                plan = InteropManifest.Parse(text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("manifest non valido: " + ex.Message);
                return 2;
            }

            foreach (RegistryEntry entry in plan.All()) log.Log("voce: " + entry.FullName);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(outDirectory);
            string provision = Path.Combine(outDirectory, "provision.bat");
            string deprovision = Path.Combine(outDirectory, "deprovision.bat");

            File.WriteAllText(provision, ProvisionPayload.Generate(plan, stamp));
            File.WriteAllText(deprovision, ProvisionPayload.GenerateDeprovision(plan, stamp));

            log.Log("scritto: " + provision);
            log.Log("scritto: " + deprovision);
            log.Log("il backup nasce accanto ai due file quando il batch gira: backup-" + stamp + ".txt");
            log.Log("il registro non crea alcun tunnel: qui si scrive solo configurazione");
            return 0;
        }
    }
}