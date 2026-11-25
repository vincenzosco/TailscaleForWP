using System;
using System.Threading.Tasks;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Native;
using Windows.Storage;

namespace Tailscale.Services
{
    /// <summary>
    /// Il collegamento "VPN via interop" dentro l'app: aggiunge la connessione,
    /// la attiva, la disattiva, e lo stato che la pagina mostra viene riletto dal
    /// registro. Tutto in una volta, come chiesto: quando il motore si collega il
    /// collegamento parte da solo (se l'interruttore e' acceso), e il pulsante
    /// della pagina lo attiva e lo disattiva.
    ///
    /// Un solo canale automatico: **telnet** (127.0.0.1:23), cioe' `reg` girato
    /// dentro CMD Injector con i diritti che servono. L'app non scrive il registro
    /// dalla propria sandbox e non ha altre strade: se il probe non passa non si
    /// scrive niente e la pagina lo dice, e il provisioning manuale resta
    /// `provision.bat` eseguito da CMD Injector.
    ///
    /// Il manifest e' quello integrato, oppure `vpn-manifest.txt` nella cartella
    /// dell'app: con ISETool lo si copia dentro senza ricompilare, e diventa il
    /// posto dove stanno le chiavi vere del proprio collegamento.
    /// </summary>
    public sealed class VpnLinkService
    {
        private const string ManifestFileName = "vpn-manifest.txt";
        private const string AutoSettingName = "vpn.auto";
        private const string TelnetHost = "127.0.0.1";
        private const int TelnetPort = 23;

        private readonly object _gate = new object();
        private readonly ILogSink _log;
        private readonly IStateStore _store;

        private VpnLink _link;
        private string _channel = "";
        private VpnLinkState _state = VpnLinkState.NotConfigured;
        private bool _busy;

        public VpnLinkService(ILogSink log, IStateStore store)
        {
            if (log == null) throw new ArgumentNullException("log");
            if (store == null) throw new ArgumentNullException("store");
            _log = log;
            _store = store;
        }

        /// <summary>Lo stato come risulta dal registro: la pagina mostra questo.</summary>
        public VpnLinkState State
        {
            get { lock (_gate) return _state; }
        }

        /// <summary>Il canale verificato ("telnet") o la stringa vuota se non ce n'e'.</summary>
        public string Channel
        {
            get { lock (_gate) return _channel == null ? "" : _channel; }
        }

        /// <summary>
        /// L'interruttore "collega in automatico quando il motore si collega".
        /// Acceso di fabbrica: e' il comportamento chiesto. Cambiarlo non tocca
        /// niente nel sistema: decide solo se il collegamento parte da solo.
        /// </summary>
        public bool AutoEnabled
        {
            get
            {
                string saved = _store.Read(AutoSettingName);
                return saved == null || saved != "0";
            }
            set
            {
                _store.Write(AutoSettingName, value ? "1" : "0");
            }
        }

        /// <summary>Cambiato qualcosa che la pagina mostra.</summary>
        public event Action Changed;

        /// <summary>Il pulsante della pagina: attiva o disattiva, tutto in una volta.</summary>
        public void Toggle()
        {
            Task.Factory.StartNew(delegate() { RunExclusive(ToggleNow); });
        }

        /// <summary>
        /// Il motore cambia stato. Quando si collega davvero (Authorized) il
        /// collegamento via interop parte da solo, se l'interruttore e' acceso.
        /// </summary>
        public void OnEngineState(ControlState state)
        {
            if (state != ControlState.Authorized) return;
            if (!AutoEnabled) return;
            Task.Factory.StartNew(delegate() { RunExclusive(AutoConnectNow); });
        }

        /// <summary>Rilegge lo stato dal registro, in secondo piano.</summary>
        public void RefreshAsync()
        {
            Task.Factory.StartNew(delegate() { RunExclusive(RefreshNow); });
        }

