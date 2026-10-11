#!/usr/bin/env python
"""Legge l'anteprima della UI con pytesseract e verifica che i testi ci siano.

Perche' un OCR e non un confronto fra stringhe: confrontare il XAML con il .resw
non direbbe se un testo e' illeggibile o tagliato. L'OCR guarda i pixel e dice se
una persona la stringa la leggerebbe. Il caso che questo controllo esiste per
prendere e' la frase italiana del suggerimento, che e' piu' lunga dell'inglese:
se il controllo la taglia, qui si vede.

Il motore Tesseract non e' nel PATH su questa macchina: il percorso si imposta
qui, e i dati della lingua italiana stanno in `tools/tessdata` (scaricati dal
repository ufficiale), cosi' il controllo non dipende da un'installazione
completa.

Uso: python tools/ocr-check-ui.py [--preview .superpowers/ui-preview]
"""
import argparse
import os
import re
import sys
import unicodedata

import pytesseract
from PIL import Image

TESSERACT = r"C:\Program Files\Tesseract-OCR\tesseract.exe"
TESSDATA = os.path.join("tools", "tessdata")
RESW = os.path.join("Tailscale", "Resources", "Strings", "%s", "Resources.resw")

# Tesseract non conosce i nomi di locale: usa codici ISO 639-2 a tre lettere.
# Qui accanto stanno le estensioni dei file di dati in tools/tessdata.
LANGUAGES = {"en-US": "eng", "it-IT": "ita"}


def read(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def resource(path, key):
    """La stringa di una chiave del .resw.

    Il codice XAML cerca le risorse degli elementi con la forma "Elemento.Proprieta"
    (che nelle x:Uid diventa "Elemento/Proprieta"); nei .resw la chiave e' scritta
    con il punto. Qui si accettano entrambe, cosi' le attese si leggono come le
    trova il codice.
    """
    text = read(path)
    for candidate in (key, key.replace("/", "."), key.replace(".", "/")):
        match = re.search(r'<data name="%s"[^>]*>\s*<value>(.*?)</value>' % re.escape(candidate),
                          text, re.S)
        if match:
            return match.group(1)
    raise KeyError("chiave %s non trovata in %s" % (key, path))


def normalize(text):
    """Confronto tollerante: maiuscole, accenti e punteggiatura non contano."""
    text = unicodedata.normalize("NFKD", text)
    text = "".join(c for c in text if not unicodedata.combining(c))
    text = text.lower()
    return re.sub(r"[^a-z0-9 ]+", " ", text).split()


def contains(haystack_words, needle):
    """Vero se le parole della stringa attesa compaiono in ordine nell'OCR."""
    needle_words = normalize(needle)
    if not needle_words:
        return True
    index = 0
    for word in haystack_words:
        if word == needle_words[index]:
            index += 1
            if index == len(needle_words):
                return True
    return False


def ocr(path, language):
    image = Image.open(path)
    return pytesseract.image_to_string(image, lang=language, config=tessdata_config())


def tessdata_config():
    """L'opzione con cui dire a Tesseract dove stanno i dati delle lingue."""
    if os.path.isdir(TESSDATA):
        return "--tessdata-dir " + os.path.abspath(TESSDATA)
    return ""


def tessdata_dir():
    """La cartella in cui Tesseract cerca i dati: la nostra, o quella del sistema."""
    if os.path.isdir(TESSDATA):
        return TESSDATA
    return os.path.join(os.path.dirname(TESSERACT), "tessdata")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", default=os.path.join(".superpowers", "ui-preview"))
    args = parser.parse_args()

    if not os.path.exists(TESSERACT):
        print("FAIL: Tesseract non trovato in %s" % TESSERACT)
        return 1

    pytesseract.pytesseract.tesseract_cmd = TESSERACT

    config = tessdata_config()

    try:
        print("tesseract " + str(pytesseract.get_tesseract_version()))
    except Exception as error:
        print("FAIL: il motore OCR non risponde: %s" % error)
        return 1

    # Tesseract non elenca le lingue tenendo conto di --tessdata-dir, quindi i dati
    # si controllano dove il motore li andra' davvero a cercare.
    data = tessdata_dir()
    print("dati delle lingue: " + os.path.abspath(data))
    missing = [code for code in sorted(LANGUAGES.values())
               if not os.path.exists(os.path.join(data, code + ".traineddata"))]
    if missing:
        print("FAIL: mancano i dati OCR per: " + ", ".join(missing))
        return 1

    # Le stringhe che devono essere leggibili, per lingua e per schermata.
    expectations = {
        "en-US": {
            "needLogin": ["Status_NeedLogin", "LoginButton/Content", "LoginHint/Text"],
            "authorized": ["Status_Authorized", "StartButton/Content", "PeersHeader/Text",
                           "LogHeader/Text"],
        },
        "it-IT": {
            "needLogin": ["Status_NeedLogin", "LoginButton/Content", "LoginHint/Text"],
            "authorized": ["Status_Authorized", "StartButton/Content", "PeersHeader/Text",
                           "LogHeader/Text"],
        },
    }

    checked = 0
    failures = []

    for language in ("en-US", "it-IT"):
        resw = RESW % language
        if not os.path.exists(resw):
            failures.append("manca %s" % resw)
            continue

        for state in ("needLogin", "authorized"):
            name = os.path.join(args.preview, "ui-%s-%s.png" % (language, state))
            if not os.path.exists(name):
                failures.append("%s non esiste: esegui prima render-ui-preview.py" % name)
                continue

            try:
                text = ocr(name, LANGUAGES[language])
            except pytesseract.TesseractError as error:
                failures.append("%s: OCR fallito (%s)" % (name, error))
                continue

            words = normalize(text)
            for key in expectations[language][state]:
                expected = resource(resw, key)
                checked += 1
                if not contains(words, expected):
                    failures.append("%s: non leggo \"%s\" (%s)" % (name, expected, key))

            # Una stringa lunga tagliata si riconosce cosi': meta' frase presente,
            # fine assente. Il suggerimento di login e' il caso che conta.
            if state == "needLogin":
                hint = resource(resw, "LoginHint/Text")
                tail = " ".join(hint.split()[-3:])
                checked += 1
                if not contains(words, tail):
                    failures.append("%s: la fine del suggerimento non c'e' (\"%s\")" % (name, tail))

    if failures:
        for failure in failures:
            print("FAIL: " + failure)
        return 1

    print("OCR OK: %d stringhe lette su %d immagini" % (checked, 4))
    return 0


if __name__ == "__main__":
    sys.exit(main())
