#!/usr/bin/env python3
"""Verifica che le costanti di protocollo corrispondano al sorgente pinnato.

Legge i valori da refs/tailscale (materializzato da tools/fetch-refs.py) e li
confronta sia con i valori attesi sia con src/Tailscale.Core/Protocol/ProtocolConstants.cs.
Se il pin viene aggiornato e upstream cambia qualcosa, questo script fallisce
PRIMA dei test: e' il punto del controllo.

Uscita: 0 se tutto corrisponde, 1 altrimenti (con il dettaglio dei mismatch).
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REFS = os.path.join(ROOT, "refs", "tailscale")
CONSTANTS_CS = os.path.join(ROOT, "src", "Tailscale.Core", "Protocol", "ProtocolConstants.cs")
NOTES = os.path.join(ROOT, "docs", "protocol-notes.md")

# (file nel tree vendored, regex, nome della costante C#, valore atteso o None)
CHECKS = [
    ("control/controlhttp/constants.go", r'serverUpgradePath\s*=\s*"([^"]+)"', "UpgradePath", "/ts2021"),
    ("control/controlhttp/controlhttpcommon/controlhttpcommon.go", r'UpgradeHeaderValue\s*=\s*"([^"]+)"', "UpgradeHeaderValue", "tailscale-control-protocol"),
    ("control/controlhttp/controlhttpcommon/controlhttpcommon.go", r'HandshakeHeaderName\s*=\s*"([^"]+)"', "HandshakeHeaderName", "X-Tailscale-Handshake"),
    ("control/controlbase/handshake.go", r'protocolName\s*=\s*"([^"]+)"', "NoiseInstanceName", "Noise_IK_25519_ChaChaPoly_BLAKE2s"),
    ("control/controlbase/handshake.go", r'protocolVersionPrefix\s*=\s*"([^"]+)"', "ProloguePrefix", "Tailscale Control Protocol v"),
    ("tailcfg/tailcfg.go", r'CurrentCapabilityVersion\s+\w+\s*=\s*(\d+)', "CapabilityVersion", "148"),
    ("control/controlbase/messages.go", r'msgTypeInitiation\s*=\s*(\d+)', "MsgTypeInitiation", "1"),
    ("control/controlbase/messages.go", r'msgTypeResponse\s*=\s*(\d+)', "MsgTypeResponse", "2"),
    ("control/controlbase/messages.go", r'msgTypeError\s*=\s*(\d+)', "MsgTypeError", "3"),
    ("control/controlbase/messages.go", r'msgTypeRecord\s*=\s*(\d+)', "MsgTypeRecord", "4"),
    ("control/controlbase/messages.go", r'^\theaderLen\s*=\s*(\d+)', "HeaderLen", "3"),
    ("control/controlbase/messages.go", r'initiationHeaderLen\s*=\s*(\d+)', "InitiationHeaderLen", "5"),
    ("control/controlbase/messages.go", r'type initiationMessage \[(\d+)\]byte', "InitiationMessageLen", "101"),
    ("control/controlbase/messages.go", r'type responseMessage \[(\d+)\]byte', "ResponseMessageLen", "51"),
    ("control/controlbase/conn.go", r'^\tmaxMessageSize\s*=\s*(\d+)', "MaxMessageSize", "4096"),
    ("control/controlclient/direct.go", r'(/machine/register)', "RegisterPath", "/machine/register"),
    ("control/controlclient/direct.go", r'(/machine/map)', "MapPath", "/machine/map"),
    ("control/controlclient/direct.go", r'(/key\?v=%d)', "ServerKeyPathFormat", "/key?v=%d"),
]

# La costante C# puo' presentare il valore in forma diversa da quella Go: il
# confronto va fatto secondo la regola giusta, non tutte con l'uguaglianza.
GO_ONLY = {"ServerKeyPathFormat"}      # Go: '/key?v=%d'  |  C#: '{0}/key?v={1}'
CONTAINS_CS = {"RegisterPath", "MapPath"}


def read_cs_constants(path):
    with open(path, "r", encoding="utf-8") as fh:
        body = fh.read()
    found = {}
    for match in re.finditer(r"public const \w+ (\w+)\s*=\s*([^;]+);", body):
        found[match.group(1)] = match.group(2).strip()
    return found


def main():
    if not os.path.isdir(REFS):
        print("[FAIL] refs/tailscale non c'e': esegui  python tools/fetch-refs.py")
        return 1

    cs = read_cs_constants(CONSTANTS_CS)
    errors = []
    checked = 0

    for rel, pattern, name, expected in CHECKS:
        path = os.path.join(REFS, rel.replace("/", os.sep))
        if not os.path.exists(path):
            errors.append("%s: file vendored mancante (%s)" % (name, rel))
            continue
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        match = re.search(pattern, body, re.MULTILINE)
        if not match:
            errors.append("%s: pattern non trovato in %s (il pin e' cambiato?)" % (name, rel))
            continue
        actual = match.group(1)
        if expected is not None and actual != expected:
            errors.append("%s: nel tree vendored vale %r, atteso %r" % (name, actual, expected))
            continue
        if name not in cs:
            errors.append("%s: costante assente da ProtocolConstants.cs" % name)
            continue
        cs_value = cs[name].strip().strip('"')
        if name in GO_ONLY:
            # La forma nel C# e' volutamente diversa (placeholder {0}/{1}); la
            # copertura di quel valore sta nel test unitario, non qui.
            checked += 1
            continue
        if expected is not None and name in CONTAINS_CS:
            if expected not in cs_value:
                errors.append("%s: ProtocolConstants.cs dice %r, non contiene %r" % (name, cs_value, expected))
                continue
            checked += 1
            continue
        if expected is not None and cs_value != expected:
            errors.append("%s: ProtocolConstants.cs dice %r, il sorgente dice %r" % (name, cs_value, expected))
            continue
        checked += 1

    # Coerenza del pin con le note: il tag pinnato deve comparire nelle note,
    # altrimenti le righe citate nelle note descrivono un'altra revisione.
    pin_path = os.path.join(REFS, "PINNED_VERSION.txt")
    if os.path.exists(pin_path):
        with open(pin_path, "r", encoding="utf-8") as fh:
            pin = fh.read()
        tag = re.search(r"ref=(\S+)", pin)
        if tag and os.path.exists(NOTES):
            with open(NOTES, "r", encoding="utf-8") as fh:
                notes = fh.read()
            if tag.group(1) not in notes:
                errors.append("docs/protocol-notes.md non cita il pin %s: le righe documentate sono di un'altra revisione" % tag.group(1))

    if errors:
        print("[FAIL] pin di protocollo:")
        for err in errors:
            print("       - " + err)
        return 1

    print("pin ok: %d costanti verificate contro refs/tailscale" % checked)
    return 0


if __name__ == "__main__":
    sys.exit(main())
