using System;
using System.Collections.Generic;
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
            Case("login senza browser", NeedLoginSurvivesWithoutBrowser);
            Case("corpo UTF-8", BodyKeepsAccents);
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

            T.Eq(nodeKeyOne, JsonString(second.LastRegisterBody, "NodeKey"),
                 "riavvio: la stessa node key, non una nuova");
            T.Eq(machineKeyOne, store.Read("machineKey"),
                 "riavvio: la chiave macchina non e' stata rigenerata");
            T.True(secondLog.Count > 0, "riavvio: il client ha girato e loggato");
            T.True(one.Status.State != ControlState.Error, "primo avvio: nessun errore");
            T.True(two.Status.State != ControlState.NeedLogin,
                   "riavvio: non chiede di accedere (era gia' autorizzato)");
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
            hostinfo.Hostname = "Pc-Di-Vincènzo-☕";
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
            T.Eq("Pc-Di-Vincènzo-☕", parsed["Hostinfo"]["Hostname"].AsString(""),
                 "l'hostname arriva intero, senza '?'");
            T.False(Text.FromUtf8(body).Contains("?"), "nessun carattere sostituito");

            // E la conversione usata dal canale di controllo non degrada nulla di
            // suo: testo accentato -> byte UTF-8 -> stesso testo.
            const string accented = "caffè ☕ àèìòù";
            byte[] utf8 = Text.Utf8Bytes(accented);
            T.True(utf8.Length > accented.Length, "UTF-8 usa piu' byte dei caratteri");
            T.Eq(accented, Text.FromUtf8(utf8), "e si rilegge identico");
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

        private sealed class FakeConnection : IControlConnection
        {
            public string RegisterBody = "{}";
            public int RegisterCalls;
            public string LastRegisterBody;

            public ControlResponse Post(string path, string jsonBody,
                                        List<KeyValuePair<string, string>> headers)
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

            public bool IsBroken { get { return false; } }

            public void Dispose() { }
        }
    }
}
