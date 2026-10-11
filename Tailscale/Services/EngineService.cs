using System;
using System.Threading.Tasks;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
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

        private readonly RingLogSink _log = new RingLogSink(LogLines);
        private readonly object _gate = new object();

        private ControlClient _client;
        private PhoneStateStore _state;
        private bool _started;

        /// <summary>Vero se l'app si e' sospesa con il motore in moto: al ritorno si riparte.</summary>
        private bool _resumePending;

        public static readonly EngineService Current = new EngineService();

        private EngineService() { }

        /// <summary>Cambiato qualcosa che la UI mostra: stato, netmap o registro.</summary>
        public event Action Changed;

        public string DeviceName { get; private set; }

        /// <summary>Vero se il client e' stato avviato almeno una volta.</summary>
        public bool IsStarted { get { lock (_gate) return _started; } }

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
            get { return _log.Snapshot(); }
        }

        /// <summary>
        /// Avvia il client. Chiamarla due volte non fa nulla: una pagina che torna
        /// in primo piano non deve creare un secondo motore (due client sullo stesso
        /// stato si contenderebbero la stessa chiave).
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }

            _log.Log("avvio del motore");
            DeviceName = ReadDeviceName();
            _log.Log("dispositivo: " + DeviceName);
            // La pagina si ridisegna subito: al ritorno da una sospensione l'utente deve
            // vedere che il motore e' ripartito, invece di una schermata vecchia.
            RaiseChanged();

            _state = new PhoneStateStore(_log);

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

            RandomSourceIsUsable(options.Random, _log);

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
                lock (_gate) _started = false;
                RaiseChanged();
                return;
            }

            client.StatusChanged += OnClientChanged;
            client.NetmapChanged += OnClientChanged;

            lock (_gate) _client = client;

            // Il login e la netmap sono attese lunghe: la UI non deve restare ferma.
            Task.Factory.StartNew(delegate()
            {
                try
                {
                    client.Start();
                }
                catch (Exception ex)
                {
                    _log.Log("il motore si e' fermato con un errore (" + ex.GetType().Name + "): " + ex.Message);
                    RaiseChanged();
                }
            });
        }

        public void Stop()
        {
            ControlClient client = Client;
            if (client == null) return;

            _log.Log("arresto del motore");
            try { client.Stop(); }
            catch (Exception ex) { _log.Log("errore in arresto: " + ex.Message); }

            // Un motore fermo si puo' riaccendere: senza questo azzeramento Start()
            // esce subito, e dopo una sospensione l'app resterebbe ferma per sempre.
            lock (_gate) _started = false;
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

        /// <summary>
        /// Il core rifiuta una chiave degenere, ma non puo' vedere una sorgente che
        /// riempie con la **stessa sequenza** a ogni chiamata: due estrazioni identiche
        /// significherebbero la stessa identita' a ogni avvio, e quindi la stessa node
        /// key rifiutata per sempre dal server. Qui si guarda una volta, all'avvio.
        /// </summary>
        private static bool RandomSourceIsUsable(IRandomSource random, ILogSink log)
        {
            byte[] first = new byte[32];
            byte[] second = new byte[32];
            random.NextBytes(first);
            random.NextBytes(second);

            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i]) return true;
            }

            log.Log("ERRORE: la sorgente casuale del sistema restituisce sempre gli stessi byte; "
                    + "le chiavi sarebbero identiche a ogni avvio");
            return false;
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
