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
                LeavesNoTempResidue(path);
                WriteFailureIsSurfaced(path);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
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

        private static void LeavesNoTempResidue(string path)
        {
            // Il salvataggio scrive un file a parte e lo sostituisce in un colpo
            // solo: dopo ogni scrittura non deve restare nulla in giro.
            FileStateStore store = new FileStateStore(path);
            store.Write("nodeKey", "nodekey:1");
            store.Write("nodeKey", "nodekey:2");
            T.False(File.Exists(path + ".tmp"), "salvataggio: nessun temporaneo lasciato in giro");
            T.Eq("nodekey:2", new FileStateStore(path).Read("nodeKey"), "salvataggio: il file contiene l'ultimo valore");
        }

        private static void WriteFailureIsSurfaced(string path)
        {
            // Il file vivo e' chiuso con un lock esclusivo: la sostituzione non
            // puo' riuscire. L'errore deve arrivare al chiamante invece di essere
            // solo loggato, e il file vivo deve restare quello vecchio: meglio
            // uno stato non aggiornato che uno stato perso.
            FileStateStore store = new FileStateStore(path);
            store.Write("nodeKey", "nodekey:vecchio");

            bool surfaced = false;
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    store.Write("nodeKey", "nodekey:nuovo");
                }
                catch (IOException)
                {
                    surfaced = true;
                }
            }

            T.True(surfaced, "salvataggio: il guasto di scrittura arriva al chiamante");
            T.Eq("nodekey:vecchio", new FileStateStore(path).Read("nodeKey"),
                 "salvataggio: il file vivo resta quello vecchio");
        }
    }
}
