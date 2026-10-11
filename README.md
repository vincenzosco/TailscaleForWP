# TailscaleForWP

Un client **Tailscale per Windows Phone 8.1**: un'app WP8.1 con un motore portabile
scritto in C# 5, costruito leggendo il sorgente di Tailscale pinnato invece che dalla
memoria.

Il progetto nasce da una domanda precisa: si può parlare il protocollo di controllo
di Tailscale (ts2021, Noise IK) da un telefono del 2014 che ha solo .NET 4.5 e il
profilo di riferimento di WP8.1? La risposta finora è **sì per l'handshake** — e
questo repository contiene la prova, non solo il codice.

## Stato

| Componente | Stato | Come è verificato |
|---|---|---|
| Primitive crittografiche (X25519, ChaCha20-Poly1305, BLAKE2s, SHA-256, HKDF, NaCl box) | **Fatto** | Vector RFC 7748 / 8439 / 7693 / 5869 e vector indipendenti TweetNaCl |
| Handshake Noise IK (controlbase) | **Fatto** | **Contro il server reale**: `Tailscale.Cli handshake` |
| Chiavi del server, dialer ts2021, upgrade HTTP | **Fatto** | `Tailscale.Cli handshake` contro `controlplane.tailscale.com` |
| Framing del trasporto Noise | **Fatto** | Test in memoria, inclusa l'autenticazione di ogni byte |
| Registrazione del nodo, netmap, URL di login | **In corso, bloccata** | Il server parla **HTTP/2** dentro Noise; vedi "Limiti noti" |
| UI dell'app (login, lista peer) | Non iniziata | — |
| Loghi e splash screen | **Fatto** | Il pacchetto contiene le immagini byte per byte |
| Localizzazione (en-US, it-IT) | Risorse fatte | Il pacchetto di lingua `it` contiene le stringhe tradotte; manca l'accessor |

Dettaglio onesto: **l'app non fa ancora login**. L'handshake funziona contro il
server vero, la registrazione no. Il perché è documentato con l'evidenza, non
 nascosto: vedi `docs/superpowers/plans/2026-10-11-wp81-tailscale-1-control-plane-login.md`.

## La prova che conta

```
$ src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake
server key ts2021=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
            legacy=mkey:9e5156a4c65121306dd2d8ed8f92cb8d738e2533011344b522c5d28409bc4970
ts2021: upgrade accettato su http://controlplane.tailscale.com:80
handshake ok, peer=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
handshake hash=4754800d5370c4209dde12d541b20b85528d6787e1fbb13bd666d21693d0c973
versione del protocollo=148
```

Perché questo è una prova di interoperabilità e non un round-trip con se stessi: il
server reale accetta il nostro messaggio 1 da 101 byte e risponde con un messaggio il
cui tag verifica contro la chiave pubblica che pubblica in `/key`. Se la nostra
trascrizione di Noise IK, HKDF-BLAKE2s, X25519 o ChaCha20-Poly1305 fosse sbagliata,
quel tag non verificherebbe.

## Come si costruisce e si verifica

Tutto passa da un comando solo:

```bash
tools/check-all.cmd
```

Che esegue, in quest'ordine: il pin check del protocollo, la build del core desktop,
la build dei test, i test, la build della CLI con il suo `selftest` offline, e la
build ARM dell'app WP8.1 (compreso il pacchetto). Esce con codice diverso da zero al
primo problema. Atteso: `CHECK-ALL OK`.

La build ARM non è un extra: è il controllo che ha trovato **ogni** vincolo di
piattaforma di questo progetto. Il profilo WP8.1 non ha
`System.Security.Cryptography`, `Encoding.ASCII`, `System.Net.Sockets`,
`Stream.Close()`, `Environment.OSVersion`, `List.AsReadOnly()` e ha solo
`HttpWebRequest` asincrona. Tutti questi vincoli sono la ragione per cui il core
inietta `IRandomSource`, `IStreamConnector`, `IHttpFetcher` e `Sleep` invece di
chiamare direttamente una API.

Verifiche manuali che richiedono la rete (non nella pipeline, perché non devono
dipendere da tailscale.com):

```bash
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake   # handshake contro il server vero
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe probe       # diagnostica HTTP/2
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe login       # registrazione (bloccata, vedi sotto)
```

## Com'è fatto

