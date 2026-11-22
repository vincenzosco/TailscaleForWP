using System;
using System.Collections.Generic;

namespace Tailscale.Core
{
    /// <summary>Lo stato del collegamento, come risulta dal registro.</summary>
    public enum VpnLinkState
    {
        /// <summary>Nessun valore del piano e' al suo posto: la connessione non e' stata aggiunta.</summary>
        NotConfigured,

        /// <summary>Disattivato: i valori [off] sono al loro posto.</summary>
        Off,

        /// <summary>Attivato: i valori [on] sono al loro posto, verificati rileggendoli.</summary>
        On,

        /// <summary>Il telefono non ha l'interop unlock: nessuna scrittura tentata.</summary>
        Locked,

        /// <summary>L'accesso al registro non si e' verificato: nessuna scrittura tentata.</summary>
        Unknown,

        /// <summary>Una scrittura non e' stata confermata dalla rilettura.</summary>
        Failed
    }

    /// <summary>
    /// Il collegamento "VPN via interop" visto dal motore: aggiunge la connessione,
    /// la attiva, la disattiva, e lo stato che la pagina mostra viene **dal
    /// registro**, rileggendo i valori, non da un flag tenuto in memoria. E' la
    /// conferma che l'utente chiede: non "io ci ho provato", ma "i valori ci sono".
    ///
    /// Tre regole del piano 3 vivono qui, tutte e tre con il loro test:
    ///
    /// - **nessuna scrittura senza probe.** Se il telefono e' bloccato o il probe
    ///   non si completa, il piano non si tocca: un mezzo valore nel registro di
    ///   sistema e' peggio di nessun valore.
    /// - **backup prima della scrittura.** Ogni valore viene letto prima di essere
    ///   scritto e il precedente finisce nello store di backup; `Revert()` torna
    ///   esattamente a prima, comprese le assenze.
    /// - **rilettura dopo la scrittura.** Se il valore non torna com'era stato
    ///   scritto, lo stato e' `Failed` e il log nomina la voce (mai il valore:
    ///   nel registro dell'app vanno i nomi, non i contenuti).
    /// </summary>
    public sealed class VpnLink
    {
        private readonly object _gate = new object();
        private readonly IRegistryApi _api;
        private readonly ILogSink _log;
        private readonly InteropPlan _plan;
        private readonly IStateStore _backup;

        private VpnLinkState _state = VpnLinkState.NotConfigured;

        public VpnLink(IRegistryApi api, ILogSink log, InteropPlan plan, IStateStore backup)
        {
            if (api == null) throw new ArgumentNullException("api");
            if (log == null) throw new ArgumentNullException("log");
            if (plan == null) throw new ArgumentNullException("plan");
            if (backup == null) throw new ArgumentNullException("backup");
            _api = api;
            _log = log;
            _plan = plan;
            _backup = backup;
        }

        public VpnLinkState State
        {
            get { lock (_gate) return _state; }
        }

        /// <summary>
        /// Lo stato si rilegge dal registro. Chiamato all'avvio e dopo ogni
        /// scrittura: quello che la pagina mostra e' cio' che il telefono ha
        /// davvero, non cio' che l'app crede di aver fatto.
        /// </summary>
        public void Refresh()
        {
            lock (_gate)
            {
                try
                {
                    if (Matches(_plan.On)) { _state = VpnLinkState.On; return; }
                    if (Matches(_plan.Off)) { _state = VpnLinkState.Off; return; }
                    _state = VpnLinkState.NotConfigured;
                }
                catch (Exception)
                {
                    // Una lettura che fallisce non dice "spent": dice "non si sa".
                    _state = VpnLinkState.Unknown;
                }
            }
        }

        /// <summary>Aggiunge la connessione e la attiva, in un colpo solo.</summary>
        public void Connect()
        {
            lock (_gate)
            {
                if (!Writable()) return;
                if (!Apply(_plan.Connection)) return;
                if (!Apply(_plan.On)) return;
                _state = VpnLinkState.On;
                _log.Log("vpn: connessione aggiunta e attivata, valori confermati");
            }
        }

        /// <summary>Disattiva il collegamento. La connessione aggiunta resta: si toglie con Revert().</summary>
        public void Disconnect()
        {
            lock (_gate)
            {
                if (!Writable()) return;
                if (!Apply(_plan.Off)) return;
                _state = VpnLinkState.Off;
                _log.Log("vpn: collegamento disattivato, valori confermati");
            }
        }

