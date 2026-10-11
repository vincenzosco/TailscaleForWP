#!/usr/bin/env python
"""Stampa i fixture C# per i test di HPACK (src/Tailscale.Core.Tests/HpackTests.cs).

I blocchi sono prodotti da `hpack` (la stessa libreria usata da hyper/httpx), cosi'
il decoder C# viene provato contro un encoder indipendente e reale invece che
contro se stesso: un round-trip interno passerebbe anche con un Huffman sbagliato
in modo simmetrico.

Uso: python tools/gen-http2-fixtures.py
"""
from hpack import Encoder

def csharp(hexstr, name):
    # Spezzato in righe da 60 caratteri, come si legge in un test.
    chunks = [hexstr[i:i + 60] for i in range(0, len(hexstr), 60)]
    body = "\n".join('            "%s"' % c for c in chunks)
    return "        // %s\n        const string %s =\n%s;" % (name, name, body)


def encode(headers):
    return Encoder().encode(headers, huffman=True).hex()


request = encode([
    (":method", "POST"),
    (":scheme", "https"),
    (":authority", "controlplane.tailscale.com"),
    (":path", "/machine/register"),
    ("content-type", "application/json"),
    ("user-agent", "tailscale"),
])

response = encode([
    (":status", "200"),
    ("content-type", "application/json"),
])

plain = encode([
    (":method", "GET"),
    (":path", "/key?v=148"),
])

big = encode([("x-tailscale-hostinfo", "linux; tailscale 1.104.1; " + "a" * 300)])

print("// Fixture generati da tools/gen-http2-fixtures.py (hpack 4.2.0), non a mano.")
print("// request  (6 header, Huffman):", len(request) // 2, "byte")
print(csharp(request, "RequestBlockHex"))
print()
print("// response (:status 200):", len(response) // 2, "byte")
print(csharp(response, "ResponseBlockHex"))
print()
print("// richiesta GET senza Huffman sui valori noti:", len(plain) // 2, "byte")
print(csharp(plain, "PlainBlockHex"))
print()
print("// valore lungo: esercita il padding di Huffman:", len(big) // 2, "byte")
print(csharp(big, "LongValueBlockHex"))
