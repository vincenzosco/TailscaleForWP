using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Quanto il telefono lascia fare al registro. Tre risposte, non due: un
    /// dispositivo sbloccato, uno bloccato, e il caso in cui il ciclo di
    /// scrittura non si e' completato in modo coerente. Da un solo indizio non si
    /// deduce "Unlocked": e' la regola del piano 3, e ha un motivo pratico --
    /// scrivere credendo di poterlo fare lascia mezzi valori nel registro del
    /// sistema, che e' peggio di non scrivere.
    /// </summary>
    public enum InteropCapability
    {
        Unlocked,
        Locked,
        Unknown
    }

    /// <summary>
    /// Il rilevamento, sull'interfaccia e non su una chiamata diretta: cosi' si
    /// prova senza telefono e il telefono usa la stessa identica regola.
    ///
    /// Il ciclo e': leggere una chiave innocua, poi scrivere un valore di prova
    /// nella chiave dell'app, rileggerlo e cancellarlo. Il valore di prova sta
    /// nella chiave dell'app perche' e' l'unica che possiamo creare senza
    /// inventare niente di sistema.
    /// </summary>
    public static class InteropProbe
    {
        public const string ProbeHive = "HKLM";
        public const string ProbePath = "SOFTWARE\\TailscaleWP";
        public const string ProbeName = "InteropProbe";

        public static InteropCapability Detect(IRegistryApi api)
        {
            if (api == null) throw new ArgumentNullException("api");

            try
            {
                // 1. lettura innocua: leggere non deve creare niente.
                RegValue ignored;
                api.TryRead(ProbeHive, ProbePath, ProbeName, out ignored);

                // 2. ciclo completo: scrivi, rileggi, cancella.
                RegValue probe = new RegValue(RegValue.Sz, "ok");
                api.Write(ProbeHive, ProbePath, ProbeName, probe);
                RegValue readBack;
                bool found = api.TryRead(ProbeHive, ProbePath, ProbeName, out readBack);
                api.Delete(ProbeHive, ProbePath, ProbeName);

                if (!found || !probe.Equals(readBack)) return InteropCapability.Unknown;
                return InteropCapability.Unlocked;
            }
            catch (UnauthorizedAccessException) { return InteropCapability.Locked; }
            catch (DllNotFoundException) { return InteropCapability.Locked; }
            catch (Exception ex)
            {
                // EntryPointNotFoundException non esiste nel profilo WP8.1, quindi
                // non si puo' nominare in un catch: si riconosce dal nome del tipo.
                // Vale come Locked: manca l'entry point di ntdll, non c'e' niente
                // da scrivere.
                if (ex.GetType().Name == "EntryPointNotFoundException") return InteropCapability.Locked;
                return InteropCapability.Unknown;
            }
        }
    }
}