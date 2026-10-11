#!/usr/bin/env python
"""Genera src/Tailscale.Core/Protocol/HpackTables.cs.

Le due tabelle HPACK (tabella statica e codice di Huffman) sono dati fissati
da RFC 7541: copiarle a mano significa sbagliare almeno un simbolo, e un
simbolo sbagliato si vede solo quando il server risponde. Qui si prendono da
`hpack` (la libreria Python usata da httpx/hyper), che a sua volta le genera
dalle stesse costanti dell'appendice del RFC.

Uso:
    python tools/gen-hpack-tables.py            # scrive il file
    python tools/gen-hpack-tables.py --check    # fallisce se il file differisce

Richiede: pip install hpack
"""
import sys
import os
import io

try:
    from hpack.table import HeaderTable
    from hpack.huffman_constants import REQUEST_CODES, REQUEST_CODES_LENGTH
except ImportError:
    sys.exit("serve hpack: pip install hpack")

OUT = os.path.join("src", "Tailscale.Core", "Protocol", "HpackTables.cs")


def as_text(value):
    """hpack espone le voci come bytes; qui servono stringhe."""
    if isinstance(value, bytes):
        return value.decode("ascii")
    return value


def csharp_string(value):
    """Stringa C# con soli caratteri ASCII stampabili (le voci sono tali)."""
    text = as_text(value)
    out = []
    for ch in text:
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif 0x20 <= ord(ch) <= 0x7E:
            out.append(ch)
        else:
            out.append("\\u%04x" % ord(ch))
    return '"' + "".join(out) + '"'


def generate():
    lines = []
    add = lines.append

    add("namespace Tailscale.Core.Protocol")
    add("{")
    add("    /// <summary>")
    add("    /// Tabelle di HPACK (RFC 7541) usate dal layer HTTP/2 del canale di controllo.")
    add("    ///")
    add("    /// File GENERATO da tools/gen-hpack-tables.py: non modificare a mano.")
    add("    /// Le voci sono in ordine di indice, con l'indice 0 non usato (HPACK")
    add("    /// numera la tabella statica da 1), cosi' l'indice del RFC si usa diretto.")
    add("    /// </summary>")
    add("    public static class HpackTables")
    add("    {")
    add("        /// <summary>Numero di voci della tabella statica (indici 1..61).</summary>")
    add("        public const int StaticTableLength = %d;" % len(HeaderTable.STATIC_TABLE))
    add("")
    add("        /// <summary>Nome della voce i-esima; l'indice 0 e' null.</summary>")
    add("        public static readonly string[] StaticNames = new string[]")
    add("        {")
    add("            null,")
    for name, value in HeaderTable.STATIC_TABLE:
        add("            %s," % csharp_string(name))
    add("        };")
    add("")
    add("        /// <summary>Valore della voce i-esima; l'indice 0 e' null.</summary>")
    add("        public static readonly string[] StaticValues = new string[]")
    add("        {")
    add("            null,")
    for name, value in HeaderTable.STATIC_TABLE:
        add("            %s," % csharp_string(value))
    add("        };")
    add("")
    add("        /// <summary>")
    add("        /// Codici di Huffman, uno per simbolo (0..255) piu' il simbolo di fine")
    add("        /// (256). Il codice del simbolo i e' HuffmanCodes[i] e e' lungo")
    add("        /// HuffmanLengths[i] bit, a partire dal bit piu' significativo.")
    add("        /// </summary>")
    add("        public static readonly int[] HuffmanCodes = new int[]")
    add("        {")
    for i in range(0, len(REQUEST_CODES), 8):
        add("            " + " ".join("%d," % c for c in REQUEST_CODES[i:i + 8]))
    add("        };")
    add("")
    add("        /// <summary>Lunghezza in bit del codice del simbolo i-esimo.</summary>")
    add("        public static readonly byte[] HuffmanLengths = new byte[]")
    add("        {")
    for i in range(0, len(REQUEST_CODES_LENGTH), 16):
        add("            " + " ".join("%d," % c for c in REQUEST_CODES_LENGTH[i:i + 16]))
    add("        };")
    add("    }")
    add("}")
    add("")
    return "\n".join(lines)


def main():
    text = generate()
    check = "--check" in sys.argv[1:]

    if check:
        if not os.path.exists(OUT):
            sys.exit("%s non esiste" % OUT)
        with io.open(OUT, "r", encoding="utf-8-sig", newline="") as handle:
            # Il file e' scritto in CRLF (convenzione del repo): il confronto
            # ignora la differenza di fine riga, altrimenti --check fallirebbe
            # sempre su un file appena generato.
            current = handle.read().replace("\r\n", "\n")
        if current != text:
            sys.exit("%s non e' aggiornato: riesegui tools/gen-hpack-tables.py" % OUT)
        print("%s aggiornato" % OUT)
        return

    with io.open(OUT, "w", encoding="utf-8-sig", newline="\r\n") as handle:
        handle.write(text)
    print("scritto %s (%d righe)" % (OUT, text.count("\n")))


if __name__ == "__main__":
    main()
