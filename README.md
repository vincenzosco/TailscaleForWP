# TailscaleForWP

**English** (default) | [Italiano](#italiano)

A **Tailscale client for Windows Phone 8.1**: a WP8.1 app with a portable engine
written in C# 5, built by reading the pinned Tailscale source instead of from
memory.

The project starts from one precise question: can the Tailscale control protocol
(ts2021, Noise IK) be spoken from a 2014 phone that only has .NET 4.5 and the
WP8.1 reference profile? So far the answer is **yes for the handshake** — and this
repository contains the proof, not just the code.

## Status

| Component | Status | How it is verified |
|---|---|---|
| Cryptographic primitives (X25519, ChaCha20-Poly1305, BLAKE2s, SHA-256, HKDF, NaCl box) | **Done** | RFC 7748 / 8439 / 7693 / 5869 vectors and independent TweetNaCl vectors |
| Noise IK handshake (controlbase) | **Done** | **Against the real server**: `Tailscale.Cli handshake` |
| Server keys, ts2021 dialer, HTTP upgrade | **Done** | `Tailscale.Cli handshake` against `controlplane.tailscale.com` |
| Noise transport framing | **Done** | In-memory tests, including authentication of every byte |
| Node registration, netmap, login URL | **Done in code, outlined by tests** | In-memory tests for the state machine (deadlines, retries, netmap) and `Tailscale.Cli login` for the real server; the acceptance on a phone is still manual |
| App UI (login, peer list, log panel) | **Done in code, outlined by tests** | `Tailscale/MainPage.xaml` and its code-behind, with every visible string checked by `tools/check-ui-resources.py`; the acceptance on a phone is still manual |
| Phone log that survives the app | **Done** | The engine writes `LocalFolder\registro.log` (a bounded tail of 400 lines): the policy has tests in the core, the WinRT file is verified by the ARM build |
| Logos and splash screens | **Done** | The package contains the images byte for byte |
| Localization (en-US, it-IT) | **Done** | `ResourceLoader` in `Tailscale/MainPage.xaml.cs`; `tools/check-ui-resources.py` checks that every string exists in both languages |

Honest detail: the handshake works against the real server, and registration,
the netmap and the request deadlines are implemented and covered by the
in-memory tests — but **none of it has been verified on a phone yet**. That
last step needs a person and a real device, and it is the one proof this
repository cannot produce by itself. What is missing is written down with the
evidence instead of hidden: see
`docs/superpowers/plans/2026-10-11-wp81-tailscale-14-request-deadlines.md`.

## The proof that matters

```
$ src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake
server key ts2021=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
            legacy=mkey:9e5156a4c65121306dd2d8ed8f92cb8d738e2533011344b522c5d28409bc4970
ts2021: upgrade accettato su http://controlplane.tailscale.com:80
handshake ok, peer=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
handshake hash=4754800d5370c4209dde12d541b20b85528d6787e1fbb13bd666d21693d0c973
versione del protocollo=148
```

Why this is a proof of interoperability and not a round-trip with itself: the
real server accepts our 101-byte message 1 and replies with a message whose tag
verifies against the public key it publishes at `/key`. If our transcription of
Noise IK, HKDF-BLAKE2s, X25519 or ChaCha20-Poly1305 were wrong, that tag would
not verify.

## How to build and verify

Everything goes through a single command:

```bash
tools/check-all.cmd
```

which runs, in this order: the protocol pin check, the desktop core build, the
test build, the tests, the CLI build with its offline `selftest`, and the ARM
build of the WP8.1 app (including the package). It exits with a non-zero code at
the first problem. Expected: `CHECK-ALL OK`.

The ARM build is not an extra: it is the check that found **every** platform
constraint in this project. The WP8.1 profile has no
`System.Security.Cryptography`, `Encoding.ASCII`, `System.Net.Sockets`,
`Stream.Close()`, `Environment.OSVersion`, `List.AsReadOnly()`, and only an
asynchronous `HttpWebRequest`. All of these constraints are the reason the core
injects `IRandomSource`, `IStreamConnector`, `IHttpFetcher` and `Sleep` instead
of calling an API directly.

Manual checks that need the network (not in the pipeline, because they must not
depend on tailscale.com):

```bash
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake   # handshake against the real server
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe probe       # HTTP/2 diagnostics
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe login       # registration: prints the login URL, the browser step needs a person
```

Manual checks that need a **phone** (not in the pipeline: there is no device
here). The app writes its log to `LocalFolder\registro.log`, and the SDK can copy
it to the PC — this is the difference between a failure that leaves evidence and
one that leaves a screenshot:

```bash
C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\IsolatedStorageExplorerTool\ISETool.exe EnumerateDevices
ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro   # attached phone
ISETool.exe ts xd 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro   # default emulator
```

## Agents

`.agents/skills/` holds five skills that a person or an agent can load by name
(the `skill` tool, or `npx skills add`): a map of the repository and its
platform constraints, the checklist for adding a feature end to end, how the
in-house test runner is used, the catalogue for reading a control-plane failure
out of the log, and the maintenance routine. They are not prose about the code:
every path, command, symbol and log line they cite is checked against the
sources by `tools/check-agents.py`, which runs inside `tools/check-all.cmd`. If
the code changes and the skill no longer matches, the gate fails and names the
skill. See `.agents/README.md` for the index and for what these skills cannot
verify (anything that needs a phone).

## How it is built

The same engine source is compiled **twice**: as a desktop library
(`src/Tailscale.Core/Tailscale.Core.Desktop.csproj`) and inside the WP8.1 app
(`Tailscale/Tailscale.csproj`, which includes it with `<Compile Include="..\src\...">`).
`LangVersion 5` is enforced on both, so syntax the phone's compiler rejects does
not slip through unnoticed.

```
src/Tailscale.Core/          portable engine, no UI and no direct network access
  Crypto/                    X25519, ChaCha20, Poly1305, BLAKE2s, SHA-256/HMAC, HKDF, Salsa20, NaCl box
  Protocol/                  protocol constants, Noise IK handshake, framing, ts2021 dialer
  Control/                   JSON, server keys, control client, netmap
  Tailcfg/                   control plane requests and responses
  Keys/                      typed keys (nodekey:, mkey:, discokey:) and key pairs
src/Tailscale.Cli/           command-line tool for real verification
src/Tailscale.Core.Tests/    test runner with no external dependencies
Tailscale/                   WP8.1 app: XAML, assets, localized resources
tools/                       verification pipeline, reference source pin, fetch
refs/                        pinned Tailscale source (git-ignored, materialized by tools/fetch-refs.py)
```

Choices worth knowing when reading the code:

- **The protocol pin is not a comment.** `tools/refs.lock` fixes the revisions of
  `tailscale` and `wireguard-go`, and `tools/check-protocol-pin.py` compares the
  `ProtocolConstants` against the vendored source: every value has its file and
  line in `docs/protocol-notes.md`. If the source and the code diverge, the
  pipeline fails.
- **Everything that touches the network or randomness is injectable**
  (`IRandomSource`, `IStreamConnector`, `IHttpFetcher`, `Sleep`,
  `ConnectionFactory`): that is what allows the client's state machine to be
  tested without a network and without a Tailscale account.
- **The tests do not claim what they do not verify.** Where an external oracle
  exists (RFC, TweetNaCl, Python) the test uses it; where none exists, the test
  says so in its own comment instead of passing a round-trip off as
  interoperability.

## Known limitations

1. **Registration requires a person.** The acceptance step involves opening the
   login URL and signing in with a Tailscale account: it cannot be automated.
   Until then Task 10 stays unverified.
2. **No data plane.** No WireGuard, no SOCKS proxy: the app handles the control
   plane. The data plane is Plan 2, not started.
3. The plan documents and the ledger live in `docs/`, which is **git-ignored** in
   this repository: they do not show up here on GitHub. If you need them
   versioned, remove `docs/` from `.gitignore`.
4. **A device deleted from the admin panel does not free the node key on the
   server.** The client notices from the `device with nodekey:... already exists`
   error, generates a new identity (machine key + node key) and retries. Because
   of this, if the app was not authorized, the first login shows up as a new
   device.
5. **The system can interrupt a read on the control socket** (the app is
   suspended, or the socket is closed abruptly): the error is
   `The I/O operation has been aborted because of either a thread exit or an
   application request` (`0x800703E3`). The client recognises it as a channel
   failure, declares the channel broken, rebuilds it and retries — up to
   `MaxChannelRetries` times for registration, then it shows an error instead of
   retrying forever. The app itself stops the engine on suspension by closing
   the socket, and on the way back to the foreground the engine starts again
   with the saved identity: the device in the tailnet stays one.
6. **No request waits forever any more.** Every request to the control server
   carries a deadline: short for a registration that is not a followup
   (`RegisterTimeoutSeconds`, 60s) and equal to the time left in the login window
   for a followup, because the server keeps that response open while the user
   signs in — a fixed value would either cut the browser off mid-login or outlive
   the window. A followup that expires is the end of the login window, not a
   channel failure: the state stays `NeedLogin` and the URL can be reopened. A
   netmap that never arrives stops with an error after the channel budget, while
   a netmap that arrived and was then lost keeps retrying without clearing what
   the user is looking at. The deadlines are verified on in-memory streams, where
   they end as a zero-byte read; on the phone the same `Dispose` produces the
   `0x800703E3` abort above, which is the case the phone check has to confirm.
7. **The WinRT adapters have no automated tests.** `SocketStream`,
   `PhoneStreamConnector`, `PhoneRandomSource`, `PhoneHttpFetcher`,
   `PasswordVaultAdapter`, `LocalSettingsAdapter` and `WinRtLogFile` call APIs
   that only exist on the phone, so no test on this PC can reach them. What was
   moved out of them is the part that decides: the random source judgement, the
   key vault policy, where the machine key is stored, and when the log file is
   trimmed all live in the core and have tests. What is left is a call, verified
   by the ARM build (which is what proves the API exists in the WP8.1 profile)
   and by the phone check above.

## Security

- No private key is ever written to the logs or printed: the CLI shows only
  public keys, and the tests verify that the output contains no secret material.
- The phone is meant to keep private keys in the `PasswordVault`, not in
  `LocalSettings` (the app plan provides for it; not implemented yet).
- The handshake is authenticated: the server's `Manifest` verifies against the
  published key, so an intercepting proxy cannot impersonate the control server.
  The plaintext attempt on :80 weakens nothing for this reason.
- TLS 1.2 is required explicitly: .NET 4.5 negotiates TLS 1.0 and the control
  server refuses it.

## License

Not defined yet. The reference source vendored in `refs/` (Tailscale) is
BSD-3-Clause and is not redistributed here: it is downloaded separately by
`tools/fetch-refs.py`.

---

# Italiano

[English](#tailscaleforwp) | **Italiano**

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
| Registrazione del nodo, netmap, URL di login | **Fatto nel codice, delineato dai test** | Test in memoria per la macchina a stati (scadenze, ritentate, netmap) e `Tailscale.Cli login` per il server vero; l'accettazione sul telefono e' ancora manuale |
| UI dell'app (login, lista peer, pannello Registro) | **Fatta nel codice, delineata dai test** | `Tailscale/MainPage.xaml` e il suo code-behind, con ogni stringa visibile controllata da `tools/check-ui-resources.py`; l'accettazione sul telefono e' ancora manuale |
| Registro del telefono che sopravvive all'app | **Fatto** | Il motore scrive `LocalFolder\registro.log` (una coda di 400 righe): la politica ha i test nel core, il file WinRT e' verificato dalla build ARM |
| Loghi e splash screen | **Fatto** | Il pacchetto contiene le immagini byte per byte |
| Localizzazione (en-US, it-IT) | **Fatto** | `ResourceLoader` in `Tailscale/MainPage.xaml.cs`; `tools/check-ui-resources.py` verifica che ogni stringa esista in entrambe le lingue |

Dettaglio onesto: l'handshake funziona contro il server vero, e registrazione,
netmap e scadenze delle richieste sono implementate e coperte dai test in
memoria — ma **niente di tutto questo e' stato verificato su un telefono**. Quel
passo richiede una persona e un dispositivo vero, ed e' l'unica prova che questo
repository non puo' produrre da se'. Quello che manca e' scritto con l'evidenza,
non nascosto: vedi
`docs/superpowers/plans/2026-10-11-wp81-tailscale-14-request-deadlines.md`.

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
src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe login       # registrazione: stampa l'URL di login, il passo nel browser richiede una persona
```

Verifiche manuali che richiedono un **telefono** (non nella pipeline: qui non c'e'
un dispositivo). L'app scrive il registro in `LocalFolder\registro.log`, e l'SDK sa
copiarlo sul PC: e' la differenza fra un guasto che lascia una prova e uno che
lascia uno screenshoot:

```bash
C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\IsolatedStorageExplorerTool\ISETool.exe EnumerateDevices
ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro   # telefono collegato
ISETool.exe ts xd 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro   # emulatore predefinito
```

## Agents

`.agents/skills/` contiene cinque skill che una persona o un agente può caricare
per nome (il tool `skill`, oppure `npx skills add`): la mappa del repository e i
suoi vincoli di piattaforma, la checklist per aggiungere una funzione da capo a
fondo, come si usa il runner di test in casa, il catalogo per leggere un guasto
del control plane dal registro, e la routine di manutenzione. Non sono prosa sul
codice: ogni percorso, comando, simbolo e riga di registro che citano è
verificato contro i sorgenti da `tools/check-agents.py`, che gira dentro
`tools/check-all.cmd`. Se il codice cambia e la skill non combacia più, il gate
fallisce e nomina la skill. L'indice è in `.agents/README.md`, che dice anche
cosa queste skill **non** possono verificare (tutto ciò che richiede un
telefono).

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

1. **La registrazione richiede una persona.** Il passo di accettazione prevede di
   aprire l'URL di login e accedere con un account Tailscale: non è automatizzabile.
   Fino ad allora il Task 10 resta non verificato.
2. **Nessun data plane.** Niente WireGuard, niente proxy SOCKS: l'app gestisce il
   piano di controllo. Il piano dati è il Piano 2, non iniziato.
3. I documenti di piano e il ledger vivono in `docs/`, che è **git-ignorato** in
   questo repository: non compaiono qui su GitHub. Se ti servono versionati, va
   tolto `docs/` dal `.gitignore`.
4. **Un device cancellato dal pannello admin non libera la node key sul server.**
   Il client se ne accorge dall'errore `device with nodekey:... already exists`,
   genera un'identità nuova (machine key + node key) e riprova. Per questo, se
   l'app non risultava autorizzata, al primo login compare un device nuovo.
5. **Il sistema può interrompere una lettura sul socket del control server**
   (sospensione dell'app, socket chiuso di colpo): l'errore è
   `The I/O operation has been aborted because of either a thread exit or an
   application request` (`0x800703E3`). Il client lo riconosce come guasto di
   canale, dichiara rotto il canale, lo rifa' e riprova -- fino a
   `MaxChannelRetries` volte per la registrazione, poi mostra un errore invece di
   ritentare per sempre. Alla sospensione il motore lo ferma l'app chiudendo lei il
   socket, e al ritorno in primo piano riparte da solo riusando l'identità salvata:
   il device nel tailnet resta uno.
6. **Nessuna richiesta resta appesa per sempre.** Ogni richiesta al control server
   porta con se' una scadenza: corta per una registrazione che non e' una followup
   (`RegisterTimeoutSeconds`, 60s) e pari al tempo che resta alla finestra di login
   per una followup, perche' il server tiene aperta quella risposta mentre l'utente
   accede -- un valore fisso o taglierebbe il browser a meta' login o vivrebbe piu'
   della finestra. Una followup scaduta e' la fine della finestra di login, non un
   guasto del canale: lo stato resta `NeedLogin` e l'URL si puo' riaprire. Una
   netmap che non arriva mai si ferma con un errore dopo il tetto dei tentativi,
   mentre una netmap gia' arrivata e poi persa continua a ritentare senza
   cancellare quello che l'utente sta guardando. Le scadenze sono verificate su
   stream in memoria, dove finiscono con una lettura a zero byte; sul telefono lo
   stesso `Dispose` produce l'abort `0x800703E3` descritto sopra, ed e' il caso che
   la verifica sul telefono deve confermare.
7. **Gli adattatori WinRT non hanno test automatici.** `SocketStream`,
   `PhoneStreamConnector`, `PhoneRandomSource`, `PhoneHttpFetcher`,
   `PasswordVaultAdapter`, `LocalSettingsAdapter` e `WinRtLogFile` chiamano API che
   esistono solo sul telefono, quindi nessun test su questo PC può raggiungerle.
   Quello che è stato portato fuori da loro è la parte che decide: il giudizio sulla sorgente casuale, la
   politica del portachiavi, dove sta la chiave macchina e quando il file del
   registro si taglia vivono nel core e hanno i loro test. Quello che resta è una
   chiamata, verificata dalla build ARM (ed è lei a dimostrare che l'API esiste nel
   profilo WP8.1) e dalla verifica sul telefono qui sopra.

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
