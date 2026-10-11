using System;
using System.Collections.Generic;
using System.IO;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Cosa deve sopravvivere al riavvio dell'app, e cosa deve restare leggibile
    /// quando il dispositivo ha un nome con accenti.
    ///
    /// Il test che conta di piu' e' il primo: se le chiavi non vengono riusate, ogni
    /// avvio crea un nodo nuovo nel tailnet e l'utente vede il proprio telefono
    /// moltiplicarsi — un guasto che non produce nessun errore, solo confusione.
    /// </summary>
    public static class ControlClientStateTests
    {
        public static void Run()
        {
            Case("riavvio: stesse chiavi", RestartKeepsKeys);
            Case("identita' nuova a ogni login", FreshIdentityForEachLoginAttempt);
            Case("login in sospeso: identita' conservata", PendingLoginKeepsIdentity);
            Case("stato avvelenato: identita' rigenerata", PoisonedStateRegeneratesEverything);
            Case("login senza browser", NeedLoginSurvivesWithoutBrowser);
            Case("corpo UTF-8", BodyKeepsAccents);
            Case("errore duplicato: recupero", RecoversFromDuplicateIdentityError);
            Case("errore duplicato su non-200", RecoversFromDuplicateOnNonSuccess);
            Case("errore duplicato ripetuto: stop", StopsAfterOneDuplicateRefresh);
            Case("canale rotto: non si riusa", BrokenChannelIsNotReused);
            Case("canale interrotto: la registrazione riprova", ChannelFailureDuringRegisterIsRetried);
            Case("canale interrotto sempre: si smette con un errore", RegisterGivesUpAfterTheBudget);
            Case("arresto durante la registrazione: non e' un errore", StoppingIsNotAnError);
            Case("riavvio dopo l'arresto: si registra di nuovo", RestartAfterStopRegistersAgain);
            Case("tetto dei guasti: solo quelli consecutivi", ChannelFailureBudgetCountsConsecutiveFailures);
        }

        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL stato/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RestartKeepsKeys()
        {
            MemoryStateStore store = new MemoryStateStore();

            FakeConnection first = new FakeConnection();
            first.RegisterBody = AuthorizedResponse();
            List<string> firstLog;
            ControlClient one = Run(store, first, null, out firstLog);

            T.True(first.RegisterCalls >= 1, "primo avvio: si registra");
            string nodeKeyOne = JsonString(first.LastRegisterBody, "NodeKey");
            T.True(nodeKeyOne != null && nodeKeyOne.Length > 0, "primo avvio: la richiesta porta la node key");
            T.Eq(64, store.Read("machineKey") == null ? 0 : store.Read("machineKey").Length,
                 "primo avvio: la chiave macchina e' salvata (64 caratteri esadecimali)");
            T.Eq(nodeKeyOne, store.Read("nodeKey"), "primo avvio: la node key e' salvata");
            string machineKeyOne = store.Read("machineKey");

            // Secondo avvio: nessuna chiave passata dall'esterno, solo lo store.
            FakeConnection second = new FakeConnection();
            second.RegisterBody = AuthorizedResponse();
            List<string> secondLog;
            ControlClient two = Run(store, second, null, out secondLog);

            T.Eq("1", store.Read("authorized"), "autorizzato: il flag e' persistito");

            T.Eq(nodeKeyOne, JsonString(second.LastRegisterBody, "NodeKey"),
                 "riavvio: la stessa node key, non una nuova");
            T.Eq(machineKeyOne, store.Read("machineKey"),
                 "riavvio: la chiave macchina non e' stata rigenerata");
            T.True(secondLog.Count > 0, "riavvio: il client ha girato e loggato");
            T.True(one.Status.State != ControlState.Error, "primo avvio: nessun errore");
            T.True(two.Status.State != ControlState.NeedLogin,
                   "riavvio: non chiede di accedere (era gia' autorizzato)");
        }

        /// <summary>
        /// Un tentativo di login senza autorizzazione precedente ne' login in corso
        /// non deve riusare le chiavi salvate: e' la chiave vecchia che il server
        /// conosce ancora a produrre "device already exists".
        /// </summary>
        private static void FreshIdentityForEachLoginAttempt()
        {
            MemoryStateStore store = new MemoryStateStore();
            // Sorgente con partenza diversa da quella del client (che parte da 0): se
            // le due coincidessero, la chiave "rigenerata" sarebbe identica a quella
            // vecchia e l'asserzione non proverebbe nulla.
            KeyPair old = KeyPair.Generate(new SequenceRandomSource(200));
            store.Write("machineKey", KeyMaterial.SavePrivate(old));
            store.Write("nodeKey", "nodekey:" + new string('b', 64));
            // Nessun "authorized" e nessun "authUrl": e' un nuovo tentativo di login.

            FakeConnection connection = new FakeConnection();
            connection.RegisterBody =
                "{\"AuthURL\":\"https://login.tailscale.com/a/xyz\",\"MachineAuthorized\":false}";
            List<string> lines;
            Run(store, connection, null, out lines);

            string sent = JsonString(connection.LastRegisterBody, "NodeKey");
            T.True(sent != "nodekey:" + new string('b', 64), "login: non si riusa la node key salvata");
            T.Eq(sent, store.Read("nodeKey"), "login: la nuova node key e' persistita");
            T.True(store.Read("machineKey") != KeyMaterial.SavePrivate(old),
                   "login: anche la chiave macchina e' nuova");
        }

        /// <summary>
        /// Un login gia' consegnato al browser deve sopravvivere al riavvio dell'app:
        /// l'URL e' legato a questa node key, e scartarli significa che l'autorizzazione
        /// appena fatta nel browser resta orfana e l'app non si aggiorna.
        /// </summary>
        private static void PendingLoginKeepsIdentity()
        {
            MemoryStateStore store = new MemoryStateStore();
            store.Write("machineKey", KeyMaterial.SavePrivate(KeyPair.Generate(new SequenceRandomSource(90))));
            store.Write("nodeKey", "nodekey:" + new string('c', 64));
            store.Write("authUrl", "https://login.tailscale.com/a/pending");

            ControlClient client = new ControlClient(Options(store, new FakeConnection()));

            T.Eq("nodekey:" + new string('c', 64), client.NodeKey.ToString(),
                 "login in sospeso: la node key resta quella autorizzata nel browser");
            T.Eq("https://login.tailscale.com/a/pending", store.Read("authUrl"),
                 "login in sospeso: l'URL resta per il prossimo avvio");
            T.Eq("https://login.tailscale.com/a/pending", client.LoginUrl,
                 "login in sospeso: l'URL e' disponibile al chiamante");
        }

        /// <summary>
        /// Uno stato scritto da una versione con la sorgente casuale rotta ha una
        /// machine key degenere (64 zeri) ma un nodeKey che si legge senza errori:
        /// entrambe le chiavi vanno sostituite, non solo quella macchina.
        /// </summary>
        private static void PoisonedStateRegeneratesEverything()
        {
            MemoryStateStore store = new MemoryStateStore();
            store.Write("machineKey", new string('0', 64));
            store.Write("nodeKey", "nodekey:" + new string('e', 64));
            store.Write("authorized", "1");

            ControlClient client = new ControlClient(Options(store, new FakeConnection()));

            T.True(client.NodeKey.ToString() != "nodekey:" + new string('e', 64),
                   "stato avvelenato: la node key costante non si riusa");
            T.Eq(client.NodeKey.ToString(), store.Read("nodeKey"),
                 "stato avvelenato: la nuova node key e' persistita");
            T.True(store.Read("machineKey") != new string('0', 64),
                   "stato avvelenato: la machine key e' nuova");
            T.Eq(null, store.Read("authorized"), "stato avvelenato: l'autorizzazione vecchia non vale piu'");
        }

        private static void NeedLoginSurvivesWithoutBrowser()
        {
            MemoryStateStore store = new MemoryStateStore();
            const string url = "https://login.tailscale.com/a/abc123";

            FakeConnection connection = new FakeConnection();
            connection.RegisterBody = "{\"AuthURL\":\"" + url + "\",\"MachineAuthorized\":false}";

            // Nessun browser: e' il caso del telefono senza IE associato.
            List<string> lines;
            ControlClient client = Run(store, connection, null, out lines);

            T.Eq(url, client.LoginUrl, "l'URL resta disponibile anche senza browser");
            T.Eq(ControlState.NeedLogin.ToString(), client.Status.State.ToString(),
                 "senza browser lo stato resta NeedLogin, non Error");
            T.Eq(url, store.Read("authUrl"), "l'URL e' salvato per il prossimo avvio");

            // Un riavvio lo ritrova senza doverlo richiedere al server.
            ControlClientOptions options = Options(store, new FakeConnection());
            ControlClient restarted = new ControlClient(options);
            T.Eq(url, restarted.LoginUrl, "dopo il riavvio l'URL e' ancora li'");

            // E quando il nodo risulta autorizzato, l'URL sparisce dallo store.
            FakeConnection authorized = new FakeConnection();
            authorized.RegisterBody = AuthorizedResponse();
            List<string> again;
            Run(store, authorized, null, out again);
            T.Eq(null, store.Read("authUrl"), "autorizzato: l'URL viene dimenticato");
        }

        private static void BodyKeepsAccents()
        {
            // Review Focus 1: un nome macchina italiano tipico.
            Hostinfo hostinfo = new Hostinfo();
            hostinfo.Hostname = "Pc-Di-Vincènzo";
            hostinfo.OS = "windows";

            RegisterRequest request = new RegisterRequest();
            request.NodeKey = "nodekey:" + new string('a', 64);
            request.Hostinfo = hostinfo;

            string json = request.ToJson();
            byte[] body = Text.Utf8Bytes(json);

            // Il JSON scritto dal nostro writer e' ASCII: i caratteri fuori ASCII
            // diventano \uXXXX, quindi il corpo non puo' degradarli nemmeno se la
            // conversione fosse ASCII. Questa riga fissa il meccanismo, cosi' se il
            // writer cambiasse si saprebbe da qui.
            T.True(json.Contains("\\u00e8"), "l'accento viene scritto come \\u00e8");

            JsonValue parsed = JsonBody.Parse(Text.FromUtf8(body));
            T.Eq("Pc-Di-Vincènzo", parsed["Hostinfo"]["Hostname"].AsString(""),
                 "l'hostname arriva intero, senza '?'");
            T.False(Text.FromUtf8(body).Contains("?"), "nessun carattere sostituito");

            // E la conversione usata dal canale di controllo non degrada nulla di
            // suo: testo accentato -> byte UTF-8 -> stesso testo.
            const string accented = "caffè àèìòù";
            byte[] utf8 = Text.Utf8Bytes(accented);
            T.True(utf8.Length > accented.Length, "UTF-8 usa piu' byte dei caratteri");
            T.Eq(accented, Text.FromUtf8(utf8), "e si rilegge identico");
        }

        /// <summary>
        /// Il server conosce ancora la node key di un device cancellato dal pannello
        /// admin: il client non deve fermarsi sull'errore, ma rigenerare l'identita'
        /// (machine key compresa, quindi con un nuovo handshake) e riprovare.
        /// </summary>
        private static void RecoversFromDuplicateIdentityError()
        {
            Script script = new Script();
            script.Enqueue(200, DuplicateError());
            script.Enqueue(200,
                "{\"AuthURL\":\"https://login.tailscale.com/a/xyz\",\"MachineAuthorized\":false}");
            MemoryStateStore store = AuthorizedStore();

            string firstNodeKey = null;
            ControlClient client = RunScript(store, script, delegate(string body)
            {
                if (firstNodeKey == null) firstNodeKey = JsonString(body, "NodeKey");
            });

            T.Eq(2, script.Calls, "duplicato: si riprova una volta");
            T.Eq(2, script.Connections, "duplicato: l'handshake viene rifatto (machine key nuova)");
            T.True(JsonString(script.LastRegisterBody, "NodeKey") != firstNodeKey,
                   "duplicato: il secondo tentativo usa una node key nuova");
            T.Eq(ControlState.NeedLogin.ToString(), client.Status.State.ToString(),
                 "duplicato: si arriva a NeedLogin, non a Error");
        }

        /// <summary>Lo stesso rifiuto puo' arrivare come corpo di una risposta non-200.</summary>
        private static void RecoversFromDuplicateOnNonSuccess()
        {
            Script script = new Script();
            script.Enqueue(403, DuplicateError());
            script.Enqueue(200, AuthorizedResponse());
            MemoryStateStore store = AuthorizedStore();

            // Il solo retry non basta a distinguere il recupero dal ricarico delle
            // chiavi di un 403: la prova e' che l'identita' sia davvero nuova.
            string firstNodeKey = null;
            ControlClient client = RunScript(store, script, delegate(string body)
            {
                if (firstNodeKey == null) firstNodeKey = JsonString(body, "NodeKey");
            });

            T.Eq(2, script.Calls, "non-200: si riprova una volta");
            T.True(JsonString(script.LastRegisterBody, "NodeKey") != firstNodeKey,
                   "non-200: anche qui l'identita' e' nuova");
            T.True(client.Status.State != ControlState.Error, "non-200: recuperato, nessun errore mostrato");
        }

        /// <summary>
        /// Se anche l'identita' nuova viene rifiutata, il client non deve rigenerare
        /// e riprovare all'infinito: dopo un solo refresh mostra l'errore.
        /// </summary>
        private static void StopsAfterOneDuplicateRefresh()
        {
            Script script = new Script();
            for (int i = 0; i < 4; i++) script.Enqueue(200, DuplicateError());
            MemoryStateStore store = AuthorizedStore();

            ControlClient client = RunScript(store, script, null);

            T.Eq(2, script.Calls, "duplicato ripetuto: al massimo un refresh");
            T.Eq(ControlState.Error.ToString(), client.Status.State.ToString(),
                 "duplicato ripetuto: l'errore viene mostrato invece di ciclare");
        }

        /// <summary>
        /// Un canale che ha dichiarato un guasto non va piu' usato: e' l'anello da
        /// cui dipende tutto il recupero. Se IsBroken non arrivasse al client, ogni
        /// riconnessione riuserebbe il canale morto e l'app resterebbe ferma sulla
        /// schermata vecchia senza che nulla lo dica.
        /// </summary>
        private static void BrokenChannelIsNotReused()
        {
            MemoryStateStore store = new MemoryStateStore();
            FakeConnection dead = new FakeConnection();
            dead.Broken = true;
            dead.RegisterBody = AuthorizedResponse();
            FakeConnection good = new FakeConnection();
            good.RegisterBody = AuthorizedResponse();

            List<string> lines;
            RunChannelSwap(store, dead, good, out lines);

            T.Eq(1, dead.RegisterCalls, "canale rotto: il primo avvio usa il canale che c'e'");
            T.True(good.RegisterCalls >= 1,
                   "canale rotto: al riavvio il canale viene rifatto invece di essere riusato");
            T.True(HasLine(lines, "la connessione e' segnata rotta"),
                   "canale rotto: il registro dice perche' il canale e' stato rifatto");
        }

        /// <summary>
        /// Il caso che l'utente ha visto: il sistema interrompe il socket mentre il
        /// client si registra, e l'interruzione e' passeggera. Prima della correzione
        /// finiva in `Error` e il motore si fermava li': l'utente autorizzava nel
        /// browser e l'app non si aggiornava piu'.
        /// </summary>
        private static void ChannelFailureDuringRegisterIsRetried()
        {
            MemoryStateStore store = new MemoryStateStore();
            FlakyConnection connection = new FlakyConnection(AuthorizedResponse(), 1);

            List<string> lines;
            ControlClient client = RunClient(store, connection, 3, out lines);

            T.Eq(2, connection.Calls, "canale interrotto: la registrazione viene ritentata una volta");
            T.True(HasLine(lines, "lo rifaccio"), "canale interrotto: il registro dice il nuovo tentativo");
            T.True(HasLine(lines, "nodo autorizzato"), "canale interrotto: la registrazione si conclude");
            T.False(HasLine(lines, "-> Error"), "canale interrotto: nessun errore mostrato");
            T.True(client.Status.State != ControlState.Error,
                   "canale interrotto: lo stato non e' Error");
        }

        /// <summary>
        /// Un canale morto per sempre deve finire con un errore comprensibile
        /// invece di ritentare all'infinito: la ritentata e' un rimedio, non una
        /// condanna a girare.
        /// </summary>
        private static void RegisterGivesUpAfterTheBudget()
        {
            MemoryStateStore store = new MemoryStateStore();
            FlakyConnection connection = new FlakyConnection(AuthorizedResponse(), int.MaxValue);

            List<string> lines;
            ControlClient client = RunClient(store, connection, 3, out lines);

            // Tetto 3: il primo tentativo piu' tre ritentate, poi l'errore.
            T.Eq(4, connection.Calls, "canale morto: i tentativi sono limitati dal tetto");
            T.True(HasLine(lines, "canale di controllo non disponibile"),
                   "canale morto: il registro lo dice");
            T.Eq(ControlState.Error.ToString(), client.Status.State.ToString(),
                 "canale morto: dopo il tetto si mostra l'errore invece di ciclare");
        }

        /// <summary>
        /// Il pulsante "Ferma" mentre una richiesta e' in volo, e la sospensione
        /// dell'app, devono fermare il motore: un arresto voluto non e' un errore e
        /// non deve diventare una schermata rossa.
        /// </summary>
        private static void StoppingIsNotAnError()
        {
            MemoryStateStore store = new MemoryStateStore();
            FlakyConnection connection = new FlakyConnection(AuthorizedResponse(), int.MaxValue);
            connection.Stopper = true;

            List<string> lines;
            ControlClient client = RunClient(store, connection, 3, out lines);

            T.Eq(1, connection.Calls, "arresto: non si ritenta dopo che il motore e' stato fermato");
            T.True(HasLine(lines, "in arresto"), "arresto: il registro dice perche' si e' smesso");
            T.False(HasLine(lines, "canale di controllo non disponibile"),
                    "arresto: nessun errore per una cosa voluta");
            T.Eq(ControlState.Stopped.ToString(), client.Status.State.ToString(),
                 "arresto: lo stato resta Fermo, non diventa Error");
        }

        /// <summary>
        /// Il motore del telefono viene fermato quando l'app si sospende e riavviato
        /// al ritorno: se un secondo Start() non rifacesse il lavoro, dopo una
        /// sospensione l'app resterebbe ferma per sempre -- ed e' il sintomo che
        /// l'utente vede. Il comportamento c'e' gia': il test lo fissa perche' la
        /// ripresa ci poggia sopra e nulla nel progetto lo difendeva.
        /// </summary>
        private static void RestartAfterStopRegistersAgain()
        {
            MemoryStateStore store = new MemoryStateStore();
            FakeConnection connection = new FakeConnection();
            connection.RegisterBody = AuthorizedResponse();

            List<string> captured = new List<string>();
            ControlClientOptions options = Options(store, connection);
            options.Log = new DelegateLogSink(captured.Add);

            ControlClient[] holder = new ControlClient[1];
            int sleeps = 0;
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                if (sleeps > 2 && holder[0] != null) holder[0].Stop();
            };

            ControlClient client = new ControlClient(options);
            holder[0] = client;

            client.Start();
            T.Eq(1, connection.RegisterCalls, "avvio: la registrazione parte");

            // E' la sospensione: l'app ferma il motore chiudendo lei il socket.
            client.Stop();
            sleeps = 0;

            // E' la ripresa: il motore deve rimettersi in moto da solo.
            client.Start();

            T.Eq(2, connection.RegisterCalls, "riavvio: dopo Stop il client si registra di nuovo");
            T.True(HasLine(captured, "stato: Stopped -> Connecting"), "riavvio: si riparte da fermo");
            T.True(client.Status.State != ControlState.Error, "riavvio: nessun errore");
        }

        /// <summary>
        /// Il tetto conta i guasti *consecutivi*. Un canale instabile -- un guasto
        /// ogni tanto mentre l'utente sta ancora accedendo nel browser -- non deve
        /// esaurire il budget e diventare un errore definitivo: la registrazione e'
        /// riuscita, il canale funziona, e l'ora di pazienza del login non e' finita.
        /// </summary>
        private static void ChannelFailureBudgetCountsConsecutiveFailures()
        {
            MemoryStateStore store = new MemoryStateStore();
            AlternatingConnection connection = new AlternatingConnection(
                "{\"AuthURL\":\"https://login.tailscale.com/a/xyz\",\"MachineAuthorized\":false}");

            List<string> captured = new List<string>();
            ControlClientOptions options = Options(store, new FakeConnection());
            options.Log = new DelegateLogSink(captured.Add);
            options.MaxChannelRetries = 1;
            options.LoginTimeoutSeconds = 6;
            options.LoginPollMilliseconds = 1000;
            options.ConnectionFactory = delegate() { return connection; };
            options.Sleep = delegate(int milliseconds) { };

            ControlClient client = new ControlClient(options);
            client.Start();

            T.True(connection.Failures >= 2, "tetto: il canale fallisce piu' di una volta");
            T.False(HasLine(captured, "canale di controllo non disponibile"),
                    "tetto: guasti non consecutivi non esauriscono il budget");
            T.Eq(ControlState.NeedLogin.ToString(), client.Status.State.ToString(),
                 "tetto: il login resta in corso invece di finire in errore");
        }

        /// <summary>La risposta esatta con cui il control server rifiuta una node key ancora nota.</summary>
        private static string DuplicateError()
        {
            return "{\"Error\":\"device with nodekey:nodekey:aabbcc already exists; "
                 + "please log out explicitly and try logging in again.\"}";
        }

        /// <summary>Uno store con un'identita' gia' autorizzata: il device che l'admin ha cancellato.</summary>
        private static MemoryStateStore AuthorizedStore()
        {
            MemoryStateStore store = new MemoryStateStore();
            store.Write("machineKey", KeyMaterial.SavePrivate(KeyPair.Generate(new SequenceRandomSource(150))));
            store.Write("nodeKey", "nodekey:" + new string('d', 64));
            store.Write("authorized", "1");
            return store;
        }

        /// <summary>
        /// Avvia un client su una connessione "a copione": la factory ne crea una
        /// nuova a ogni chiamata (cosi' si conta se l'handshake e' stato rifatto) e
        /// tutte condividono lo script delle risposte.
        /// </summary>
        private static ControlClient RunScript(IStateStore store, Script script, Action<string> onRegister)
        {
            ControlClientOptions options = Options(store, new FakeConnection());
            // Il test misura i tentativi di registrazione, non il polling del login:
            // un AuthURL chiude subito il ciclo invece di ripetersi ogni 3s.
            options.LoginTimeoutSeconds = 0;
            script.OnRegister = onRegister;
            options.ConnectionFactory = delegate()
            {
                script.Connections++;
                return new ScriptedConnection(script);
            };

            ControlClient[] holder = new ControlClient[1];
            int sleeps = 0;
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                if (sleeps > 2 && holder[0] != null) holder[0].Stop();
            };

            ControlClient client = new ControlClient(options);
            holder[0] = client;
            client.Start();
            return client;
        }

        /// <summary>
        /// Avvia il client, lo ferma e lo riavvia su un canale nuovo: e' la forma
        /// minima della ripresa dopo che una lettura e' fallita. Il registro dice
        /// anche *perche'* il canale e' stato rifatto.
        /// </summary>
        private static ControlClient RunChannelSwap(IStateStore store, IControlConnection first,
                                                    IControlConnection second, out List<string> lines)
        {
            List<string> captured = new List<string>();
            ControlClientOptions options = Options(store, new FakeConnection());
            options.Log = new DelegateLogSink(captured.Add);

            int created = 0;
            options.ConnectionFactory = delegate()
            {
                created++;
                return created == 1 ? first : second;
            };

            ControlClient[] holder = new ControlClient[1];
            int sleeps = 0;
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                if (sleeps > 2 && holder[0] != null) holder[0].Stop();
            };

            ControlClient client = new ControlClient(options);
            holder[0] = client;
            client.Start();

            // Il riavvio ha di nuovo il suo margine di attese.
            sleeps = 0;
            client.Start();

            lines = captured;
            return client;
        }

        /// <summary>
        /// Avvia un client su un canale che si puo' far fallire. L'attesa finta ferma
        /// il client solo quando e' arrivato da qualche parte (autorizzato o in
        /// errore): una ritentata dorme a pezzi dentro il backoff, quindi fermarlo
        /// dopo due attese misurerebbe l'arresto invece del recupero.
        /// </summary>
        private static ControlClient RunClient(IStateStore store, FlakyConnection connection,
                                              int maxRetries, out List<string> lines)
        {
            List<string> captured = new List<string>();
            ControlClientOptions options = Options(store, new FakeConnection());
            options.Log = new DelegateLogSink(captured.Add);
            options.MaxChannelRetries = maxRetries;

            ControlClient[] holder = new ControlClient[1];
            options.ConnectionFactory = delegate()
            {
                connection.Attach(holder[0]);
                return connection;
            };
            int sleeps = 0;
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                ControlClient client = holder[0];
                if (client == null) return;

                // Si ferma quando il client e' arrivato da qualche parte (autorizzato
                // o in errore) e comunque dopo un tetto di attese: la netmap finta non
                // consegna nulla, quindi il client continua legittimamente a
                // riconnettersi e senza il tetto il test non finirebbe mai.
                if (client.Status.State != ControlState.Connecting || sleeps > 200) client.Stop();
            };

            ControlClient started = new ControlClient(options);
            holder[0] = started;
            started.Start();

            lines = captured;
            return started;
        }

        // --- attrezzi ---

        private static string AuthorizedResponse()
        {
            return "{\"MachineAuthorized\":true,\"LoginName\":\"vincenzo@example.com\"}";
        }

        private static ControlClientOptions Options(IStateStore store, FakeConnection connection)
        {
            ControlClientOptions options = new ControlClientOptions();
            options.Hostname = "test";
            options.Random = new SequenceRandomSource();
            options.State = store;
            options.Log = NullLogSink.Instance;
            options.ConnectionFactory = delegate() { return connection; };
            options.LoginPollMilliseconds = 3000;
            options.LoginTimeoutSeconds = 3;
            // Nessuna attesa vera: i test non devono dipendere dal tempo.
            options.Sleep = delegate(int milliseconds) { };
            return options;
        }

        /// <summary>
        /// Avvia un client e lo ferma dopo un paio di attese: il ciclo della netmap
        /// non deve dipendere dal tempo reale in un test.
        /// </summary>
        private static ControlClient Run(IStateStore store, FakeConnection connection,
                                        Func<string, bool> opener, out List<string> lines)
        {
            List<string> captured = new List<string>();
            ControlClientOptions options = Options(store, connection);
            options.Log = new DelegateLogSink(captured.Add);
            if (opener != null) options.BrowserOpener = opener;

            ControlClient[] holder = new ControlClient[1];
            int sleeps = 0;
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                if (sleeps > 2 && holder[0] != null) holder[0].Stop();
            };

            ControlClient client = new ControlClient(options);
            holder[0] = client;
            client.Start();

            lines = captured;
            return client;
        }

        private static string JsonString(string json, string name)
        {
            if (json == null) return null;
            return JsonBody.Parse(json)[name].AsString(null);
        }

        private static bool HasLine(List<string> lines, string fragment)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>Le risposte che il server finto deve dare, in ordine, e i contatori osservati.</summary>
        private sealed class Script
        {
            private readonly List<int> _statuses = new List<int>();
            private readonly List<string> _bodies = new List<string>();

            public int Calls;
            public int Connections;
            public string LastRegisterBody;
            public Action<string> OnRegister;

            public void Enqueue(int status, string body)
            {
                _statuses.Add(status);
                _bodies.Add(body);
            }

            public ControlResponse Next(string jsonBody)
            {
                LastRegisterBody = jsonBody;
                if (OnRegister != null) OnRegister(jsonBody);

                int index = Calls;
                Calls++;
                // Esaurito lo script si ripete l'ultima risposta: un login in attesa
                // riceve lo stesso AuthURL finche' non cambia qualcosa.
                int at = index < _bodies.Count ? index : _bodies.Count - 1;
                if (at < 0) at = 0;
                ControlResponse response = new ControlResponse();
                response.StatusCode = _statuses.Count > 0 ? _statuses[at] : 500;
                response.Body = _bodies.Count > 0 ? _bodies[at] : "{}";
                return response;
            }
        }

        private sealed class ScriptedConnection : IControlConnection
        {
            private readonly Script _script;

            public ScriptedConnection(Script script) { _script = script; }

            public ControlResponse Post(string path, string jsonBody,
                                        List<KeyValuePair<string, string>> headers,
                                        int deadlineSeconds)
            {
                return _script.Next(jsonBody);
            }

            public void PostStream(string path, string jsonBody,
                                   List<KeyValuePair<string, string>> headers,
                                   Action<string> onJson, int watchdogSeconds)
            {
            }

            public bool IsBroken { get { return false; } }

            public void Dispose() { }
        }

        /// <summary>
        /// Un canale che fallisce come il socket di un'app sospesa, poi risponde.
        /// Il testo dell'errore e' quello che il canale vero produce.
        /// </summary>
        private sealed class FlakyConnection : IControlConnection
        {
            public const string Interrupted =
                "canale di controllo interrotto durante la richiesta /machine/register"
                + ": la connessione e' caduta o il sistema ha interrotto la lettura";

            private readonly string _body;
            private ControlClient _client;

            public int FailuresRemaining;
            public int Calls;
            public bool Stopper;

            public FlakyConnection(string body, int failures)
            {
                _body = body;
                FailuresRemaining = failures;
            }

            public void Attach(ControlClient client) { _client = client; }

            public ControlResponse Post(string path, string jsonBody,
                                       List<KeyValuePair<string, string>> headers,
                                       int deadlineSeconds)
            {
                Calls++;
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    if (Stopper && _client != null) _client.Stop();
                    throw new ControlStreamException(Interrupted, new IOException(Interrupted));
                }

                ControlResponse response = new ControlResponse();
                response.StatusCode = 200;
                response.Body = _body;
                return response;
            }

            public void PostStream(string path, string jsonBody,
                                   List<KeyValuePair<string, string>> headers,
                                   Action<string> onJson, int watchdogSeconds)
            {
            }

            public bool IsBroken { get { return false; } }

            public void Dispose() { }
        }

        /// <summary>Fallisce una volta si' e una no: un canale instabile, non un canale morto.</summary>
        private sealed class AlternatingConnection : IControlConnection
        {
            private readonly string _body;

            public int Calls;
            public int Failures;

            public AlternatingConnection(string body) { _body = body; }

            public ControlResponse Post(string path, string jsonBody,
                                       List<KeyValuePair<string, string>> headers,
                                       int deadlineSeconds)
            {
                Calls++;
                if (Calls % 2 == 1)
                {
                    Failures++;
                    throw new ControlStreamException(FlakyConnection.Interrupted);
                }

                ControlResponse response = new ControlResponse();
                response.StatusCode = 200;
                response.Body = _body;
                return response;
            }

            public void PostStream(string path, string jsonBody,
                                   List<KeyValuePair<string, string>> headers,
                                   Action<string> onJson, int watchdogSeconds)
            {
            }

            public bool IsBroken { get { return false; } }

            public void Dispose() { }
        }

        private sealed class FakeConnection : IControlConnection
        {
            public string RegisterBody = "{}";
            public int RegisterCalls;
            public string LastRegisterBody;

            /// <summary>Vero quando il canale ha gia' fallito: il client non deve riusarlo.</summary>
            public bool Broken;

            public ControlResponse Post(string path, string jsonBody,
                                        List<KeyValuePair<string, string>> headers,
                                        int deadlineSeconds)
            {
                RegisterCalls++;
                LastRegisterBody = jsonBody;
                ControlResponse response = new ControlResponse();
                response.StatusCode = 200;
                response.Body = RegisterBody;
                return response;
            }

            public void PostStream(string path, string jsonBody,
                                   List<KeyValuePair<string, string>> headers,
                                   Action<string> onJson, int watchdogSeconds)
            {
            }

            public bool IsBroken { get { return Broken; } }

            public void Dispose() { }
        }
    }
}
