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

    for path in files():
        try:
            with open(path, "r", encoding="utf-8-sig", newline="") as handle:
                text = handle.read()
        except (IOError, UnicodeDecodeError):
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
                with open(path, "w", encoding="utf-8", newline="") as handle:
                    handle.write(text)
                fixed += 1

    if fix:
        print("emoji controllate: %d segnalazioni, %d file modificati" % (problems, fixed))
        return 0

    if problems:
        print("FAIL: %d emoji o frecce decorative (usa --fix)" % problems)
        return 1

    print("nessuna emoji ne' freccia decorativa")
    return 0


if __name__ == "__main__":
    sys.exit(main())
