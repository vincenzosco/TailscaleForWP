#!/usr/bin/env python
"""Controlla che la UI e le risorse dicano la stessa cosa.

Tre regole, tutte cose che si rompono in silenzio:

1. ogni `x:Uid` del XAML ha la sua chiave (`<uid>.Text`, `<uid>.Content`) in
   **entrambe** le lingue: una chiave mancante fa vedere all'utente il nome della
   chiave, non un errore;
2. nessun testo visibile scritto a mano nel XAML: se c'e', la traduzione non lo
   tocchera' mai;
3. gli elementi che la pagina usa dal codice (pulsante principale, lista peer,
   registro) esistono, con il nome che il codice cerca.

Uso: python tools/check-ui-resources.py
"""
import os
import re
import sys

XAML = os.path.join("Tailscale", "MainPage.xaml")
LANGUAGES = ("en-US", "it-IT")
RESW = os.path.join("Tailscale", "Resources", "Strings", "%s", "Resources.resw")

# Nomi che MainPage.xaml.cs cerca: rinominarli nel XAML senza toccare il codice
# darebbe una NullReferenceException a runtime, sul telefono.
REQUIRED_NAMES = ("DeviceText", "StatusText", "PrimaryButton", "LoginHint",
                  "NoBrowserHint", "PeersHeader", "PeerList", "LogHeader", "LogText")

# Chiavi usate dal codice (ResourceLoader.GetString). La barra e' la forma che
# vede il codice: nel .resw la stessa chiave si scrive con il punto, perche' il
# punto separa il controllo dalla proprieta' ("PeersHeader/Text" sta in resw come
# "PeersHeader.Text").
REQUIRED_KEYS = ("Status_Connecting", "Status_NeedLogin", "Status_Authorized",
                 "Status_Error", "Status_Stopped", "LoginButton/Content",
                 "StartButton/Content", "PeerOnline", "PeerOffline",
                 "PeersHeader/Text", "PeerCountFormat")


def read(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def resw_keys(path):
    return set(re.findall(r'<data name="([^"]+)"', read(path)))


def main():
    problems = []

    if not os.path.exists(XAML):
        print("FAIL: %s non esiste" % XAML)
        return 1

    xaml = read(XAML)
    uids = sorted(set(re.findall(r'x:Uid="([^"]+)"', xaml)))
    names = set(re.findall(r'x:Name="([^"]+)"', xaml))

    for language in LANGUAGES:
        path = RESW % language
        if not os.path.exists(path):
            problems.append("manca %s" % path)
            continue
        keys = resw_keys(path)

        for uid in uids:
            candidates = [uid + ".Text", uid + ".Content", uid]
            if not any(c in keys for c in candidates):
                problems.append("%s: la chiave di x:Uid=\"%s\" non esiste (%s)"
                                % (language, uid, " o ".join(candidates)))

        for key in REQUIRED_KEYS:
            if key.replace("/", ".") not in keys:
                problems.append("%s: manca la chiave \"%s\" usata dal codice" % (language, key))

    for name in REQUIRED_NAMES:
        if name not in names:
            problems.append("MainPage.xaml: manca x:Name=\"%s\" (il codice lo cerca)" % name)

    # Testi visibili scritti a mano: TextBlock/Button senza x:Uid ma con Text o
    # Content valorizzato. Le {Binding} sono dati, non testo di interfaccia.
    for match in re.finditer(r'<(TextBlock|Button)\b([^>]*)>', xaml):
        element, attributes = match.group(1), match.group(2)
        if 'x:Uid=' in attributes:
            continue
        text = re.search(r'\b(Text|Content)="([^"]*)"', attributes)
        if text and not text.group(2).startswith("{Binding"):
            line = xaml.count("\n", 0, match.start()) + 1
            problems.append("MainPage.xaml:%d: %s con testo scritto a mano (%r): usa x:Uid"
                            % (line, element, text.group(2)))

    if problems:
        for problem in problems:
            print("FAIL: " + problem)
        return 1

    print("UI e risorse coerenti: %d x:Uid, %d chiavi richieste, %d lingue"
          % (len(uids), len(REQUIRED_KEYS), len(LANGUAGES)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
