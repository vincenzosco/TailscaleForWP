using System;
using System.IO;
using Tailscale.Core;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Lo store su file della CLI. Due cose contano: che i valori tornino identici
    /// (compreso l'URL con caratteri speciali) e che un file corrotto non impedisca
    /// di partire — uno state illeggibile deve costare un login, non un'app morta.
    /// </summary>
    public static class FileStateStoreTests
    {
        public static void Run()
        {
            string path = Path.Combine(Path.GetTempPath(), "tailscale-state-test-" + Guid.NewGuid().ToString("n") + ".json");
            try
            {
                RoundTrip(path);
                CorruptFileIsTolerated(path);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void RoundTrip(string path)
        {
            FileStateStore store = new FileStateStore(path);
            T.Eq(null, store.Read("nodeKey"), "file assente: si parte vuoti");

            const string url = "https://login.tailscale.com/a/x?y=\"1\"&z=2";
            store.Write("nodeKey", "nodekey:abcd");
            store.Write("authUrl", url);

            T.Eq("nodekey:abcd", store.Read("nodeKey"), "il valore scritto si rilegge");
            T.True(File.Exists(path), "il file viene creato");

            // Una seconda istanza (cioe' un riavvio) vede gli stessi valori.
            FileStateStore reopened = new FileStateStore(path);
            T.Eq("nodekey:abcd", reopened.Read("nodeKey"), "un riavvio rilegge la node key");
            T.Eq(url, reopened.Read("authUrl"), "anche un URL con virgolette e caratteri speciali");

            reopened.Delete("authUrl");
            T.Eq(null, new FileStateStore(path).Read("authUrl"), "cancellare toglie davvero il valore");
            T.Eq("nodekey:abcd", new FileStateStore(path).Read("nodeKey"), "e non tocca gli altri");
        }

        private static void CorruptFileIsTolerated(string path)
        {
            File.WriteAllText(path, "{ questo non e' JSON");
            FileStateStore store = new FileStateStore(path);
            T.Eq(null, store.Read("nodeKey"), "un file corrotto non lancia: si riparte da vuoto");

            store.Write("nodeKey", "nodekey:beef");
            T.Eq("nodekey:beef", new FileStateStore(path).Read("nodeKey"), "e si puo' riscrivere sopra");
        }
    }
}
