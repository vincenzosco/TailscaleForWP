using System;
using Tailscale.Core.Control;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Test della netmap accumulata.
    ///
    /// Il caso che questi test difendono: le risposte del server sono
    /// incrementali, quindi la differenza fra "campo assente" e "lista vuota" e'
    /// una informazione, non un dettaglio. Se si confondono, un peer rimosso
    /// resta nella lista per sempre e l'utente vede un dispositivo che non c'e'
    /// piu'.
    /// </summary>
    public static class NetworkMapTests
    {
        public static void Run()
        {
            Case("lista vuota", EmptyPeerListClearsTheMap);
            Case("campi incrementali", ChangedAndRemovedAreApplied);
            Case("campo assente", MissingPeersLeavesTheMapAlone);
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
                Console.WriteLine("FAIL netmap/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmptyPeerListClearsTheMap()
        {
            NetworkMap map = new NetworkMap();
            map.Apply(MapResponse.FromJson("{\"Peers\":[{\"ID\":1,\"Name\":\"uno.tailnet.ts.net.\"},"
                                           + "{\"ID\":2,\"Name\":\"due.tailnet.ts.net.\"}]}"));
            T.Eq(2, map.Peers.Count, "vuota: due peer in mappa");

            // Il server dice che non c'e' piu' nessun peer: la lista vuota e'
            // autorevole e va applicata.
            map.Apply(MapResponse.FromJson("{\"Seq\":2,\"Peers\":[]}"));
            T.Eq(0, map.Peers.Count, "vuota: l'ultimo peer rimosso sparisce");
        }

        private static void ChangedAndRemovedAreApplied()
        {
            NetworkMap map = new NetworkMap();
            map.Apply(MapResponse.FromJson("{\"Peers\":[{\"ID\":1,\"Hostinfo\":{\"Hostname\":\"uno\"}},"
                                           + "{\"ID\":2,\"Hostinfo\":{\"Hostname\":\"due\"}},"
                                           + "{\"ID\":3,\"Hostinfo\":{\"Hostname\":\"tre\"}}]}"));

            T.Eq(3, map.Peers.Count, "incrementale: tre peer in mappa");

            // PeersChanged aggiorna per ID senza toccare gli altri.
            map.Apply(MapResponse.FromJson("{\"PeersChanged\":[{\"ID\":2,\"Online\":true,"
                                           + "\"Hostinfo\":{\"Hostname\":\"due\"}}]}"));
            T.Eq(3, map.Peers.Count, "incrementale: il numero non cambia");
            T.True(map.FindPeer(2).Online, "incrementale: il peer 2 risulta online");

            // PeersRemoved toglie, e gli indici successivi scalano: senza la
            // ricostruzione degli indici FindPeer restituirebbe il peer sbagliato.
            map.Apply(MapResponse.FromJson("{\"PeersRemoved\":[1]}"));
            T.Eq(2, map.Peers.Count, "incrementale: il peer rimosso sparisce");
            T.True(map.FindPeer(1) == null, "incrementale: il peer 1 non c'e' piu'");
            T.Eq("due", map.FindPeer(2).Hostname, "incrementale: il peer 2 e' ancora quello giusto");
            T.Eq("tre", map.FindPeer(3).Hostname, "incrementale: il peer 3 si ritrova dopo lo scalo");
        }

        private static void MissingPeersLeavesTheMapAlone()
        {
            NetworkMap map = new NetworkMap();
            map.Apply(MapResponse.FromJson("{\"Peers\":[{\"ID\":1,\"Hostinfo\":{\"Hostname\":\"uno\"}}]}"));

            // Senza il campo `Peers` la risposta parla d'altro (seq, salute, il
            // nostro nodo): la lista non va toccata.
            map.Apply(MapResponse.FromJson("{\"Seq\":2,\"KeepAlive\":true}"));
            T.Eq(1, map.Peers.Count, "assente: la lista resta com'era");

            // E il nostro nodo autorizza, senza confondere se' stesso con un peer.
            T.False(map.IsAuthorized, "assente: non autorizzato finche' non arriva il nodo");
            map.Apply(MapResponse.FromJson("{\"Node\":{\"ID\":9,\"Hostinfo\":{\"Hostname\":\"telefono\"}}}"));
            T.True(map.IsAuthorized, "assente: il nodo presente autorizza");
            T.Eq(1, map.Peers.Count, "assente: il nodo non finisce fra i peer");
        }
    }
}