Lo stesso sorgente del motore viene compilato **due volte**: come libreria desktop
(`src/Tailscale.Core/Tailscale.Core.Desktop.csproj`) e dentro l'app WP8.1
(`Tailscale/Tailscale.csproj`, che lo include con `<Compile Include="..\src\...">`).
`LangVersion 5` è imposto su entrambi, così una sintassi che il compilatore del
telefono non accetta non passa inosservata.

```
src/Tailscale.Core/          motore portabile, senza una riga di UI e senza rete diretta
  Crypto/                    X25519, ChaCha20, Poly1305, BLAKE2s, SHA-256/HMAC, HKDF, Salsa20, NaCl box
  Protocol/                  costanti del protocollo, handshake Noise IK, framing, dialer ts2021
  Control/                   JSON, chiavi del server, client di controllo, netmap
  Tailcfg/                   richieste e risposte del control plane
  Keys/                      chiavi tipizzate (nodekey:, mkey:, discokey:) e coppie
src/Tailscale.Cli/           strumento da riga di comando per la verifica reale
src/Tailscale.Core.Tests/    runner di test senza dipendenze esterne
Tailscale/                   app WP8.1: XAML, asset, risorse localizzate
tools/                       pipeline di verifica, pin del sorgente di riferimento, fetch
refs/                        sorgente Tailscale pinnato (git-ignorato, materializzato da tools/fetch-refs.py)
```

Scelte che vale la pena conoscere leggendo il codice:

- **Il pin del protocollo non è un commento.** `tools/refs.lock` fissa le revisioni di
  `tailscale` e `wireguard-go`, e `tools/check-protocol-pin.py` confronta le costanti
  di `ProtocolConstants` con il sorgente venduto: ogni valore ha file e riga in
  `docs/protocol-notes.md`. Se il sorgente e il codice divergono, la pipeline fallisce.
- **Ogni cosa che tocca la rete o il caso è iniettabile** (`IRandomSource`,
  `IStreamConnector`, `IHttpFetcher`, `Sleep`, `ConnectionFactory`): è ciò che permette
  di testare la macchina a stati del client senza rete e senza un account Tailscale.
- **I test non affermano ciò che non verificano.** Dove esiste un oracolo esterno
  (RFC, TweetNaCl, Python) il test lo usa; dove non esiste lo dichiara nel proprio
  commento invece di far passare un round-trip per interoperabilità.

## Limiti noti

1. **Il control server parla HTTP/2 dentro il trasporto Noise.** Il client Go lo
   forza (`tr.Protocols.SetUnencryptedHTTP2(true)`, `control/ts2021/client.go`), e
   una richiesta HTTP/1.1 viene chiusa senza risposta. `Control/NoiseControlConnection.cs`
   parla HTTP/1.1: la registrazione del nodo e la netmap non funzionano finché non
   esiste un'implementazione HTTP/2 (prefazione, frame, HPACK). È il prossimo lavoro,
   descritto nel piano del Piano 1.
2. **La registrazione richiede una persona.** Il passo di accettazione prevede di
   aprire l'URL di login e accedere con un account Tailscale: non è automatizzabile.
   Fino ad allora il Task 10 resta non verificato anche con HTTP/2 a posto.
3. **Nessun data plane.** Niente WireGuard, niente proxy SOCKS: l'app gestisce il
   piano di controllo. Il piano dati è il Piano 2, non iniziato.
4. **La localizzazione non ha ancora un accessor.** Le stringhe tradotte sono nel
   pacchetto, ma manca il Task 2 del piano di localizzazione.
5. I documenti di piano e il ledger vivono in `docs/`, che è **git-ignorato** in
   questo repository: non compaiono qui su GitHub. Se ti servono versionati, va
   tolto `docs/` dal `.gitignore`.

## Sicurezza

- Nessuna chiave privata viene mai scritta nei log o stampata: la CLI mostra solo
  chiavi pubbliche, e i test verificano che l'output non contenga materiale segreto.
- Il telefono dovrà custodire le chiavi private nel `PasswordVault`, non in
  `LocalSettings` (il piano dell'app lo prevede; non è ancora implementato).
- L'handshake è autenticato: il `Manifest` del server verifica contro la chiave
  pubblicata, quindi un proxy che intercetta non può impersonare il control server.
  Il tentativo in chiaro su :80 non indebolisce nulla per questo motivo.
- TLS 1.2 è richiesto esplicitamente: .NET 4.5 negozia TLS 1.0 e il control server lo
  rifiuta.

## Licenza

Non ancora definita. Il sorgente di riferimento venduto in `refs/` (Tailscale) è
BSD-3-Clause e non è ridistribuito qui: viene scaricato a parte da `tools/fetch-refs.py`.
