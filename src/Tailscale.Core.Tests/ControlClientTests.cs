using System;
using System.Collections.Generic;
using System.Text;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Test del client di controllo con una connessione finta: quello che si
    /// verifica non e' il filo ma l'ordine delle cose visibili a chi ascolta.
    ///
    /// Il caso qui e' quello osservato davvero nella prima registrazione
    /// riuscita: il comando stampava "(il browser non si e' aperto...)" e subito
    /// dopo il log diceva "apertura del browser riuscita". Chi legge lo stato una
    /// volta sola deve trovarlo gia' definitivo.
    /// </summary>
    public static class ControlClientTests
    {
        public static void Run()
        {
            BrowserOutcomeIsKnownWhenNeedLoginIsPublished();
        }

        private static void BrowserOutcomeIsKnownWhenNeedLoginIsPublished()
        {
            FakeConnection connection = new FakeConnection();
            connection.RegisterBody =
                "{\"AuthURL\":\"https://login.tailscale.com/a/xyz\",\"MachineAuthorized\":false}";

            List<string> opened = new List<string>();
            List<string> seen = new List<string>();
            ControlClient[] holder = new ControlClient[1];
            int sleeps = 0;

            IRandomSource random = new SequenceRandomSource();
            ControlClientOptions options = new ControlClientOptions();
            options.Hostname = "test";
            options.Random = random;
            options.MachineKeyPair = KeyPair.Generate(random);
            options.Log = new DelegateLogSink(seen.Add);
            options.ConnectionFactory = delegate() { return connection; };
            options.LoginPollMilliseconds = 3000;
            options.LoginTimeoutSeconds = 3;
            // Nessuna attesa vera, e dopo qualche giro si ferma: il test non deve
            // dipendere dal tempo.
            options.Sleep = delegate(int milliseconds)
            {
                sleeps++;
                if (sleeps > 3 && holder[0] != null) holder[0].Stop();
            };
            options.BrowserOpener = delegate(string url)
            {
                opened.Add(url);
                return true;   // il browser si apre
            };

            ControlClient client = new ControlClient(options);
            holder[0] = client;

            string urlAtNeedLogin = null;
            ControlState stateAtNeedLogin = ControlState.Stopped;
            client.StatusChanged += delegate()
            {
                ControlStatus status = client.Status;
                if (status.State == ControlState.NeedLogin && urlAtNeedLogin == null)
                {
                    urlAtNeedLogin = status.LoginUrl;
                    stateAtNeedLogin = ControlState.NeedLogin;
                    seen.Add("needLogin: browserOpened=" + status.BrowserOpened);
                }
            };

            client.Start();

            T.Eq("https://login.tailscale.com/a/xyz", urlAtNeedLogin, "login: l'URL arriva a chi ascolta");
            T.Eq(ControlState.NeedLogin.ToString(), stateAtNeedLogin.ToString(), "login: lo stato e' NeedLogin");
            T.Eq(1, opened.Count, "login: il browser viene aperto una volta sola");
            T.Eq("https://login.tailscale.com/a/xyz", opened[0], "login: si apre l'URL del server");

            // Questa e' la riga che il difetto rendeva FALSA: al momento della
            // notifica l'esito dell'apertura deve essere gia' noto.
            T.True(HasLine(seen, "needLogin: browserOpened=True"),
                   "login: l'esito del browser e' noto quando lo stato viene pubblicato");
        }

        private static bool HasLine(List<string> lines, string fragment)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Connessione finta: risponde alla registrazione con l'URL di login, senza
        /// rete. La netmap non serve a questo test.
        /// </summary>
        private sealed class FakeConnection : IControlConnection
        {
            public string RegisterBody = "{}";

            public ControlResponse Post(string path, string jsonBody, List<KeyValuePair<string, string>> headers)
            {
                ControlResponse response = new ControlResponse();
                response.StatusCode = 200;
                response.Body = RegisterBody;
                return response;
            }

            public void PostStream(string path, string jsonBody, List<KeyValuePair<string, string>> headers,
                                   Action<string> onJson, int watchdogSeconds)
            {
            }

            public bool IsBroken { get { return false; } }

            public void Dispose() { }
        }
    }
}
