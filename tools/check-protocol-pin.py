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
WG_REFS = os.path.join(ROOT, "refs", "wireguard-go")
WG_CONSTANTS_CS = os.path.join(ROOT, "src", "Tailscale.Core", "Wg", "WgConstants.cs")
WG_NOTES = os.path.join(ROOT, "docs", "wireguard-notes.md")

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

# Stesse regole per refs/wireguard-go contro WgConstants.cs. Le righe "num"
# si confrontano valutando l'espressione intera (i due linguaggi scrivono gli
# stessi numeri in forme diverse): i valori sono quelli di
# docs/wireguard-notes.md, trascritti dal sorgente pinnato.
WG_CHECKS = [
    ("str", "device/noise-protocol.go", r'NoiseConstruction\s*=\s*"([^"]+)"', "Construction", "Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s"),
    ("str", "device/noise-protocol.go", r'WGIdentifier\s*=\s*"([^"]+)"', "Identifier", "WireGuard v1 zx2c4 Jason@zx2c4.com"),
    ("str", "device/noise-protocol.go", r'WGLabelMAC1\s*=\s*"([^"]+)"', "LabelMac1", "mac1----"),
    ("str", "device/noise-protocol.go", r'WGLabelCookie\s*=\s*"([^"]+)"', "LabelCookie", "cookie--"),
    ("num", "device/noise-protocol.go", r'MessageInitiationType\s*=\s*(\d+)', "MessageInitiationType", "1"),
    ("num", "device/noise-protocol.go", r'MessageResponseType\s*=\s*(\d+)', "MessageResponseType", "2"),
    ("num", "device/noise-protocol.go", r'MessageCookieReplyType\s*=\s*(\d+)', "MessageCookieReplyType", "3"),
    ("num", "device/noise-protocol.go", r'MessageTransportType\s*=\s*(\d+)', "MessageTransportType", "4"),
    ("num", "device/noise-protocol.go", r'MessageInitiationSize\s*=\s*(\d+)', "MessageInitiationSize", "148"),
    ("num", "device/noise-protocol.go", r'MessageResponseSize\s*=\s*(\d+)', "MessageResponseSize", "92"),
    ("num", "device/noise-protocol.go", r'MessageCookieReplySize\s*=\s*(\d+)', "MessageCookieReplySize", "64"),
    ("num", "device/noise-protocol.go", r'MessageTransportHeaderSize\s*=\s*(\d+)', "MessageTransportHeaderSize", "16"),
    ("num", "device/noise-protocol.go", r'MessageTransportOffsetReceiver\s*=\s*(\d+)', "MessageTransportOffsetReceiver", "4"),
    ("num", "device/noise-protocol.go", r'MessageTransportOffsetCounter\s*=\s*(\d+)', "MessageTransportOffsetCounter", "8"),
    ("num", "device/noise-protocol.go", r'MessageTransportOffsetContent\s*=\s*(\d+)', "MessageTransportOffsetContent", "16"),
    ("num", "tai64n/tai64n.go", r'base\s*=\s*uint64\((0x[0-9A-Fa-f]+)\)', "Tai64Base", "0x400000000000000a"),
    ("num", "tai64n/tai64n.go", r'whitenerMask\s*=\s*uint32\((0x[0-9A-Fa-f -]+)\)', "NanosecondWhitenerMask", "0x1000000 - 1"),
    ("num", "device/constants.go", r'RekeyAfterMessages\s*=\s*(\(1 \d+\)|1 << \d+|\(1 << \d+\))', "RekeyAfterMessages", "1 << 60"),
    ("num", "device/constants.go", r'RejectAfterMessages\s*=\s*(\(1 << 64\) - \(1 << 13\) - 1)', "RejectAfterMessages", "(1 << 64) - (1 << 13) - 1"),
    ("num", "device/constants.go", r'RekeyAfterTime\s*=\s*time\.Second\s*\*\s*(\d+)', "RekeyAfterSeconds", "120"),
    ("num", "device/constants.go", r'RejectAfterTime\s*=\s*time\.Second\s*\*\s*(\d+)', "RejectAfterSeconds", "180"),
    ("num", "device/constants.go", r'KeepaliveTimeout\s*=\s*time\.Second\s*\*\s*(\d+)', "KeepaliveSeconds", "10"),
    ("num", "device/constants.go", r'RekeyTimeout\s*=\s*time\.Second\s*\*\s*(\d+)', "RekeyTimeoutSeconds", "5"),
    ("num", "device/constants.go", r'RekeyAttemptTime\s*=\s*time\.Second\s*\*\s*(\d+)', "RekeyAttemptSeconds", "90"),
]


def eval_int(expr):
    """Valuta un'espressione intera costante, con sole cifre, parentesi e
    gli operatori <<, + e -. Le costanti di sistema ammesse sono poche e
    dichiarate qui: niente eval sul codice vendored."""
    cleaned = expr.replace("ulong.MaxValue", "18446744073709551615").replace("UL", "").strip()
    if not re.match(r"^[0-9x() <<+\-A-Fa-f]+$", cleaned):
        raise ValueError("espressione non ammessa: " + expr)
    return int(eval(cleaned, {"__builtins__": {}}, {}))


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

    # WireGuard: stessa regola contro refs/wireguard-go e WgConstants.cs.
    wg_cs = {}
    if not os.path.isfile(WG_CONSTANTS_CS):
        errors.append("WgConstants.cs non esiste: attendi il piano 2 (Task 1)")
    else:
        wg_cs = read_cs_constants(WG_CONSTANTS_CS)
    if not os.path.isdir(WG_REFS):
        errors.append("refs/wireguard-go non c'e': esegui  python tools/fetch-refs.py")
    else:
        for kind, rel, pattern, name, expected in WG_CHECKS:
            path = os.path.join(WG_REFS, rel.replace("/", os.sep))
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
            if name not in wg_cs:
                errors.append("%s: costante assente da WgConstants.cs" % name)
                continue
            cs_value = wg_cs[name].strip()
            try:
                if kind == "num":
                    same = eval_int(actual) == eval_int(expected) == eval_int(cs_value)
                else:
                    same = actual == expected and cs_value.strip('"') == expected
            except ValueError as ex:
                errors.append("%s: valore non valutabile (%s)" % (name, ex))
                continue
            if not same:
                errors.append("%s: vendored %r, atteso %r, WgConstants.cs %r"
                              % (name, actual, expected, cs_value))
                continue
            checked += 1

        # Coerenza del pin con le note WireGuard, stessa regola di sopra.
        wg_pin_path = os.path.join(WG_REFS, "PINNED_VERSION.txt")
        if os.path.exists(wg_pin_path):
            with open(wg_pin_path, "r", encoding="utf-8") as fh:
                wg_pin = fh.read()
            wg_tag = re.search(r"ref=(\S+)", wg_pin)
            if wg_tag and os.path.isfile(WG_NOTES):
                with open(WG_NOTES, "r", encoding="utf-8") as fh:
                    wg_notes = fh.read()
                if wg_tag.group(1) not in wg_notes:
                    errors.append("docs/wireguard-notes.md non cita il pin %s: le righe documentate sono di un'altra revisione"
                                  % wg_tag.group(1))
            elif wg_tag and not os.path.isfile(WG_NOTES):
                errors.append("docs/wireguard-notes.md mancante: servono le note del protocollo pinnato")
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

    print("pin ok: %d costanti verificate contro refs/tailscale e refs/wireguard-go" % checked)
    return 0


if __name__ == "__main__":
    sys.exit(main())
