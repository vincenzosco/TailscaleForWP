using System;
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
}
