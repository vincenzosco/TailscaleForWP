#!/usr/bin/env python
"""Rifiuta emoji e frecce decorative in sorgenti e documentazione.

Perche' un controllo e non una raccomandazione: un'emoji in un messaggio di
errore o in una riga di log passa inosservata in revisione, e su un telefono
8.1 il font di sistema puo' non averla — al suo posto si vede un quadratino.
Le lettere accentate italiane non sono toccate: sono lingua, non decorazione.

Uso:
    python tools/check-no-emoji.py           # controlla (exit 1 se trova qualcosa)
    python tools/check-no-emoji.py --fix     # sostituisce le frecce e cancella le emoji
"""
import codecs
import os
import re
import sys

# Emoji e simboli decorativi.
EMOJI = re.compile(
    "[\U0001F000-\U0001FAFF"      # faccine, simboli, trasporti...
    "\u2600-\u27BF"               # simboli vari e dingbat
    "\u2B00-\u2BFF"               # frecce e stelle decorative
    "\uFE0F\u200D"                # selettore di variante e zero-width joiner
    "\U0001F1E6-\U0001F1FF]"      # bandiere
)

# Frecce tipografiche: si sostituiscono con l'equivalente ASCII.
ARROWS = [
    ("\u2194", "<->"),
    ("\u2192", "->"),
    ("\u2190", "<-"),
    ("\u21d2", "=>"),
    ("\u21d0", "<="),
]

EXTENSIONS = (".cs", ".py", ".md", ".xaml", ".resw", ".cmd", ".xml", ".json",
              ".csproj", ".txt", ".vb")
SKIP_DIRS = ("bin", "obj", "AppPackages", "BundleArtifacts", ".git", "tessdata",
             "refs", "node_modules", ".superpowers", "__pycache__")
ROOTS = ("src", "Tailscale", "tools", "docs")


def read_text(path):
    """(testo, codec, bom) del file, o (None, None, None) se non decodifica.

    Codifiche ammesse: utf-8 con o senza BOM, utf-16 LE o BE. Il BOM del
    file si conserva per la riscrittura, cosi' --fix non lo strappa. Un file
    che non decodifica non si salta in silenzio: e' proprio quello che la
    porta non puo' controllare.
    """
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except IOError:
        return None, None, None
    for bom, codec in ((codecs.BOM_UTF8, "utf-8"),
                       (codecs.BOM_UTF16_LE, "utf-16-le"),
                       (codecs.BOM_UTF16_BE, "utf-16-be")):
        if raw.startswith(bom):
            try:
                return raw[len(bom):].decode(codec), codec, bom
            except UnicodeDecodeError:
                return None, None, None
    try:
        return raw.decode("utf-8"), "utf-8", b""
    except UnicodeDecodeError:
        pass
    # utf-16 senza BOM: lo si riconosce dai byte NUL che ogni carattere
    # ASCII ci lascia. Senza NUL il file non e' utf-16 leggibile e la porta
    # non puo' controllarlo.
    codec = "utf-16-le" if sys.byteorder == "little" else "utf-16-be"
    if b"\x00" in raw:
        try:
            return raw.decode(codec), codec, b""
        except UnicodeDecodeError:
            pass
    return None, None, None


def files():
    """I file da controllare: le cartelle di lavoro e quelli in radice.

    I file in radice (README, soluzione) non stanno in ROOTS, ma fanno parte del
    progetto quanto il resto: senza questa riga una emoji messa li' passerebbe.
    """
    for name in sorted(os.listdir(".")):
        if os.path.isfile(name) and name.lower().endswith(EXTENSIONS):
            yield name

    for root in ROOTS:
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            if any(part in SKIP_DIRS for part in dirpath.split(os.sep)):
                continue
            for name in filenames:
                if name.lower().endswith(EXTENSIONS):
                    yield os.path.join(dirpath, name)


def main():
    fix = "--fix" in sys.argv[1:]
    problems = 0
    fixed = 0
    unreadable = 0

    for path in files():
        text, codec, bom = read_text(path)
        if text is None:
            unreadable += 1
            print("%s: codifica non riconosciuta: la porta non lo controlla" % path)
            continue

        original = text
        for arrow, ascii_form in ARROWS:
            if arrow in text:
                problems += text.count(arrow)
                text = text.replace(arrow, ascii_form)

        for match in EMOJI.finditer(text):
            problems += 1
            print("%s: emoji o simbolo decorativo U+%04X" % (path, ord(match.group(0))))

        if fix:
            text = EMOJI.sub("", text)
            if text != original:
                with open(path, "wb") as handle:
                    handle.write(bom + text.encode(codec))
                fixed += 1

    if fix:
        print("emoji controllate: %d segnalazioni, %d file modificati, %d file non controllabili"
              % (problems, fixed, unreadable))
        return 1 if unreadable else 0

    if problems or unreadable:
        print("FAIL: %d emoji o frecce decorative, %d file non controllabili (usa --fix)"
              % (problems, unreadable))
        return 1

    print("nessuna emoji ne' freccia decorativa")
    return 0


if __name__ == "__main__":
    sys.exit(main())
