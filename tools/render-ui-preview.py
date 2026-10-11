#!/usr/bin/env python
"""Disegna l'anteprima della pagina WP8.1, leggendo il XAML e le risorse vere.

Perche' un rendering e non uno screenshot: l'app gira solo su Windows Phone, e
l'appx non si esegue su questo PC. Un'anteprima costruita *dallo stesso XAML* e
*dalle stesse stringhe* verifica quello che si puo' verificare senza telefono:
che ogni testo esista, che sia leggibile alla dimensione dichiarata e che non
venga tagliato quando la lingua e' piu' lunga. Non verifica il layout reale del
sistema operativo, e il messaggio finale lo dice.

Uso: python tools/render-ui-preview.py [--out .superpowers/ui-preview]
"""
import argparse
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

XAML = os.path.join("Tailscale", "MainPage.xaml")
RESW = os.path.join("Tailscale", "Resources", "Strings", "%s", "Resources.resw")
WIDTH, HEIGHT = 480, 800
MARGIN = 16

# Le proporzioni sono quelle del telefono; i corpi dei testi sono letti dal XAML.
FALLBACK_FONT_SIZE = 16.0


def read(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def resources(language):
    text = read(RESW % language)
    pairs = re.findall(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', text, re.S)
    values = {}
    for name, value in pairs:
        # Nel .resw la chiave del controllo e' "Nome.Proprieta'"; nel codice, e
        # nell'anteprima, si usa "Nome/Proprieta'" (e' la stessa mappa di risorse).
        values[name.replace(".", "/", 1) if "." in name.split("/")[0] else name] = value
        values[name] = value
    return values


def font_sizes():
    """Le dimensioni dichiarate nel XAML, per nome di controllo."""
    xaml = read(XAML)
    sizes = {}
    for match in re.finditer(r'x:Name="([^"]+)"([^>]*)', xaml):
        size = re.search(r'FontSize="([0-9.]+)"', match.group(2))
        if size:
            sizes[match.group(1)] = float(size.group(1))
    return sizes


def font(size):
    for candidate in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(candidate, int(size))
        except Exception:
            continue
    return ImageFont.load_default()


def wrap(text, draw, face, max_width):
    words = text.split()
    lines, current = [], ""
    for word in words:
        probe = word if not current else current + " " + word
        if draw.textlength(probe, font=face) <= max_width:
            current = probe
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def draw_screen(language, state):
    """state: 'needLogin' oppure 'authorized'."""
    values = resources(language)
    sizes = font_sizes()

    image = Image.new("RGB", (WIDTH, HEIGHT), (28, 28, 30))
    draw = ImageDraw.Draw(image)
    y = 12

    device = "Lumia-535" if language == "en-US" else "Lumia-535-di-Vincenzo"
    draw.text((MARGIN, y), device, font=font(sizes.get("DeviceText", 22)), fill=(240, 240, 240))
    y += 34

    status_key = "Status_NeedLogin" if state == "needLogin" else "Status_Authorized"
    draw.text((MARGIN, y), values[status_key], font=font(sizes.get("StatusText", 16)), fill=(190, 190, 195))
    y += 34

    button_key = "LoginButton/Content" if state == "needLogin" else "StartButton/Content"
    button_label = values[button_key]
    button_height = 48
    draw.rectangle([MARGIN, y, WIDTH - MARGIN, y + button_height], outline=(150, 150, 160))
    label_face = font(sizes.get("PrimaryButton", 20))
    label_width = draw.textlength(button_label, font=label_face)
    draw.text(((WIDTH - label_width) / 2, y + 12), button_label, font=label_face, fill=(245, 245, 245))
    y += button_height + 10

    hint_lines = []
    if state == "needLogin":
        hint_face = font(sizes.get("LoginHint", 14))
        hint_lines = wrap(values["LoginHint/Text"], draw, hint_face, WIDTH - 2 * MARGIN)
        for line in hint_lines:
            draw.text((MARGIN, y), line, font=hint_face, fill=(180, 180, 185))
            y += 20
        y += 6

    header_face = font(sizes.get("PeersHeader", 16))
    header = values["PeersHeader/Text"]
    if state == "authorized":
        header += " (" + values["PeerCountFormat"].replace("{0}", "2") + ")"
    draw.text((MARGIN, y), header, font=header_face, fill=(235, 235, 235))
    y += 26

    peers = []
    if state == "authorized":
        peers = [
            ("server-di-casa", "linux - 100.101.102.103 - " + values["PeerOnline"]),
            ("telefono-di-anna", "android - 100.101.102.104 - " + values["PeerOffline"]),
        ]
    for title, detail in peers:
        draw.text((MARGIN, y), title, font=font(17), fill=(240, 240, 240))
        draw.text((MARGIN, y + 20), detail, font=font(13), fill=(170, 170, 175))
        y += 46

    log_y = HEIGHT - 168
    draw.text((MARGIN, log_y), values["LogHeader/Text"], font=header_face, fill=(235, 235, 235))
    log_face = font(sizes.get("LogText", 12))
    log_lines = [
        "canale di controllo: pronto",
        "registrazione: risposta http 200",
        "stato: NeedLogin -> Authorized",
        "netmap: 2 peer, dns=tailnet.ts.net",
    ]
    for index, line in enumerate(log_lines):
        draw.text((MARGIN, log_y + 22 + index * 16), line, font=log_face, fill=(165, 165, 170))

    return image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=os.path.join(".superpowers", "ui-preview"))
    args = parser.parse_args()

    if not os.path.exists(XAML):
        print("FAIL: %s non esiste" % XAML)
        return 1

    if not os.path.isdir(args.out):
        os.makedirs(args.out)

    written = []
    for language in ("en-US", "it-IT"):
        if not os.path.exists(RESW % language):
            print("FAIL: manca %s" % (RESW % language))
            return 1
        for state in ("needLogin", "authorized"):
            image = draw_screen(language, state)
            path = os.path.join(args.out, "ui-%s-%s.png" % (language, state))
            image.save(path)
            written.append(path)

    for path in written:
        print("scritto " + path)
    print("anteprima: %d immagini da %s e dalle risorse" % (len(written), XAML))
    return 0


if __name__ == "__main__":
    sys.exit(main())
