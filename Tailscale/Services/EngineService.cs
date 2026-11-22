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

        /// <summary>
        /// Quanto, e ogni quanto, si aspetta che il ciclo precedente liberi il motore
        /// prima di dichiarare l'avvio rifiutato. Un arresto chiesto adesso chiude il
        /// socket in pochi millisecondi, ma il ciclo puo' essere nel mezzo di una
        /// pausa fra due tentativi: il margine e' di secondi, non di millisecondi.
        /// Oltre quello il rifiuto resta la risposta giusta, e la via d'uscita
        /// dell'utente resta il pulsante principale.
        /// </summary>
        private const int StartAttempts = 30;
        private const int StartWaitMilliseconds = 100;

        /// <summary>Le ultime righe, quelle che la schermata mostra.</summary>
        private readonly RingLogSink _ring = new RingLogSink(LogLines);

        /// <summary>Il registro su file: e' quello che sopravvive alla chiusura dell'app.</summary>
        private readonly TailLogFile _fileLog;

        /// <summary>Dove va ogni riga: la schermata e il file.</summary>
        private readonly ILogSink _log;

        /// <summary>
        /// Il collegamento "VPN via interop": si attiva da solo quando il motore
        /// si collega, si attiva e disattiva dalla pagina, e la sua conferma viene
        /// rileggendo il registro. Vive qui per un motivo preciso: la pagina non
        /// deve sapere nulla del registro, e il motore e' l'unico che sa quando il
        /// telefono e' davvero collegato.
        /// </summary>
        private readonly VpnLinkService _vpn;

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
            _vpn = new VpnLinkService(_log, new LocalSettingsAdapter());
            _vpn.Changed += RaiseChanged;

            // Lo stato del collegamento si rilegge dal registro all'avvio: la
            // pagina deve mostrare cio' che il telefono ha, non un'icona ferma.
            _vpn.RefreshAsync();
        }

        private string[] Snapshot()
        {
            return _ring.Snapshot();
        }

        /// <summary>Cambiato qualcosa che la UI mostra: stato, netmap o registro.</summary>
        public event Action Changed;

        private string _deviceName;

        /// <summary>
        /// Il nome del dispositivo. Non e' mai nullo, e questo e' il punto: la pagina
        /// lo legge al primo disegno, quando il motore -- che gira su un thread di
        /// lavoro -- non l'ha ancora scritto. Un TextBlock non accetta null
        /// (`TextBlock.put_Text` solleva ArgumentNullException anche per una stringa
        /// nulla), e l'eccezione arrivava sul thread della UI: l'app partiva con la
        /// schermata nera e nel registro non restava niente. Il motore lo riempie
        /// appena parte e notifica il cambio, quindi la pagina si aggiorna da sola.
        /// </summary>
        public string DeviceName
        {
            get { return _deviceName == null ? "" : _deviceName; }
            private set { _deviceName = value; }
        }

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

        /// <summary>Il collegamento "VPN via interop": la pagina ne legge lo stato e ne preme il pulsante.</summary>
        public VpnLinkService Vpn
        {
            get { return _vpn; }
        }

        /// <summary>
        /// Avvia il client, su un thread di lavoro: il login e la netmap sono attese
        /// lunghe e la pagina deve restare reattiva.
        ///
        /// Due chiamate ravvicinate non fanno nascere due motori, e **una richiesta
        /// che arriva mentre il ciclo precedente sta uscendo non si perde**: aspetta
        /// che il posto si liberi e riprova. E' il caso visto sul telefono il
        /// 2026-10-11: il ritorno in primo piano arrivava mentre il ciclo stava
        /// uscendo, l'avvio veniva rifiutato e spariva, e in stato "Accesso
        /// richiesto" il pulsante apre solo il browser: da li' il motore non
        /// ripartiva piu'. Il ciclo libera il motore quando esce -- anche quando esce
        /// per un errore -- ed e' quello che riaccende il pulsante principale.
        /// </summary>
        public void Start()
        {
            Task.Factory.StartNew(delegate()
            {
                // Il motore puo' essere occupato da un ciclo che sta uscendo: si aspetta
                // che si liberi. La prima attesa si registra una volta sola, ed e' la
                // riga che dice se la richiesta ha dovuto aspettare: senza di essa la
                // differenza fra "l'avvio entra subito" e "l'avvio aspetta" non si
                // vedrebbe dal registro.
                bool waited = false;
                Action<int> wait = delegate(int milliseconds)
                {
                    if (!waited)
                    {
                        waited = true;
                        _log.Log("il motore e' ancora in uscita: aspetto che si liberi");
                    }
                    WaitBetweenStartAttempts(milliseconds);
                };

                try
                {
                    if (!_run.RunWhenFree(RunEngine, wait, StartAttempts, StartWaitMilliseconds))
                    {
                        // Il ciclo precedente non ha liberato il motore entro il
                        // margine: la richiesta non parte. La pagina mostra ancora lo
                        // stato di prima, e il pulsante principale resta la via per
                        // riprovare.
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
        ///
        /// Il ritorno puo' arrivare mentre il ciclo precedente sta ancora uscendo (basta
        /// tornare all'app subito dopo averla lasciata): la richiesta non viene persa,
        /// perche' l'avvio aspetta che il motore si liberi invece di rifiutare.
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

        /// <summary>
        /// Scrive nel registro un'eccezione non gestita, con il tipo, il messaggio,
        /// le cause e lo stack. E' la traccia che altrimenti sul telefono non resta
        /// da nessuna parte: la si legge dal file, senza un debugger collegato.
        /// </summary>
        public void LogException(string what, Exception error)
        {
            if (error == null)
            {
                _log.Log(what + ": nessuna eccezione (da chiarire)");
                return;
            }

            _log.Log(what + ": " + error.GetType().FullName + ": " + error.Message);

            // Le cause: quella interna e' spesso l'unica che dice cosa e' successo
            // davvero (l'esterno dice solo quale strato se n'e' accorto).
            Exception cause = error.InnerException;
            for (int depth = 0; cause != null && depth < 4; depth++)
            {
                _log.Log("  causa: " + cause.GetType().FullName + ": " + cause.Message);
                cause = cause.InnerException;
            }

            if (error.StackTrace != null) _log.Log(error.StackTrace);
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
            // Il motore e' cambiato stato: se e' collegato davvero, il
            // collegamento via interop parte da solo. L'errore qui non deve
            // disturbare il motore: resta nel registro.
            try { _vpn.OnEngineState(Status.State); }
            catch (Exception ex)
            {
                _log.Log("vpn: errore sul cambio di stato (" + ex.GetType().Name + "): " + ex.Message);
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>
        /// L'attesa fra due tentativi di avvio. Sul telefono non c'e'
        /// `System.Threading.Thread`: la fornisce `Task.Delay`, lo stesso schema di
        /// `ControlClientOptions.Sleep`. Un'attesa interrotta non e' un errore qui:
        /// il tentativo successivo e' gia' in programma, ed e' il ciclo che decide.
        /// </summary>
        private static void WaitBetweenStartAttempts(int milliseconds)
        {
            try { Task.Delay(milliseconds).Wait(); }
            catch (Exception) { }
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
