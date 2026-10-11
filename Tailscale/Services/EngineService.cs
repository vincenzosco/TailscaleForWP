using System;
using System.Threading.Tasks;
using Tailscale.Core;
using Tailscale.Core.Control;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System;

namespace Tailscale.Services
{
    /// <summary>
    /// Il motore visto dall'app: un solo oggetto che possiede il client di
    /// controllo, il registro delle ultime righe e l'apertura del login.
    ///
    /// La pagina non conosce ne' il protocollo ne' i thread: si sottoscrive a
    /// `Changed` e rilegge `Status` e `Netmap`. Il client gira su un thread di
    /// lavoro perche' il login e la netmap sono attese lunghe; le notifiche
    /// arrivano da quel thread e chi aggiorna la UI rimanda tutto al dispatcher.
    /// </summary>
    public sealed class EngineService
    {
        private const int LogLines = 200;

        /// <summary>Un'ora: l'app resta in ascolto dell'esito del login finche' e' aperta.</summary>
        private const int PhoneLoginSeconds = 3600;

        /// <summary>Le ultime righe, quelle che la schermata mostra.</summary>
        private readonly RingLogSink _ring = new RingLogSink(LogLines);

        /// <summary>Il registro su file: e' quello che sopravvive alla chiusura dell'app.</summary>
        private readonly TailLogFile _fileLog;

        /// <summary>Dove va ogni riga: la schermata e il file.</summary>
        private readonly ILogSink _log;

        private readonly object _gate = new object();

        /// <summary>
        /// Il ciclo del motore: uno solo per volta, e si libera quando il ciclo esce.
        ///
        /// La regola sta nel core (con il suo test) e non qui: un flag tenuto
        /// dall'app sopravviveva alla fine del ciclo, e da li' veniva il guasto -- in
        /// stato di errore il pulsante principale non riavviava piu' niente.
        /// </summary>
        private readonly EngineRunState _run = new EngineRunState();

        private ControlClient _client;
        private VaultBackedStateStore _state;

        /// <summary>Vero se l'app si e' sospesa con il motore in moto: al ritorno si riparte.</summary>
        private bool _resumePending;

        public static readonly EngineService Current = new EngineService();

        private EngineService()
        {
            // Due destinazioni, una riga: le ultime righe per quello che si vede, e
            // un file perche' la diagnosi sopravviva alla sospensione, alla chiusura
            // e alla morte dell'app — prima di questo, un guasto sul telefono lasciava
            // solo cio' che l'utente aveva il tempo di copiare a mano dallo schermo.
            // Il file vero nasce alla prima riga: qui si costruisce solo la politica.
            _fileLog = new TailLogFile(new WinRtLogFile(), Snapshot, LogLines * 2, _ring);
            _log = new FanoutLogSink(_ring, _fileLog);
        }

        private string[] Snapshot()
        {
            return _ring.Snapshot();
        }

        /// <summary>Cambiato qualcosa che la UI mostra: stato, netmap o registro.</summary>
        public event Action Changed;

        public string DeviceName { get; private set; }

        /// <summary>
        /// Vero se il ciclo del motore e' in corso. Se il ciclo e' uscito da solo
        /// (budget dei tentativi esaurito) qui c'e' falso: e' la verita' che serve
        /// alla pagina per riaccendere il pulsante e alla sospensione per sapere se
        /// al ritorno c'e' qualcosa da riavviare.
        /// </summary>
        public bool IsStarted { get { return _run.IsRunning; } }

        public ControlStatus Status
        {
            get
            {
                ControlClient client = Client;
                return client == null ? new ControlStatus() : client.Status;
            }
        }

        public NetworkMap Netmap
        {
            get
            {
                ControlClient client = Client;
                return client == null ? new NetworkMap() : client.Netmap;
            }
        }

        private ControlClient Client
        {
            get { lock (_gate) return _client; }
        }

        /// <summary>Le ultime righe di registro, dalla piu' vecchia alla piu' recente.</summary>
        public string[] Log
        {
            get { return _ring.Snapshot(); }
        }

        /// <summary>Dove l'app scrive il registro, cosi' si puo' tirare fuori dal telefono.</summary>
        public string LogFileLocation
        {
            get { return _fileLog.Location; }
        }

