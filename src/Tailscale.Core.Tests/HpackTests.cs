using System;
using System.Collections.Generic;
using Tailscale.Core;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Le due tabelle di HPACK sono dati fissati da RFC 7541 e sono generate da
    /// tools/gen-hpack-tables.py. Questi test non verificano che "il file compila":
    /// verificano che il *contenuto* sia quello del RFC, cioe' le voci che il
    /// server usera' per rispondere. Un indice sbagliato qui non si vede finche'
    /// il server non risponde con un header che non ci aspettiamo.
    ///
    /// I valori attesi sono letti dall'appendice del RFC (App. A per la tabella
    /// statica, App. B per i codici di Huffman) e riconfermati dal generatore.
    /// </summary>
    public static class HpackTableTests
    {
        public static void Run()
        {
            T.Eq(61, HpackTables.StaticTableLength, "tabella statica: 61 voci");
            T.Eq(62, HpackTables.StaticNames.Length, "indice 0 non usato");
            T.Eq(62, HpackTables.StaticValues.Length, "indice 0 non usato");

            // Voci usate davvero dal canale di controllo.
            T.Eq(":authority", HpackTables.StaticNames[1], "indice 1 = :authority");
            T.Eq(":method", HpackTables.StaticNames[2], "indice 2 = :method");
            T.Eq("GET", HpackTables.StaticValues[2], "indice 2 = :method GET");
            T.Eq("POST", HpackTables.StaticValues[3], "indice 3 = :method POST");
            T.Eq(":path", HpackTables.StaticNames[4], "indice 4 = :path");
            T.Eq("/", HpackTables.StaticValues[4], "indice 4 = :path /");
            T.Eq(":status", HpackTables.StaticNames[8], "indice 8 = :status");
            T.Eq("200", HpackTables.StaticValues[8], "indice 8 = :status 200");
            T.Eq("content-type", HpackTables.StaticNames[31], "indice 31 = content-type");
            T.Eq("user-agent", HpackTables.StaticNames[58], "indice 58 = user-agent");

            // Huffman: 256 simboli piu' il simbolo di fine.
            T.Eq(257, HpackTables.HuffmanCodes.Length, "Huffman: 256 simboli + EOS");
            T.Eq(257, HpackTables.HuffmanLengths.Length, "Huffman: una lunghezza per simbolo");
            T.Eq(0x1ff8, HpackTables.HuffmanCodes[0], "Huffman di 0x00 = 0x1ff8 (RFC App. B)");
            T.Eq(13, HpackTables.HuffmanLengths[0], "Huffman di 0x00 = 13 bit");
            // '0' = 0x30 -> 00000 (5 bit), 'a' = 0x61 -> 00011 (5 bit), 0xff -> 30 bit.
            T.Eq(0x0, HpackTables.HuffmanCodes[0x30], "Huffman di '0' = 0x0");
            T.Eq(5, HpackTables.HuffmanLengths[0x30], "Huffman di '0' = 5 bit");
            T.Eq(0x3, HpackTables.HuffmanCodes[0x61], "Huffman di 'a' = 0x3");
            T.Eq(5, HpackTables.HuffmanLengths[0x61], "Huffman di 'a' = 5 bit");
            // Gli ultimi simboli della tabella: 0xff e' il valore piu' lungo fra i
            // byte (26 bit) e l'EOS e' l'unico codice di 30 bit, tutti a uno.
            T.Eq(0x3ffffee, HpackTables.HuffmanCodes[0xff], "Huffman di 0xff = 0x3ffffee");
            T.Eq(26, HpackTables.HuffmanLengths[0xff], "Huffman di 0xff = 26 bit");
            T.Eq(0x3fffffff, HpackTables.HuffmanCodes[256], "Huffman EOS = tutti uno");
            T.Eq(30, HpackTables.HuffmanLengths[256], "Huffman EOS = 30 bit");

            // I codici sono liberi da prefissi: ogni codice piu' corto non puo'
            // essere il prefisso di uno piu' lungo, altrimenti la decodifica
            // sarebbe ambigua e non si potrebbe decidere simbolo per simbolo.
            int collisions = 0;
            for (int i = 0; i < 257; i++)
            {
                for (int j = 0; j < 257; j++)
                {
                    if (i == j) continue;
                    int shortLen = HpackTables.HuffmanLengths[i];
                    int longLen = HpackTables.HuffmanLengths[j];
                    if (shortLen > longLen) continue;
                    int shifted = HpackTables.HuffmanCodes[j] >> (longLen - shortLen);
                    if (shifted == HpackTables.HuffmanCodes[i]) collisions++;
                }
            }
            T.Eq(0, collisions, "codici di Huffman liberi da prefissi");
        }
    }

    /// <summary>
    /// Il decoder viene provato contro blocchi prodotti da `hpack` (Python, la
    /// libreria di hyper/httpx), non contro il nostro encoder: un round-trip
    /// interno passerebbe anche con un Huffman sbagliato in modo simmetrico.
    ///
    /// I fixture si rigenerano con tools/gen-http2-fixtures.py.
    /// </summary>
    public static class HpackDecoderTests
    {
        // hpack.encode([(":method","POST"),(":scheme","https"),
        // (":authority","controlplane.tailscale.com"),(":path","/machine/register"),
        // ("content-type","application/json"),("user-agent","tailscale")], huffman=True)
        private const string RequestBlockHex =
            "8387419221ea4d87a2ba07515748cd42083a0ab90f4f448c629192735456"
            + "2c2cc6424b675f8b1d75d0620d263d4c7441ea7a8648cd42083a0b";

        // hpack.encode([(":status","200"),("content-type","application/json")])
        private const string ResponseBlockHex = "885f8b1d75d0620d263d4c7441ea";

        // hpack.encode([(":method","GET"),(":path","/key?v=148")])
        private const string PlainBlockHex = "82448963a97d7f9de00b4f7f";

        // hpack.encode([("x-tailscale-hostinfo", "linux; tailscale 1.104.1; " + "a"*300)]):
        // un valore lungo, che esercita il riempimento finale di Huffman.
        private const string LongValueBlockHex =
            "408ef2b2466a1041d055a73a126aa53fff50a0d55be7ed448cd42083a0aa"
            + "057081a5c3f6a0c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c6318c"
            + "6318c6318c6318c6318c6318c631ff";

        public static void Run()
        {
            DecodesRequestBlock();
            DecodesResponseBlock();
            DecodesPlainBlock();
            DecodesLongHuffmanValue();
            RejectsOutOfTableIndex();
            RejectsIndexZero();
            RejectsBadHuffmanPadding();
        }

        private static void DecodesRequestBlock()
        {
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            List<HpackHeader> headers = decoder.Decode(T.FromHex(RequestBlockHex));

            T.Eq(6, headers.Count, "richiesta: sei header");
            T.Eq(":method", headers[0].Name, "richiesta: header 0 nome");
            T.Eq("POST", headers[0].Value, "richiesta: header 0 valore");
            T.Eq(":scheme", headers[1].Name, "richiesta: header 1 nome");
            T.Eq("https", headers[1].Value, "richiesta: header 1 valore");
            T.Eq(":authority", headers[2].Name, "richiesta: header 2 nome");
            T.Eq("controlplane.tailscale.com", headers[2].Value, "richiesta: header 2 valore (Huffman)");
            T.Eq(":path", headers[3].Name, "richiesta: header 3 nome");
            T.Eq("/machine/register", headers[3].Value, "richiesta: header 3 valore (Huffman)");
            T.Eq("content-type", headers[4].Name, "richiesta: header 4 nome");
            T.Eq("application/json", headers[4].Value, "richiesta: header 4 valore");
            T.Eq("user-agent", headers[5].Name, "richiesta: header 5 nome");
            T.Eq("tailscale", headers[5].Value, "richiesta: header 5 valore");

            // Il blocco entra nella tabella dinamica (i campi literal sono con
            // indicizzazione incrementale): i campi indicizzati dal server dopo
            // questo blocco si riferiscono a quelle voci.
            T.True(decoder.DynamicTableCount > 0, "richiesta: la tabella dinamica si popola");

            // Decodificare di nuovo lo stesso blocco da' la stessa risposta: gli
            // indici usati qui sono tutti statici, quindi la tabella cresciuta non
            // cambia il risultato.
            List<HpackHeader> again = decoder.Decode(T.FromHex(RequestBlockHex));
            T.Eq(6, again.Count, "richiesta: decodifica ripetuta, stesso numero di header");
            T.Eq("POST", again[0].Value, "richiesta: decodifica ripetuta, stessi valori");
            T.Eq("controlplane.tailscale.com", again[2].Value, "richiesta: decodifica ripetuta, Huffman");
        }

        private static void DecodesResponseBlock()
        {
            // Il sink cattura le righe: cosi' il test verifica anche che il
            // decoder si faccia sentire nei log (un decoder muto rende invisibile
            // un blocco corrotto) ed e' la prova che questo gruppo di test gira.
            List<string> lines = new List<string>();
            HpackDecoder decoder = new HpackDecoder(new DelegateLogSink(lines.Add));
            List<HpackHeader> headers = decoder.Decode(T.FromHex(ResponseBlockHex));

            T.Eq(1, lines.Count, "risposta: il decoder logga il blocco letto");
            // Una voce in tabella da 60 byte = "content-type" (12) +
            // "application/json" (16) + 32 di overhead (RFC 7541 §4.1): il conto
            // della tabella dinamica torna.
            T.Eq("hpack: 14 byte -> 2 header (tabella dinamica 1 voci, 60 byte)", lines[0],
                 "risposta: la riga di log dice dimensione e risultato");
            T.Eq(2, headers.Count, "risposta: due header");
            T.Eq(":status", headers[0].Name, "risposta: header 0 nome");
            T.Eq("200", headers[0].Value, "risposta: header 0 valore (indicizzato 8)");
            T.Eq("content-type", headers[1].Name, "risposta: header 1 nome");
            T.Eq("application/json", headers[1].Value, "risposta: header 1 valore (Huffman)");
        }

        private static void DecodesPlainBlock()
        {
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            List<HpackHeader> headers = decoder.Decode(T.FromHex(PlainBlockHex));

            T.Eq(2, headers.Count, "key: due header");
            T.Eq("GET", headers[0].Value, "key: metodo");
            T.Eq(":path", headers[1].Name, "key: header 1 nome");
            T.Eq("/key?v=148", headers[1].Value, "key: percorso con query");
        }

        private static void DecodesLongHuffmanValue()
        {
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            List<HpackHeader> headers = decoder.Decode(T.FromHex(LongValueBlockHex));

            T.Eq(1, headers.Count, "valore lungo: un header");
            T.Eq("x-tailscale-hostinfo", headers[0].Name, "valore lungo: nome in Huffman");
            T.Eq("linux; tailscale 1.104.1; ", headers[0].Value.Substring(0, 26), "valore lungo: prefisso");
            T.Eq(326, headers[0].Value.Length, "valore lungo: 26 + 300 caratteri");
            T.Eq(new string('a', 300), headers[0].Value.Substring(26), "valore lungo: coda di 300 'a'");
        }

        private static void RejectsOutOfTableIndex()
        {
            // 0xBE = campo indicizzato con indice 62, cioe' la prima voce della
            // tabella dinamica, che a questo punto e' vuota. Un decoder che
            // restituisse un header vuoto qui leggerebbe spazzatura come se fosse
            // un header valido.
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            T.Throws(delegate { decoder.Decode(T.FromHex("be")); }, "indice 62 con tabella dinamica vuota");
        }

        private static void RejectsIndexZero()
        {
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            T.Throws(delegate { decoder.Decode(T.FromHex("80")); }, "indice 0");
        }

        private static void RejectsBadHuffmanPadding()
        {
            // 0x00 = literal senza indicizzazione, nome come stringa; 0x81 =
            // stringa Huffman di 1 byte; 0x00 = cinque zeri ('0') piu' tre bit di
            // riempimento che non sono tutti a uno.
            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            T.Throws(delegate { decoder.Decode(T.FromHex("00810000")); }, "riempimento di Huffman non valido");
        }
    }
}