        /// <summary>
        /// Rimette ogni valore come era prima della prima scrittura, compresi i
        /// valori che non c'erano (si cancellano). E' il "torna come prima" che un
        /// provisioning onesto deve avere.
        /// </summary>
        public void Revert()
        {
            lock (_gate)
            {
                if (!Writable()) return;

                List<RegistryEntry> entries = new List<RegistryEntry>();
                foreach (RegistryEntry entry in _plan.All()) entries.Add(entry);

                bool failed = false;
                for (int i = 0; i < entries.Count; i++)
                {
                    RegistryEntry entry = entries[i];
                    string saved = _backup.Read(BackupKey(entry));
                    if (saved == null) continue;

                    try
                    {
                        if (saved == RegValue.Absent) _api.Delete(entry.Hive, entry.Path, entry.Name);
                        else _api.Write(entry.Hive, entry.Path, entry.Name, RegValue.Parse(saved));
                        _backup.Delete(BackupKey(entry));
                    }
                    catch (Exception ex)
                    {
                        // Una voce che non si ripristina non deve fermare le
                        // altre: si logga e si continua, o una riga illeggibile
                        // lascia il registro ripristinato solo a meta'.
                        failed = true;
                        _state = VpnLinkState.Failed;
                        _log.Log("vpn: ripristino fallito per " + entry.FullName
                                 + " (" + ex.GetType().Name + ")");
                    }
                }

                if (failed) return;

                Refresh();
                _log.Log("vpn: valori precedenti ripristinati");
            }
        }

        private static string BackupKey(RegistryEntry entry)
        {
            return "vpn.bak." + entry.FullName;
        }

        private bool Writable()
        {
            InteropCapability capability = InteropProbe.Detect(_api);
            if (capability == InteropCapability.Locked)
            {
                _state = VpnLinkState.Locked;
                _log.Log("vpn: interop bloccato, nessuna scrittura");
                return false;
            }
            if (capability != InteropCapability.Unlocked)
            {
                _state = VpnLinkState.Unknown;
                _log.Log("vpn: accesso al registro non verificato, nessuna scrittura");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Scrittura con backup e rilettura. Si ferma alla prima voce che non
        /// torna: proseguire vorrebbe dire dichiarare attivo un collegamento che
        /// e' attivo solo a meta'.
        /// </summary>
        private bool Apply(IList<RegistryEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                RegistryEntry entry = entries[i];

                // Il backup si prende una volta sola: rieseguire Connect() non deve
                // salvare come "originale" il valore che abbiamo scritto noi.
                string backupKey = BackupKey(entry);
                if (_backup.Read(backupKey) == null)
                {
                    RegValue previous;
                    bool existed;
                    try { existed = _api.TryRead(entry.Hive, entry.Path, entry.Name, out previous); }
                    catch (Exception ex)
                    {
                        // Senza il valore precedente non c'e' ripristino possibile:
                        // non si scrive, e il motivo resta nel registro.
                        _state = VpnLinkState.Failed;
                        _log.Log("vpn: impossibile leggere il valore precedente di "
                                 + entry.FullName + " (" + ex.GetType().Name + "), nessuna scrittura");
                        return false;
                    }
                    _backup.Write(backupKey, existed ? previous.ToString() : RegValue.Absent);
                }

                try
                {
                    _api.Write(entry.Hive, entry.Path, entry.Name, entry.Value);
                }
                catch (Exception ex)
                {
                    _state = VpnLinkState.Failed;
                    _log.Log("vpn: scrittura fallita per " + entry.FullName
                             + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                    return false;
                }

                RegValue readBack;
                bool found;
                try { found = _api.TryRead(entry.Hive, entry.Path, entry.Name, out readBack); }
                catch (Exception ex)
                {
                    _state = VpnLinkState.Failed;
                    _log.Log("vpn: rilettura fallita per " + entry.FullName
                             + " (" + ex.GetType().Name + "), scrittura non confermata");
                    return false;
                }
                if (!found || !entry.Value.Equals(readBack))
                {
                    _state = VpnLinkState.Failed;
                    _log.Log("vpn: scrittura non confermata per " + entry.FullName);
                    return false;
                }
            }
            return true;
        }

        private bool Matches(IList<RegistryEntry> entries)
        {
            if (entries.Count == 0) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                RegistryEntry entry = entries[i];
                RegValue current;
                if (!_api.TryRead(entry.Hive, entry.Path, entry.Name, out current)) return false;
                if (!entry.Value.Equals(current)) return false;
            }
            return true;
        }
    }
}