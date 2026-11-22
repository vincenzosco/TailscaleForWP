using System;
using System.Collections.Generic;
using Tailscale.Core.Control;
using Tailscale.Core.Tailcfg;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Lo scrittore JSON delle richieste di controllo. Un errore qui non si vede
    /// compilando: produce una richiesta che il server rifiuta con un 400 che non
    /// spiega nulla, oppure che il parser del server legge in modo diverso da come
    /// l'abbiamo scritta.
    /// </summary>
    public static class JsonWriteTests
    {
        public static void Run()
        {
            ArrayElementsAreSeparated();
            ContainersInArrayAreSeparated();
            EmptyArrayIsStillAnArray();
        }

        /// <summary>
        /// Due elementi scritti di fila in un array devono avere la virgola fra loro:
        /// senza, ["a""b"] non e' JSON, e nessuno se ne accorge finche' il server non
        /// rifiuta la richiesta. Il caso reale e' MapRequest.Endpoints con piu' di un
        /// indirizzo.
        /// </summary>
        private static void ArrayElementsAreSeparated()
        {
            MapRequest request = new MapRequest();
            request.NodeKey = "nodekey:aa";
            request.DiscoKey = "discokey:bb";
            request.Endpoints.Add("1.2.3.4:41641");
            request.Endpoints.Add("5.6.7.8:41641");

            string json = request.ToJson();

            T.True(json.IndexOf("\"1.2.3.4:41641\",\"5.6.7.8:41641\"", StringComparison.Ordinal) >= 0,
                   "array: gli elementi sono separati da una virgola");

            // E il risultato deve restare JSON valido per il nostro stesso parser.
            JsonValue parsed = JsonBody.Parse(json);
            JsonValue endpoints = parsed["Endpoints"];
            T.True(endpoints != null && endpoints.IsArray, "array: il campo Endpoints e' un array");
            T.Eq(2, endpoints.Count, "array: due endpoint leggibili");
        }

        /// <summary>
        /// Un contenitore dentro un array deve prendere la virgola come uno scalare:
        /// senza, [{...}{...}] non e' JSON. Il caso reale e' un array di oggetti
        /// scritto con BeginObject dopo il primo elemento.
        /// </summary>
        private static void ContainersInArrayAreSeparated()
        {
            JsonWriter writer = new JsonWriter();
            writer.BeginArray();
            writer.BeginObject();
            writer.String("a", "1");
            writer.EndObject();
            writer.BeginObject();
            writer.String("b", "2");
            writer.EndObject();
            writer.EndArray();

            T.Eq("[{\"a\":\"1\"},{\"b\":\"2\"}]", writer.ToString(),
                 "array di oggetti: la virgola sta fra un oggetto e l'altro");

            // E il risultato deve restare JSON valido per il nostro stesso parser.
            JsonValue parsed = JsonBody.Parse(writer.ToString());
            T.True(parsed != null && parsed.IsArray, "array di oggetti: il risultato e' un array");
            T.Eq(2, parsed.Count, "array di oggetti: due elementi leggibili");
        }

        /// <summary>Un elenco vuoto resta un array vuoto: la virgola non compare.</summary>
        private static void EmptyArrayIsStillAnArray()
        {
            MapRequest request = new MapRequest();
            request.NodeKey = "nodekey:aa";
            request.DiscoKey = "discokey:bb";

            JsonValue parsed = JsonBody.Parse(request.ToJson());
            JsonValue endpoints = parsed["Endpoints"];
            T.True(endpoints != null && endpoints.IsArray, "array vuoto: il campo c'e' ed e' un array");
            T.Eq(0, endpoints.Count, "array vuoto: nessun elemento");
        }
    }
}
