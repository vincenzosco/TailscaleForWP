using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Quanto il canale scelto lascia fare al registro: verificato, API nativa
    /// assente, oppure non verificato. Un accesso negato non distingue un sistema
    /// interop-locked dalla sandbox dell'app, quindi non viene presentato come
    /// prova che il telefono sia bloccato.
    /// </summary>
    public enum InteropCapability
    {
        Unlocked,
        Unavailable,
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
            string ignored;
            return Detect(api, out ignored);
        }

        /// <summary>
        /// Come <see cref="Detect(IRegistryApi)"/>, ma restituisce una causa
        /// diagnostica priva di valori del registro. L'accesso negato dal
        /// comando telnet o del sandbox non prova che il telefono sia
        /// interop-locked: un accesso negato riporta Unknown, non Locked.
        /// </summary>
        public static InteropCapability Detect(IRegistryApi api, out string failure)
        {
            if (api == null) throw new ArgumentNullException("api");
            failure = null;

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

                if (!found || !probe.Equals(readBack))
                {
                    failure = "il valore di prova non e' stato confermato";
                    return InteropCapability.Unknown;
                }
                return InteropCapability.Unlocked;
            }
            catch (UnauthorizedAccessException ex)
            {
                failure = Describe(ex);
                return InteropCapability.Unknown;
            }
            catch (DllNotFoundException ex)
            {
                failure = Describe(ex);
                return InteropCapability.Unavailable;
            }
            catch (Exception ex)
            {
                // EntryPointNotFoundException non esiste nel profilo WP8.1, quindi
                // non si puo' nominare in un catch: si riconosce dal nome del tipo.
                // Un accesso negato resta Unknown, perche' puo' essere sandbox; solo
                // l'assenza esplicita della DLL o dell'entry point e' Unavailable.
                failure = Describe(ex);
                if (ex.GetType().Name == "EntryPointNotFoundException") return InteropCapability.Unavailable;
                return InteropCapability.Unknown;
            }
        }

        private static string Describe(Exception ex)
        {
            string detail = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
            return detail + " (HRESULT 0x" + unchecked((uint)ex.HResult).ToString("X8") + ")";
        }
    }
}