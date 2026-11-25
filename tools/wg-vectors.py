#!/usr/bin/env python3
"""Genera i vettori di test dell'handshake WireGuard per il core C#.

Perche' un generatore e non i vettori del sorgente Go: nel commit pinnato
(refs/wireguard-go, 417aef361226) `device/noise_test.go` e' randomizzato e
non contiene byte attesi. Questo script e' una SECONDA implementazione,
scritta dalle equazioni di docs/wireguard-notes.md in Python con la libreria
`cryptography` (BoringSSL per X25519 e ChaCha20-Poly1305, hashlib per
BLAKE2s): i vettori generati qui sono verificati da un'implementazione che
non condivide una riga con il core C#.

Uso (in un venv con `pip install cryptography`):
    python tools/wg-vectors.py > src/Tailscale.Core.Tests/WgVectors.cs

Uscita: una classe C# con gli input fissi e gli output attesi come byte.
"""
import hashlib
import hmac
import struct
import sys

from cryptography.hazmat.primitives.asymmetric.x25519 import (
    X25519PrivateKey, X25519PublicKey)
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305

CONSTRUCTION = b"Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s"
IDENTIFIER = b"WireGuard v1 zx2c4 Jason@zx2c4.com"
LABEL_MAC1 = b"mac1----"

# Input fissi: chiavi private (mai degeneri: KeyPair rifiuta 32 byte uguali),
# psk (zeri, come Tailscale), timestamp e indici.
INIT_STATIC_PRIV = bytes(range(1, 33))
RESP_STATIC_PRIV = bytes(range(33, 65))
INIT_EPH_PRIV = bytes(range(65, 97))
RESP_EPH_PRIV = bytes(range(97, 129))
PSK = bytes(32)
INIT_SENDER_INDEX = 0x11223344
RESP_SENDER_INDEX = 0x55667788

# TAI64N di 1700000000 s e 123456789 ns: i nanosecondi vengono smussati a
# 7 * 2^24 come da tai64n.go (si azzera la maschera 0xFFFFFF, non altro).
TAI64N = struct.pack(">Q", 0x400000000000000A + 1700000000) + \
    struct.pack(">I", 123456789 & ~0xFFFFFF & 0xFFFFFFFF)


def pub(priv):
    return X25519PrivateKey.from_private_bytes(priv).public_key().public_bytes_raw()


def dh(priv, public):
    return X25519PrivateKey.from_private_bytes(priv).exchange(
        X25519PublicKey.from_public_bytes(public))


def h1(data):
    return hashlib.blake2s(data, digest_size=32).digest()


def hm(key, data):
    return hmac.new(key, data, hashlib.blake2s).digest()


def kdf1(ck, x):
    return hm(hm(ck, x), b"\x01")


def kdf2(ck, x):
    prk = hm(ck, x)
    t1 = hm(prk, b"\x01")
    t2 = hm(prk, t1 + b"\x02")
    return t1, t2


def kdf3(ck, x):
    prk = hm(ck, x)
    t1 = hm(prk, b"\x01")
    t2 = hm(prk, t1 + b"\x02")
    t3 = hm(prk, t2 + b"\x03")
    return t1, t2, t3


def aead(key, ad, plaintext):
    return ChaCha20Poly1305(key).encrypt(b"\x00" * 12, plaintext, ad)


def aead_open(key, ad, ciphertext):
    return ChaCha20Poly1305(key).decrypt(b"\x00" * 12, ciphertext, ad)


def mixhash(h, data):
    return h1(h + data)


def mac1(destination_static, message):
    key = h1(LABEL_MAC1 + destination_static)
    return hashlib.blake2s(message, key=key, digest_size=16).digest()


