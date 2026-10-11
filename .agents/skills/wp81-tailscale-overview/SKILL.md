---
name: wp81-tailscale-overview
description: Usa quando lavori in questo repository — mappa dei file, vincoli che decidono il codice (C# 5, zero NuGet, WP8.1) e il gate unico.
---

# TailscaleForWP: mappa, vincoli e gate

Un client Tailscale per Windows Phone 8.1: un'app WP8.1 con un motore portabile
in C# 5. Lo stesso sorgente del motore si compila **due volte**, come libreria
desktop e dentro l'app: è il motivo per cui il profilo di piattaforma del
telefono decide il codice del core e non solo quello della schermata.

## Dove sta cosa

```
src/Tailscale.Core/            motore portabile: nessuna UI, nessuna rete diretta
  Crypto/                      X25519, ChaCha20-Poly1305, BLAKE2s, SHA-256/HMAC, HKDF, Salsa20, NaCl box
  Protocol/                    costanti, handshake Noise IK, trasporto, HTTP/2, dialer ts2021
  Control/                     client di controllo, canale Noise, netmap
  Tailcfg/                     richieste e risposte del control plane
  Keys/                        chiavi tipizzate (nodekey:, mkey:, discokey:) e coppie
  Util/                        registro, store di stato, conversione testo
  Net/                         fetch HTTP e HTTP grezzo, dietro interfacce iniettabili
src/Tailscale.Cli/             verifica manuale da riga di comando
src/Tailscale.Core.Tests/      runner di test in casa, nessuna dipendenza esterna
Tailscale/                     app WP8.1: XAML, asset, risorse localizzate
  Services/EngineService.cs    il motore dentro l'app: stato esposto alla UI
  Services/PhonePlatform.cs    adattatori WP8.1: socket, sorgente casuale, fetcher
  Services/PhoneStores.cs      persistenza sul telefono
  Resources/Strings/it-IT/Resources.resw   le stringhe visibili all'utente
tools/                         pipeline di verifica, pin del sorgente, script di controllo
refs/                          sorgente Tailscale pinnato (git-ignorato, vedi tools/fetch-refs.py)
.agents/skills/                queste skill
```

Progetti: `src/Tailscale.Core/Tailscale.Core.Desktop.csproj` (core desktop),
`src/Tailscale.Core.Tests/Tailscale.Core.Tests.csproj` (test),
`src/Tailscale.Cli/Tailscale.Cli.csproj` (CLI), `Tailscale/Tailscale.csproj`
(app, che include il core con `<Compile Include="..\src\...">`).

Punti di ingresso del motore: `src/Tailscale.Core/Control/ControlClient.cs#Start`
(registrazione, attesa del login, lettura della netmap) e, sul telefono,
`Tailscale/Services/EngineService.cs`.

## I vincoli che decidono il codice

- **`LangVersion 5`** su tutto il codice condiviso: niente interpolazione di
  stringhe, `nameof`, membri con corpo di espressione, `async/await`, filtri di
  eccezione (`catch (...) when`).
- **Zero NuGet**: quello che serve o è nel framework o si scrive. La scadenza di
  una richiesta si fa con `System.Threading.Timer`, non con una libreria.
- **Profilo WP8.1**: non esistono `System.Security.Cryptography`,
  `Encoding.ASCII`, `System.Net.Sockets`, `Stream.Close()`,
  `Environment.OSVersion`, `List.AsReadOnly()`; `HttpWebRequest` è solo
  asincrona; le capability VPN di Windows 10 non esistono.
- È per questo che il core **inietta** invece di chiamare: `IRandomSource`
  (`src/Tailscale.Core/Keys/CryptoKey.cs#IRandomSource`), `IStreamConnector`
  (`src/Tailscale.Core/Net/RawHttp.cs#IStreamConnector`), `IHttpFetcher`
  (`src/Tailscale.Core/Net/HttpFetcher.cs#IHttpFetcher`), `Sleep` e
  `ConnectionFactory` in `ControlClientOptions`
  (`src/Tailscale.Core/Control/ControlClient.cs#ControlClientOptions`). Se ti
  serve una API di sistema nuova nel core, il segno è che va dietro una
  interfaccia e l'adattatore va nel telefono.
- Le chiavi private non finiscono mai nei log. Il testo visibile all'utente è in
  italiano e senza emoji (`tools/check-no-emoji.py`).

## Il gate: un comando solo

```bash
cmd //c "tools\check-all.cmd"
```

Su Windows con Git Bash usa **`cmd //c`**, non `cmd /c`: MSYS converte `/c` in un
percorso del file system e il comando stampa solo il banner di `cmd`. Il gate
esegue, nell'ordine: i controlli dei documenti e delle skill, la build del core
desktop, la build dei test, i test, la build della CLI con il suo `selftest`
offline, la build ARM del pacchetto WP8.1. Al primo problema esce con codice
diverso da zero. Atteso: `CHECK-ALL OK`, exit 0.

Comandi utili a pezzi (tutti con `cmd //c` quando passano da un `.cmd`):

```bash
cmd //c "tools\msbuild.cmd src\Tailscale.Core\Tailscale.Core.Desktop.csproj -nologo -verbosity:minimal -p:Configuration=Release"
cmd //c "tools\msbuild.cmd Tailscale\Tailscale.csproj -nologo -verbosity:minimal -p:Configuration=Release -p:Platform=ARM"
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe probe
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe selftest
python tools/check-protocol-pin.py
```

La build ARM non è un extra: è il controllo che trova i vincoli di piattaforma,
perché il profilo WP8.1 non ha le API che il desktop ha. Un `using` nuovo che il
telefono non conosce si vede solo lì.

## Dove vivono piani e ledger

I piani in `docs/superpowers/plans/`, le specifiche in
`docs/superpowers/specs/`, il ledger dell'esecuzione in `.superpowers/sdd/`.
Né `docs/` né `.superpowers/` sono tracciati in questo repository: se un piano
deve essere versionato, va tolto `docs/` dal `.gitignore` o spostato.

## La regola del README

Ogni comportamento nuovo o corretto aggiorna `README.md` in **entrambe** le
lingue: la sezione Stato se cambia cosa è fatto, la sezione Limiti noti se cambia
un limite. Un comportamento nuovo non documentato è un lavoro a metà, e un limite
non più vero è un bug della documentazione.

Per un lavoro nuovo la checklist completa è nella skill
`wp81-tailscale-feature`; per leggere un guasto, `wp81-tailscale-control-plane-debug`.

Ultima verifica: 2026-10-11
