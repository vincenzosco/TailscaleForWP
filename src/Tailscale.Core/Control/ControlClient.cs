using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Tailscale.Core.Keys;
using Tailscale.Core.Net;
using Tailscale.Core.Protocol;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Core.Control
{
    public enum ControlState
    {
        Stopped,
        Connecting,
        NeedLogin,
        Authorized,
        Error
    }

    /// <summary>Quello che la UI mostra: stato, URL di login riapribile, errore, conteggi.</summary>
    public sealed class ControlStatus
    {
        public ControlState State = ControlState.Stopped;

        /// <summary>Presente in NeedLogin. Resta disponibile finche' l'utente non accede.</summary>
        public string LoginUrl;

        /// <summary>Presente in Error: testo leggibile, senza segreti.</summary>
        public string ErrorMessage;

        public int PeerCount;
        public string DnsSuffix = "";
        public string SelfHostname = "";
        public bool BrowserOpened;

        public ControlStatus Clone()
        {
            return (ControlStatus)MemberwiseClone();
        }

        public override string ToString()
        {
            switch (State)
            {
                case ControlState.NeedLogin: return "NeedLogin (login pronto)";
                case ControlState.Authorized:
                    return "Authorized (" + PeerCount + " peer, dns=" + DnsSuffix + ")";
                case ControlState.Error: return "Error: " + ErrorMessage;
                default: return State.ToString();
            }
        }
    }

    /// <summary>Una risposta HTTP del control server sul canale Noise.</summary>
    public sealed class ControlResponse
    {
        public int StatusCode;
        public string Body = "";

        public bool IsSuccess { get { return StatusCode == 200; } }
    }

    /// <summary>
    /// Il canale verso il control server, come lo vede ControlClient.
    ///
    /// E' un'interfaccia perche' cosi' la macchina a stati (login, rotazione delle
    /// chiavi, watchdog, backoff) si puo' provare senza rete e senza Tailscale: un
    /// test fornisce risposte finte e osserva le transizioni.
    /// </summary>
    public interface IControlConnection : IDisposable
    {
        ControlResponse Post(string path, string jsonBody, List<KeyValuePair<string, string>> headers);

        /// <summary>
        /// Apre uno stream e chiama onJson per ogni valore JSON che arriva: e' come
        /// si legge la netmap, che e' un flusso senza fine invece di una risposta
        /// sola.
        /// </summary>
        void PostStream(
            string path, string jsonBody, List<KeyValuePair<string, string>> headers,
            Action<string> onJson, int watchdogSeconds);

        /// <summary>Segnala che il canale non e' piu' utilizzabile e va rifatto.</summary>
        bool IsBroken { get; }
    }

    public sealed class ControlClientOptions
    {
        public string Hostname = "device";
        public string OSVersion = "";
        public string ServerUrl = ProtocolConstants.DefaultControlUrl;
        public string ControlHost = ProtocolConstants.ControlServerHost;
        public ushort Version = ProtocolConstants.CapabilityVersion;

        public KeyPair MachineKeyPair;
        public NodeKey NodeKey;
        public DiscoKey DiscoKey;

        public IRandomSource Random;
        public ILogSink Log = NullLogSink.Instance;
        public IStreamConnector Connector;
        public IHttpFetcher Fetcher;

        /// <summary>
        /// Dove tenere cio' che deve sopravvivere al riavvio (chiave macchina, node
        /// key, URL di login). Se e' null il client vive solo in memoria.
        /// </summary>
        public IStateStore State;

        /// <summary>
        /// Chiave di autenticazione ("tskey-auth-..."): se e' valorizzata la
        /// registrazione non ha bisogno che l'utente apra il browser (requisito R6).
        /// </summary>
        public string AuthKey;

        /// <summary>
        /// Come aprire l'URL di login. Se resta null il client non apre nulla: e'
        /// il chiamante a decidere (la UI del telefono usa il browser di sistema).
        /// </summary>
        public Func<string, bool> BrowserOpener;

        public int MapWatchdogSeconds = ProtocolConstants.MapStreamWatchdogSeconds;
        public int LoginPollMilliseconds = 3000;
        public int LoginTimeoutSeconds = 600;

        /// <summary>
        /// Come attendere fra due tentativi. Lo fornisce chi ospita il core: il
        /// profilo WP8.1 non espone System.Threading.Thread, quindi il core non puo'
        /// chiamare Thread.Sleep (e un no-op predefinito sarebbe un ciclo attivo).
        /// </summary>
        public Action<int> Sleep;

        /// <summary>
        /// Sostituisce il canale vero con uno finto. Serve ai test per esercitare
        /// login, rotazione delle chiavi e watchdog senza rete e senza Tailscale.
        /// </summary>
        public Func<IControlConnection> ConnectionFactory;
    }

    /// <summary>
    /// Il client di controllo: si connette, registra il nodo, espone l'URL di login
    /// quando serve e tiene vivo lo stream della netmap.
    ///
    /// Non apre mai il browser da solo e non decide per l'utente: se il login serve,
    /// lo stato diventa NeedLogin e l'URL resta in <see cref="ControlStatus.LoginUrl"/>
    /// finche' il chiamante vuole riaprirlo.
    /// </summary>
    public sealed class ControlClient : IDisposable
    {
        private readonly ControlClientOptions _options;
        private readonly ControlStatus _status = new ControlStatus();
        private readonly NetworkMap _netmap = new NetworkMap();
        private readonly object _gate = new object();

        private IControlConnection _connection;
        private ControlServerKeys _serverKeys;
        private volatile bool _stopping;
        private int _generations;

        public ControlClient(ControlClientOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.Random == null) throw new ArgumentException("serve una sorgente casuale", "options");
            if (options.Sleep == null) throw new ArgumentException("serve una funzione di attesa", "options");
            if (options.MachineKeyPair == null && options.State == null)
                throw new ArgumentException("serve una coppia di chiavi della macchina o uno IStateStore da cui leggerla", "options");

            _options = options;

            // Prima si recupera quello che c'e' gia': generare chiavi nuove a ogni
            // avvio farebbe apparire il telefono come un nodo diverso a ogni lancio,
            // e il tailnet si riempirebbe di dispositivi fantasma.
            if (_options.State != null) RestoreState();

            if (_options.MachineKeyPair == null)
            {
                _options.MachineKeyPair = KeyPair.Generate(_options.Random);
                SaveMachineKey();
            }
            if (_options.NodeKey == null)
            {
                _options.NodeKey = GenerateNodeKey();
                SaveNodeKey();
            }
            if (_options.DiscoKey == null) _options.DiscoKey = GenerateDiscoKey();
        }

        // Nomi dei valori nello stato: cambiarli significa far ripartire da zero
        // chi ha gia' un'app installata, quindi restano quelli (spec §5).
        private const string StateMachineKey = "machineKey";
        private const string StateNodeKey = "nodeKey";
        private const string StateAuthUrl = "authUrl";

        /// <summary>Rilegge chiavi e URL salvati. Un valore illeggibile si logga e si rigenera.</summary>
        private void RestoreState()
        {
            string machine = _options.State.Read(StateMachineKey);
            if (!string.IsNullOrEmpty(machine))
            {
                try
                {
                    _options.MachineKeyPair = KeyMaterial.LoadPrivate(machine);
                }
                catch (Exception ex)
                {
                    _options.Log.Log("stato: la chiave macchina salvata non e' leggibile ("
                                     + ex.Message + "), ne genero una nuova");
                }
            }

            string node = _options.State.Read(StateNodeKey);
            if (!string.IsNullOrEmpty(node))
            {
                try
                {
                    _options.NodeKey = NodeKey.Parse(node);
                }
                catch (Exception ex)
                {
                    _options.Log.Log("stato: la node key salvata non e' leggibile (" + ex.Message
                                     + "), ne genero una nuova");
                }
            }

            string authUrl = _options.State.Read(StateAuthUrl);
            if (!string.IsNullOrEmpty(authUrl))
            {
                lock (_gate) _status.LoginUrl = authUrl;
                _options.Log.Log("stato: c'e' un URL di login salvato da un avvio precedente");
            }
        }

        private void SaveMachineKey()
        {
            if (_options.State == null) return;
            _options.State.Write(StateMachineKey, KeyMaterial.SavePrivate(_options.MachineKeyPair));
        }

        private void SaveNodeKey()
        {
            if (_options.State == null) return;
            _options.State.Write(StateNodeKey, _options.NodeKey.ToString());
        }

        /// <summary>Salva l'URL di login, o lo dimentica se non ce n'e' piu' bisogno.</summary>
        private void SaveAuthUrl(string url)
        {
            if (_options.State == null) return;
            if (string.IsNullOrEmpty(url)) _options.State.Delete(StateAuthUrl);
            else _options.State.Write(StateAuthUrl, url);
        }

        public ControlStatus Status
        {
            get { lock (_gate) return _status.Clone(); }
        }

        public NetworkMap Netmap { get { return _netmap; } }

        /// <summary>Numero di connessioni map aperte: serve ai test per contare i riavvii.</summary>
        public int ConnectionGeneration { get { return _generations; } }

        /// <summary>L'URL di login corrente, o null se non serve accedere.</summary>
        public string LoginUrl
        {
            get
            {
                lock (_gate) return _status.LoginUrl;
            }
        }

        /// <summary>
        /// Dimentica chiavi e URL salvati e riparte da un nodo nuovo. Non fa nulla
        /// di distruttivo sul server: il vecchio nodo resta nel tailnet finche' non
        /// lo si rimuove dall'admin console, ed e' scritto nel README.
        /// </summary>
        public void Logout()
        {
            Stop();

            if (_options.State != null)
            {
                _options.State.Delete(StateMachineKey);
                _options.State.Delete(StateNodeKey);
                _options.State.Delete(StateAuthUrl);
            }

            lock (_gate) _status.LoginUrl = null;

            _options.MachineKeyPair = KeyPair.Generate(_options.Random);
            _options.NodeKey = GenerateNodeKey();
            SaveMachineKey();
            SaveNodeKey();
            _options.Log.Log("logout: chiavi dimenticate e rigenerate");
        }

        public NodeKey NodeKey { get { return _options.NodeKey; } }

        public event Action NetmapChanged;
        public event Action StatusChanged;

        /// <summary>Vero se il chiamante ha chiesto di fermarsi.</summary>
        public bool IsStopping { get { return _stopping; } }

        /// <summary>
        /// Riapre l'URL di login con il browser fornito. Non cambia lo stato: se il
        /// browser non c'e', l'utente puo' riprovare piu' tardi e l'URL resta valido.
        /// </summary>
        public bool OpenLoginInBrowser()
        {
            string url;
            lock (_gate)
            {
                url = _status.LoginUrl;
                if (url == null) return false;
            }
            if (_options.BrowserOpener == null) return false;

            bool opened = _options.BrowserOpener(url);
            lock (_gate)
            {
                _status.BrowserOpened = opened;
            }
            _options.Log.Log("apertura del browser " + (opened ? "riuscita" : "non riuscita; l'URL resta disponibile"));
            return opened;
        }

        public void Stop()
        {
            _stopping = true;
            IControlConnection connection = _connection;
            if (connection != null)
            {
                try { connection.Dispose(); }
                catch (Exception) { }
            }
            SetState(ControlState.Stopped, null, null);
        }

        /// <summary>
        /// Ciclo completo: registra il nodo, attende l'autorizzazione (poll durante il
        /// login) e legge la netmap finche' non si chiama Stop.
        /// </summary>
        public void Start()
        {
            _stopping = false;

            try
            {
                SetState(ControlState.Connecting, null, null);

                if (!Register())
                {
                    // Register ha gia' impostato lo stato (Error).
                    return;
                }

                ReadMap();
            }
            catch (Exception ex)
            {
                // Tipo di eccezione compreso: distinguere un timeout di rete da un
                // JSON inatteso e' la differenza fra aspettare e correggere il codice.
                _options.Log.Log("errore del client di controllo (" + ex.GetType().Name
                                 + "): " + ex.Message);
                SetState(ControlState.Error, null, ex.Message);
            }
        }

        /// <summary>
        /// Registra e, se serve il login, aspetta. Restituisce false se lo stato e'
        /// Error (registrazione fallita in modo definitivo).
        /// </summary>
        private bool Register()
        {
            EnsureConnection();

            bool sentFollowup = false;
            int waitedSeconds = 0;

            while (!_stopping)
            {
                RegisterRequest request = new RegisterRequest();
                request.Version = _options.Version;
                request.NodeKey = _options.NodeKey.ToString();
                // Dopo una rotazione il server deve poter collegare la chiave nuova a
                // quella vecchia, altrimenti il nodo viene visto come nuovo.
                if (_oldNodeKey != null) request.OldNodeKey = _oldNodeKey;
                if (!string.IsNullOrEmpty(_options.AuthKey)) request.Auth = _options.AuthKey;
                request.Hostinfo = BuildHostinfo();
                // Followup dice al server di tenere aperta la risposta finche' l'utente
                // non ha visitato l'URL: senza di esso il server risponde subito.
                if (sentFollowup) request.Followup = "true";

                List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
                headers.Add(new KeyValuePair<string, string>(ProtocolConstants.LbHeaderName, request.NodeKey));

                string body = request.ToJson();
                _options.Log.Log("registrazione: tentativo (" + (sentFollowup ? "followup" : "primo")
                                 + ", atteso " + waitedSeconds + "s, nodeKey="
                                 + Short(request.NodeKey) + ", " + body.Length + " byte)");

                ControlResponse response = _connection.Post(
                    ProtocolConstants.RegisterPath, body, headers);

                _options.Log.Log("registrazione: risposta http " + response.StatusCode
                                 + ", " + (response.Body == null ? 0 : response.Body.Length) + " byte");

                if (!response.IsSuccess)
                {
                    // Un 401 o un errore di decifratura puo' voler dire che il server ha
                    // ruotato le chiavi: si ricaricano e si riprova una volta sola.
                    if (ReloadServerKeysAndReconnect(response.StatusCode))
                    {
                        EnsureConnection();
                        continue;
                    }

                    SetState(ControlState.Error, null,
                        "registrazione rifiutata: http " + response.StatusCode + " " + Trim(response.Body));
                    return false;
                }

                RegisterResponse parsed;
                try
                {
                    parsed = RegisterResponse.FromJson(response.Body);
                }
                catch (JsonParseException ex)
                {
                    SetState(ControlState.Error, null, "risposta di registrazione illeggibile: " + ex.Message);
                    return false;
                }

                _options.Log.Log("registrazione: nodeKeyExpired=" + parsed.NodeKeyExpired
                                 + ", machineAuthorized=" + parsed.MachineAuthorized
                                 + ", authURL=" + (parsed.AuthURL.Length > 0 ? "si" : "no"));

                if (parsed.Error.Length > 0)
                {
                    SetState(ControlState.Error, null, parsed.Error);
                    return false;
                }

                if (parsed.NodeKeyExpired)
                {
                    // Il server chiede una chiave nuova: si rigenera e si riregistra con
                    // OldNodeKey. Non si insiste all'infinito: una sola rotazione per
                    // risposta, come il client Go.
                    RotateNodeKey(parsed);
                    sentFollowup = false;
                    continue;
                }

                if (parsed.AuthURL.Length > 0)
                {
                    // Lo stato NeedLogin viene pubblicato solo dopo il tentativo di
                    // apertura: chi ascolta legge lo stato una volta sola e lo
                    // interpreta, quindi non deve trovarlo ancora incompleto (era
                    // il caso in cui il comando stampava "il browser non si e'
                    // aperto" e subito dopo il log diceva il contrario).
                    SetState(ControlState.NeedLogin, parsed.AuthURL, null, false);
                    SaveAuthUrl(parsed.AuthURL);
                    if (_options.BrowserOpener != null && !IsBrowserOpened())
                        OpenLoginInBrowser();
                    NotifyStatusChanged();

                    if (waitedSeconds >= _options.LoginTimeoutSeconds)
                    {
                        // Non e' un errore: l'URL resta valido e riapribile.
                        _options.Log.Log("login non completato entro " + _options.LoginTimeoutSeconds
                                         + "s; l'URL resta disponibile");
                        return false;
                    }

                    _options.Sleep(_options.LoginPollMilliseconds);
                    waitedSeconds += _options.LoginPollMilliseconds / 1000;
                    sentFollowup = true;
                    continue;
                }

                // Nessun AuthURL e nessuna scadenza: il nodo e' autorizzato.
                lock (_gate)
                {
                    _status.LoginUrl = null;
                }
                // Anche nello store: un URL salvato da un avvio precedente, se
                // restasse, al prossimo avvio l'app inviterebbe ad accedere un
                // dispositivo che e' gia' dentro.
                SaveAuthUrl(null);
                _options.Log.Log("nodo autorizzato; ultimo login: " + parsed.LoginName);
                return true;
            }

            return false;
        }

        /// <summary>Apre lo stream della netmap e lo legge finche' non si chiama Stop.</summary>
        private void ReadMap()
        {
            while (!_stopping)
            {
                try
                {
                    ReadMapOnce();
                    if (_stopping) return;
                    _options.Log.Log("stream della netmap chiuso; riconnessione");
                }
                catch (Exception ex)
                {
                    if (_stopping) return;
                    _options.Log.Log("stream della netmap interrotto: " + ex.Message);
                }

                if (!BackoffBeforeReconnect()) return;
                try
                {
                    EnsureConnection();
                }
                catch (Exception ex)
                {
                    _options.Log.Log("riconnessione fallita: " + ex.Message);
                }
            }
        }

        /// <summary>Backoff 1s -> 2s -> 5s -> 15s -> 60s, come il client Go.</summary>
        private int _backoffIndex;

        private bool BackoffBeforeReconnect()
        {
            int[] delays = new int[] { 1, 2, 5, 15, 60 };
            int seconds = delays[Math.Min(_backoffIndex, delays.Length - 1)];
            if (_backoffIndex < delays.Length - 1) _backoffIndex++;
            _options.Log.Log("nuovo tentativo tra " + seconds + "s");

            // Il sonno a pezzi permette a Stop di essere rapido.
            for (int i = 0; i < seconds * 10 && !_stopping; i++)
                _options.Sleep(100);
            return !_stopping;
        }

        private void ReadMapOnce()
        {
            MapRequest request = new MapRequest();
            request.Version = _options.Version;
            request.NodeKey = _options.NodeKey.ToString();
            request.DiscoKey = _options.DiscoKey.ToString();
            request.Hostinfo = BuildHostinfo();
            request.Stream = true;
            request.KeepAlive = true;

            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>(ProtocolConstants.LbHeaderName, request.NodeKey));

            _generations++;
            _backoffIndex = 0;

            // Lo stream map e' gestito dalla connessione: qui si legge a pezzi e si
            // applica ogni MapResponse. Il watchdog e' nella connessione, che chiude
            // lo stream se non arriva nulla: senza di esso un server muto lascerebbe
            // l'app bloccata per sempre.
            _connection.PostStream(ProtocolConstants.MapPath, request.ToJson(), headers,
                delegate(string json)
                {
                    MapResponse response = MapResponse.FromJson(json);
                    _netmap.Apply(response);

                    if (_netmap.IsAuthorized && _status.State != ControlState.Authorized)
                    {
                        PublishNetmap();
                        SetState(ControlState.Authorized, null, null);
                    }
                    else
                    {
                        PublishNetmap();
                    }
                }, _options.MapWatchdogSeconds);
        }

        private void PublishNetmap()
        {
            lock (_gate)
            {
                _status.PeerCount = _netmap.Peers.Count;
                _status.DnsSuffix = _netmap.DnsSuffix;
                _status.SelfHostname = _netmap.Self == null ? "" : _netmap.Self.Hostname;
            }

            Action handler = NetmapChanged;
            if (handler != null) handler();
        }

        private void SetState(ControlState state, string loginUrl, string error)
        {
            SetState(state, loginUrl, error, true);
        }

        /// <summary>
        /// Cambia stato e, se richiesto, avvisa chi ascolta. Il parametro esiste per
        /// i casi in cui lo stato ha ancora un passo da fare (l'apertura del
        /// browser) prima di essere quello definitivo.
        /// </summary>
        private void SetState(ControlState state, string loginUrl, string error, bool notify)
        {
            lock (_gate)
            {
                // Una riga per ogni transizione: se lo stato non cambia mai come
                // l'utente si aspetta, qui si vede dove si e' fermato.
                if (_status.State != state)
                    _options.Log.Log("stato: " + _status.State + " -> " + state
                                     + (error != null ? " (" + error + ")" : "")
                                     + (loginUrl != null ? " (URL di login disponibile)" : ""));

                _status.State = state;
                _status.ErrorMessage = error;
                if (state == ControlState.Authorized)
                {
                    // Autorizzato: l'URL non serve piu' e non va ripresentato al
                    // prossimo avvio (sarebbe un invito ad accedere di nuovo).
                    _status.LoginUrl = null;
                    SaveAuthUrl(null);
                }
                if (loginUrl != null) _status.LoginUrl = loginUrl;
            }

            if (notify) NotifyStatusChanged();
        }

        private void NotifyStatusChanged()
        {
            Action handler = StatusChanged;
            if (handler != null) handler();
        }

        private bool IsBrowserOpened()
        {
            lock (_gate) return _status.BrowserOpened;
        }

        /// <summary>Rigenera il node key e riregistra con OldNodeKey (Review Focus 2).</summary>
        private void RotateNodeKey(RegisterResponse response)
        {
            string previous = _options.NodeKey.ToString();
            _options.NodeKey = GenerateNodeKey();
            _options.Log.Log("il server ha chiesto una chiave nuova: " + previous.Substring(0, 12)
                             + "... -> " + _options.NodeKey.ToString().Substring(0, 12) + "...");

            // La nuova chiave va comunicata al server come OldNodeKey al prossimo giro;
            // il campo resta nella richiesta successiva.
            _oldNodeKey = previous;
        }

        private string _oldNodeKey;

        /// <summary>Ricarica le chiavi del server e rifa' l'handshake, una volta sola.</summary>
        private bool ReloadServerKeysAndReconnect(int statusCode)
        {
            if (statusCode != 401 && statusCode != 403) return false;
            if (_reloadedKeys) return false;
            _reloadedKeys = true;

            _options.Log.Log("il server ha risposto " + statusCode
                             + ": ricarico le chiavi e rifaccio l'handshake");
            try
            {
                DisposeConnection();
                return true;
            }
            catch (Exception ex)
            {
                _options.Log.Log("ricarica delle chiavi fallita: " + ex.Message);
                return false;
            }
        }

        private bool _reloadedKeys;

        private void EnsureConnection()
        {
            if (_connection != null && !_connection.IsBroken)
            {
                _options.Log.Log("canale di controllo: riuso la connessione esistente");
                return;
            }

            if (_connection != null) _options.Log.Log("canale di controllo: la connessione e' segnata rotta, la rifaccio");
            DisposeConnection();

            if (_options.ConnectionFactory != null)
            {
                _options.Log.Log("canale di controllo: connessione finta (test)");
                _connection = _options.ConnectionFactory();
                return;
            }

            if (_serverKeys == null)
            {
                _serverKeys = ControlServerKeyLoader.Load(
                    _options.Fetcher, _options.ServerUrl, _options.Version, _options.Log);
            }

            _options.Log.Log("canale di controllo: chiavi del server pronte (" + _serverKeys + ")");
            _options.Log.Log("canale di controllo: apro " + _options.ControlHost
                             + ", nodeKey=" + Short(_options.NodeKey.ToString())
                             + ", versione=" + _options.Version);

            NoiseTransport transport = ControlHttpDialer.Dial(
                _options.ControlHost, _options.MachineKeyPair, _serverKeys, _options.Version,
                _options.Random, _options.Connector, _options.Log);

            _connection = new NoiseControlConnection(transport, _options.Log);
            _options.Log.Log("canale di controllo: pronto");
        }

        private void DisposeConnection()
        {
            IControlConnection connection = _connection;
            _connection = null;
            if (connection != null)
            {
                try { connection.Dispose(); }
                catch (Exception) { }
            }
        }

        private Hostinfo BuildHostinfo()
        {
            Hostinfo hostinfo = new Hostinfo();
            hostinfo.Hostname = _options.Hostname;
            hostinfo.OS = "windows";
            hostinfo.OSVersion = _options.OSVersion;
            return hostinfo;
        }

        private NodeKey GenerateNodeKey()
        {
            KeyPair pair = KeyPair.Generate(_options.Random);
            return NodeKey.FromBytes(pair.PublicKey);
        }

        private DiscoKey GenerateDiscoKey()
        {
            KeyPair pair = KeyPair.Generate(_options.Random);
            return DiscoKey.FromBytes(pair.PublicKey);
        }

        /// <summary>I primi caratteri di una stringa pubblica (una chiave), per i log.</summary>
        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return "<nessuna>";
            return text.Length <= 18 ? text : text.Substring(0, 18) + "...";
        }

        private static string Trim(string text)
        {
            if (text == null) return "";
            string trimmed = text.Trim();
            return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