        /// <summary>
        /// Avvia il client, su un thread di lavoro: il login e la netmap sono attese
        /// lunghe e la pagina deve restare reattiva.
        ///
        /// Due chiamate ravvicinate non fanno nascere due motori: la seconda non
        /// trova posto e lo dice nel registro. Il ciclo libera il motore quando esce
        /// -- anche quando esce per un errore -- ed e' quello che riaccende il
        /// pulsante principale.
        /// </summary>
        public void Start()
        {
            Task.Factory.StartNew(delegate()
            {
                try
                {
                    if (!_run.Run(RunEngine))
                    {
                        _log.Log("il motore e' gia' in moto: nessuna seconda partenza");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Un errore prima ancora di costruire il client (lettura del nome
                    // del dispositivo, costruzione dello stato) uscirebbe dalla task in
                    // silenzio: il motore si libera, ma l'utente vedrebbe la schermata
                    // di prima e nessuno saprebbe perche'. Una riga, e la pagina si
                    // ridisegna.
                    _log.Log("il ciclo del motore si e' fermato con un errore ("
                             + ex.GetType().Name + "): " + ex.Message);
                }

                // Il ciclo e' uscito: arresto voluto, budget esaurito, o un errore. La
                // pagina deve tornare a mostrare il pulsante che rimette in moto il
                // motore -- e quel pulsante ora funziona davvero.
                RaiseChanged();
            });
        }

        /// <summary>
        /// Il ciclo del motore: costruisce il client, lo lascia girare finche' non si
        /// ferma, e non torna prima. Gira sul thread di lavoro di Start().
        /// </summary>
        private void RunEngine()
        {
            _log.Log("avvio del motore");
            _log.Log("registro su file: " + LogFileLocation);
            DeviceName = ReadDeviceName();
            _log.Log("dispositivo: " + DeviceName);
            // La pagina si ridisegna subito: al ritorno da una sospensione l'utente deve
            // vedere che il motore e' ripartito, invece di una schermata vecchia.
            RaiseChanged();

            _state = new VaultBackedStateStore(new LocalSettingsAdapter(), new PasswordVaultAdapter(), _log);

            ControlClientOptions options = new ControlClientOptions();
            options.Hostname = DeviceName;
            options.OSVersion = ReadOsVersion();
            options.Random = new PhoneRandomSource();
            options.Log = _log;
            options.State = _state;
            options.Connector = new PhoneStreamConnector();
            options.Fetcher = new PhoneHttpFetcher();
            // Sul telefono non c'e' System.Threading.Thread: l'attesa la fornisce
            // chi ospita il core.
            options.Sleep = delegate(int milliseconds)
            {
                try { Task.Delay(milliseconds).Wait(); }
                catch (Exception) { }
            };
            // Il telefono non e' un comando: l'utente puo' metterci minuti ad accedere
            // nel browser. Il client continua a chiedere al server finche' l'app e' viva,
            // invece di arrendersi dopo dieci minuti e lasciare la pagina ferma su
            // "Accesso richiesto" anche se l'accesso e' stato completato.
            options.LoginTimeoutSeconds = PhoneLoginSeconds;

            // La sorgente casuale si controlla **prima** di costruire il client: una
            // sorgente che ripete la stessa sequenza produrrebbe la stessa identita' a
            // ogni avvio, il server rifiuterebbe quella node key perche' gia' nota, e
            // l'app finirebbe in un errore definitivo che non spiega niente. Il
            // controllo sta nel core (con il suo test); qui si obbedisce al risultato.
            if (!RandomSourceCheck.IsUsable(options.Random, _log))
            {
                _log.Log("il motore non parte: la sorgente casuale del sistema non e' utilizzabile");
                RaiseChanged();
                return;
            }

            // Il portachiavi si verifica all'avvio: se il sistema accetta la
            // scrittura ma restituisce altro in lettura, l'identita' non sopravvive al
            // riavvio. Non e' fatale — l'app funziona, semplicemente non ricorda chi
            // e' — quindi si prosegue, ma la riga dice la conseguenza, non il guasto.
            if (!_state.Keys.Verify(options.Random))
            {
                _log.Log("portachiavi: l'identita' non sopravvivera' al riavvio "
                         + "(il dispositivo apparira' nuovo a ogni avvio)");
            }

            ControlClient client;
            try
            {
                client = new ControlClient(options);
            }
            catch (Exception ex)
            {
                // Una sorgente casuale rotta o uno stato illeggibile non devono far
                // chiudere l'app: l'errore resta nel registro e si puo' ritentare.
                _log.Log("il motore non e' partito (" + ex.GetType().Name + "): " + ex.Message);
                RaiseChanged();
                return;
            }

            client.StatusChanged += OnClientChanged;
            client.NetmapChanged += OnClientChanged;

            lock (_gate) _client = client;

            // Questo e' il thread del ciclo: il client ci gira sopra, e non serve un
            // terzo thread. La chiamata torna quando il client si ferma -- arresto
            // voluto, o budget dei tentativi esaurito -- e da quel momento il motore
            // e' di nuovo avviabile.
            try
            {
                client.Start();
            }
            catch (Exception ex)
            {
                _log.Log("il motore si e' fermato con un errore (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        public void Stop()
        {
            ControlClient client = Client;
            if (client == null) return;

            _log.Log("arresto del motore");
            try { client.Stop(); }
            catch (Exception ex) { _log.Log("errore in arresto: " + ex.Message); }

            // Il motore non si segna fermo qui: e' l'uscita del ciclo a liberarlo
            // (EngineRunState). client.Stop() chiude il canale, il ciclo in corso esce,
            // e solo allora un nuovo avvio puo' partire: due client sullo stesso stato
            // si contenderebbero la stessa chiave.
            RaiseChanged();
        }

        /// <summary>
        /// L'app va in secondo piano. Il motore lo fermiamo noi, adesso, invece di
        /// lasciare che sia il sistema a interrompere la lettura in corso: un socket
        /// chiuso da noi e' un arresto riconosciuto, e non diventa un errore. Di cosa
        /// stava facendo si tiene memoria solo per il ritorno.
        /// </summary>
        public void Suspend()
        {
            // IsStarted e' vero solo se il ciclo e' davvero in moto: se il motore si
            // era fermato da solo, al ritorno non c'e' niente da riavviare d'ufficio,
            // ed e' giusto cosi' (l'utente decide, con il pulsante).
            _resumePending = IsStarted;
            Stop();
        }

        /// <summary>
        /// L'app torna in primo piano. Se il motore stava girando riparte: il canale
        /// di controllo viene rifatto (quello di prima e' morto con la sospensione) e
        /// la netmap riletta, riusando l'identita' salvata -- il device nel tailnet
        /// resta uno.
        /// </summary>
        public void Resume()
        {
            if (!_resumePending) return;
            _resumePending = false;
            Start();
        }

        /// <summary>
        /// Apre l'URL di login nel browser di sistema. Non si usa una WebView: Google
        /// rifiuta il login dentro i browser incorporati (spec §2.5).
        /// </summary>
        public bool OpenLogin()
        {
            ControlClient client = Client;
            if (client == null)
            {
                _log.Log("nessun URL di login: il motore non e' avviato");
                return false;
            }

            string url = client.LoginUrl;
            if (string.IsNullOrEmpty(url))
            {
                _log.Log("nessun URL di login da aprire");
                return false;
            }

            try
            {
                bool launched = Launcher.LaunchUriAsync(new Uri(url)).AsTask().Result;
                _log.Log(launched ? "browser aperto sull'URL di login" : "il browser non si e' aperto");
                return launched;
            }
            catch (Exception ex)
            {
                // Senza browser associato l'URL resta valido e riapribile.
                _log.Log("apertura del browser fallita (" + ex.GetType().Name + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>Dimentica chiavi e stato: il prossimo avvio e' un nodo nuovo.</summary>
        public void Logout()
        {
            ControlClient client = Client;
            if (client == null) return;

            try { client.Logout(); }
            catch (Exception ex) { _log.Log("logout fallito: " + ex.Message); }
            RaiseChanged();
        }

        private void OnClientChanged()
        {
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        private static string ReadDeviceName()
        {
            try
            {
                string name = new EasClientDeviceInformation().FriendlyName;
                return string.IsNullOrEmpty(name) ? "windows-phone" : name;
            }
            catch (Exception)
            {
                return "windows-phone";
            }
        }

        private static string ReadOsVersion()
        {
            try
            {
                EasClientDeviceInformation info = new EasClientDeviceInformation();
                return info.OperatingSystem + " " + info.SystemProductName;
            }
            catch (Exception)
            {
                return "Windows Phone 8.1";
            }
        }
    }
}