def main():
    out = {}
    values = []

    ck = h1(CONSTRUCTION)
    h = h1(ck + IDENTIFIER)

    init_static_pub = pub(INIT_STATIC_PRIV)
    resp_static_pub = pub(RESP_STATIC_PRIV)
    init_e_pub = pub(INIT_EPH_PRIV)
    resp_e_pub = pub(RESP_EPH_PRIV)

    out["init_static_priv"] = INIT_STATIC_PRIV
    out["resp_static_priv"] = RESP_STATIC_PRIV
    out["init_eph_priv"] = INIT_EPH_PRIV
    out["resp_eph_priv"] = RESP_EPH_PRIV
    out["init_static_pub"] = init_static_pub
    out["resp_static_pub"] = resp_static_pub
    out["psk"] = PSK
    out["timestamp"] = TAI64N

    # --- initiation (initiatore) ---
    h = mixhash(h, resp_static_pub)
    ck = kdf1(ck, init_e_pub)
    h = mixhash(h, init_e_pub)

    ck, key = kdf2(ck, dh(INIT_EPH_PRIV, resp_static_pub))
    enc_static = aead(key, h, init_static_pub)
    h = mixhash(h, enc_static)

    ck, key = kdf2(ck, dh(INIT_STATIC_PRIV, resp_static_pub))
    enc_ts = aead(key, h, TAI64N)
    h = mixhash(h, enc_ts)

    head = struct.pack("<II", 1, INIT_SENDER_INDEX)
    initiation_wo_mac = head + init_e_pub + enc_static + enc_ts
    m1 = mac1(resp_static_pub, initiation_wo_mac)
    initiation = initiation_wo_mac + m1 + bytes(16)

    out["initiation"] = initiation
    out["initiation_mac1"] = m1

    # --- initiation (responder): specchio + verifica ---
    rck = h1(CONSTRUCTION)
    rh = h1(rck + IDENTIFIER)
    rh = mixhash(rh, resp_static_pub)
    rh = mixhash(rh, init_e_pub)
    rck = kdf1(rck, init_e_pub)

    rck, rkey = kdf2(rck, dh(RESP_STATIC_PRIV, init_e_pub))
    got_static = aead_open(rkey, rh, enc_static)
    assert got_static == init_static_pub, "static non torna"
    rh = mixhash(rh, enc_static)

    rck, rkey = kdf2(rck, dh(RESP_STATIC_PRIV, init_static_pub))
    got_ts = aead_open(rkey, rh, enc_ts)
    assert got_ts == TAI64N, "timestamp non torna"
    rh = mixhash(rh, enc_ts)

    assert (rck, rh) == (ck, h), "le catene dei due lati non coincidono"

    # --- response (responder) ---
    rh = mixhash(rh, resp_e_pub)
    rck = kdf1(rck, resp_e_pub)
    rck, _ = kdf2(rck, dh(RESP_EPH_PRIV, init_e_pub))
    rck, _ = kdf2(rck, dh(RESP_EPH_PRIV, init_static_pub))
    rck, tau, rkey = kdf3(rck, PSK)
    rh = mixhash(rh, tau)
    empty = aead(rkey, rh, b"")
    rh = mixhash(rh, empty)

    resp_head = struct.pack("<III", 2, RESP_SENDER_INDEX, INIT_SENDER_INDEX)
    response_wo_mac = resp_head + resp_e_pub + empty
    m2 = mac1(init_static_pub, response_wo_mac)
    response = response_wo_mac + m2 + bytes(16)

    out["response"] = response
    out["response_mac1"] = m2

    # --- response (initiatore): specchio + verifica ---
    h = mixhash(h, resp_e_pub)
    ck = kdf1(ck, resp_e_pub)
    ck = kdf1(ck, dh(INIT_EPH_PRIV, resp_e_pub))
    ck = kdf1(ck, dh(INIT_STATIC_PRIV, resp_e_pub))
    ck, tau, key = kdf3(ck, PSK)
    h = mixhash(h, tau)
    got_empty = aead_open(key, h, empty)
    assert got_empty == b"", "empty non torna"
    h = mixhash(h, empty)

    assert (ck, h) == (rck, rh), "le catene finali non coincidono"
    out["handshake_hash"] = h

    # --- chiavi di sessione: KDF2(ck, vuoto) ---
    send1, recv1 = kdf2(ck, b"")
    out["initiator_send_key"] = send1
    out["initiator_recv_key"] = recv1

    for name in sorted(out):
        values.append((name, out[name]))

    sys.stdout.write("using System;\n\n")
    sys.stdout.write("namespace Tailscale.Core.Tests\n{\n")
    sys.stdout.write("    /// <summary>\n")
    sys.stdout.write("    /// Generato da tools/wg-vectors.py con un'implementazione indipendente\n")
    sys.stdout.write("    /// (BoringSSL): non modificare a mano.\n")
    sys.stdout.write("    /// </summary>\n")
    sys.stdout.write("    public static class WgVectors\n    {\n")
    for name, value in values:
        sys.stdout.write("        public static readonly byte[] %s = T.FromHex(\"%s\");\n"
                         % (camel(name), value.hex()))
    sys.stdout.write("    }\n}\n")


def camel(name):
    return "".join(part.capitalize() for part in name.split("_"))


if __name__ == "__main__":
    main()