        private void RunExclusive(Action work)
        {
            lock (_gate)
            {
                if (_busy)
                {
                    // Un'operazione alla volta: due scritture sovrapposte sullo
                    // stesso stato si pesterebbero i piedi. La richiesta in piu'
                    // si lascia perdere, il pulsante resta la via per riprovare.
                    _log.Log("vpn: c'e' gia' un'operazione in corso, questa si salta");
                    return;
                }
                _busy = true;
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                _log.Log("vpn: operazione fallita (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            finally
            {
                lock (_gate) _busy = false;
                RaiseChanged();
            }
        }

        private void ToggleNow()
        {
            VpnLink link = EnsureLink();
            if (link == null) return;

            link.Refresh();
            if (link.State == VpnLinkState.On)
            {
                link.Disconnect();
                AutoEnabled = false;
            }
            else
            {
                link.Connect();
                AutoEnabled = true;
            }
            lock (_gate) _state = link.State;
        }

        private void AutoConnectNow()
        {
            VpnLink link = EnsureLink();
            if (link == null) return;

            link.Refresh();
            if (link.State == VpnLinkState.On)
            {
                lock (_gate) _state = link.State;
                return;  // gia' attivo: non si riscrive niente
            }

            _log.Log("vpn: il motore e' collegato, attivo il collegamento via interop");
            link.Connect();
            lock (_gate) _state = link.State;
        }

        private void RefreshNow()
        {
            VpnLink link = EnsureLink();
            if (link == null) return;

            link.Refresh();
            lock (_gate) _state = link.State;
        }

        /// <summary>
        /// L'unico canale automatico, scelto una volta sola: telnet dentro CMD
        /// Injector, oppure niente. Il probe deve verificare il canale prima di
        /// ogni scrittura: senza verifica non si scrive.
        /// </summary>
        private VpnLink EnsureLink()
        {
            lock (_gate)
            {
                if (_link != null) return _link;
            }

            InteropPlan plan = LoadPlan();

            try
            {
                IRegistryApi telnet = new TelnetRegistryApi(new LocalTelnetShell(TelnetHost, TelnetPort));
                string failure;
                InteropCapability capability = InteropProbe.Detect(telnet, out failure);
                if (capability == InteropCapability.Unlocked)
                {
                    _log.Log("vpn: canale telnet (" + TelnetHost + ":" + TelnetPort + ") verificato");
                    lock (_gate)
                    {
                        _link = new VpnLink(telnet, _log, plan, _store);
                        _channel = "telnet";
                        return _link;
                    }
                }

                string reason = failure ?? capability.ToString();
                if (capability == InteropCapability.Unavailable)
                    _log.Log("vpn: telnet non raggiungibile (" + reason + "), nessuna scrittura");
                else if (reason.IndexOf("accesso negato", StringComparison.OrdinalIgnoreCase) >= 0)
                    _log.Log("vpn: telnet risponde ma CMD Injector non ha accesso al registro");
                else
                    _log.Log("vpn: telnet presente ma probe non verificato (" + reason + ")");
                _log.Log("vpn: il provisioning manuale resta provision.bat eseguito da CMD Injector");
                lock (_gate) _state = capability == InteropCapability.Unavailable
                    ? VpnLinkState.Unavailable
                    : VpnLinkState.Unknown;
                return null;
            }
            catch (Exception ex)
            {
                // Un guasto nuovo senza una riga di registro e' la cosa peggiore:
                // il perche' resta nel log dell'app, senza valori del registro.
                _log.Log("vpn: telnet non utilizzabile (" + ex.GetType().Name + ": " + ex.Message + ")");
                lock (_gate) _state = VpnLinkState.Unknown;
                return null;
            }
        }

        /// <summary>
        /// Il manifest: `vpn-manifest.txt` nella cartella dell'app se c'e',
        /// altrimenti quello integrato. Un file che non si legge NON si ignora in
        /// silenzio: il motivo resta nel registro e si usa il manifest integrato.
        /// </summary>
        private InteropPlan LoadPlan()
        {
            string text = null;
            try
            {
                StorageFile file = ApplicationData.Current.LocalFolder
                    .GetFileAsync(ManifestFileName).AsTask().Result;
                text = FileIO.ReadTextAsync(file).AsTask().Result;
            }
            catch (Exception)
            {
                // Nessun file: il manifest integrato e' la scelta giusta.
            }

            if (text == null) return InteropManifest.Parse(InteropManifest.Default);

            try
            {
                InteropPlan plan = InteropManifest.Parse(text);
                _log.Log("vpn: manifest letto da " + ManifestFileName);
                return plan;
            }
            catch (Exception ex)
            {
                _log.Log("vpn: " + ManifestFileName + " non si legge (" + ex.GetType().Name + ": "
                         + ex.Message + "), uso il manifest integrato");
                return InteropManifest.Parse(InteropManifest.Default);
            }
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }
    }
}