<p align="center">
  <img src="Tailscale/Assets/Logo.scale-240.png" width="128" alt="TailscaleForWP icon">
</p>

<div align="center">

# TailscaleForWP

**A Tailscale client for Windows Phone 8.1.** Sign-in, registration and the peer
list work on the phone. Traffic does not pass through it yet.

[What works](#what-works-today) · [Build and verify](#build-and-verify) · [Use it](#use-it) · [Troubleshooting](#troubleshooting) · [Italiano](#italiano)

![Windows Phone 8.1](https://img.shields.io/badge/platform-Windows%20Phone%208.1-0078D7)
![C-sharp 5](https://img.shields.io/badge/engine-C%23%205-68217A)
![MIT License](https://img.shields.io/badge/license-MIT-green)

</div>

One app for the phone and one portable engine, both C# 5. The engine speaks the
Tailscale control protocol (ts2021, Noise IK) with `controlplane.tailscale.com`,
the same one the official clients use. There is no server of mine in the middle.

## What works today

- **Good for:** getting a tailnet's control plane onto a WP8.1 phone: sign-in,
  registration, the peer list, and a log that survives the app.
- **Not for:** moving traffic. There is no data plane (no WireGuard, no proxy),
  so no app on the phone reaches a peer through this today.

| Part | State | How it is checked |
|---|---|---|
| Crypto (X25519, ChaCha20-Poly1305, BLAKE2s, SHA-256, HKDF, NaCl box) | done | RFC 7748 / 8439 / 7693 / 5869 vectors, plus independent TweetNaCl vectors |
| Noise IK handshake, ts2021 dialer | done | `Tailscale.Cli handshake` against `controlplane.tailscale.com` |
| Registration, netmap, sign-in URL | works on a phone | state machine tests in memory, and a phone run on 2026-10-11; the first sign-in still needs a person at the browser |
| App UI (sign-in, device list, log) | done in code | every visible string checked by `tools/check-ui-resources.py`; phone acceptance is still manual |
| Phone log (`LocalFolder\registro.log`) | done | pulled from a real phone with `ISETool.exe` on 2026-10-11 |
| en-US and it-IT strings | done | `tools/check-ui-resources.py` checks both |
| VPN link settings via interop registry | done | provision and revert run against a scratch registry key, see [provisioning/README.md](provisioning/README.md) |
| Data plane (WireGuard, proxy) | not started | nothing yet |

## Build and verify

You need a Windows PC with MSBuild 12.0 (VS2013), the Windows Phone 8.1 SDK and
Python 3. No NuGet, no dotnet, no Go.

```
tools\check-all.cmd
```

It checks the protocol pin, the UI strings and the agent skills first, then builds
the desktop core, runs the tests, runs the CLI selftest and builds the ARM package
of the app. The first problem stops it. When everything is fine the last line is
`CHECK-ALL OK`.

One of the checks talks to the live server. This is what `handshake` printed on
2026-11-22 from this repo:

```
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe handshake
server key ts2021=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
            legacy=mkey:9e5156a4c65121306dd2d8ed8f92cb8d738e2533011344b522c5d28409bc4970
ts2021: upgrade accettato su http://controlplane.tailscale.com:80
handshake ok, peer=mkey:7d2792f9c98d753d2042471536801949104c247f95eac770f8fb321595e2173b
handshake hash=389aafa04037612c14cecffd0b40436369b78021d1bc3f0003448b3910ec715e
versione del protocollo=148
```

The hash changes at every run, the server key and the protocol version do not. The
server's answer only verifies if our Noise IK and cipher code matches its own, byte
for byte, so a wrong transcription fails here instead of failing on the phone.

The ARM build is where the WP8.1 limits bite: no `System.Security.Cryptography`, no
`System.Net.Sockets`, no `Encoding.ASCII`, and `HttpWebRequest` only works
asynchronously. That is why the core takes `IRandomSource`, `IStreamConnector`,
`IHttpFetcher` and `Sleep` as injected dependencies: the same code runs in the
desktop tests, offline.

## Use it

**On the PC.** The CLI talks to the live server:

```
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe handshake             # Noise IK handshake with the Tailscale control server
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe probe                 # HTTP/2 diagnostics
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe login                 # prints the sign-in URL; the browser step needs a person
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe selftest              # offline, no network
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe provision [manifest] [out-dir]   # writes provision.bat and deprovision.bat for CMD Injector
```

**On the phone.** Deploy the package the gate built. Unlock the screen first, or
the launch gives up.

```
"C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe" /installlaunch Tailscale\bin\ARM\Release\Tailscale_1.0.0.0_Bundle\Tailscale_1.0.0.0_ARM.appx /targetdevice:de
```

The engine starts when the app opens. The line under the device name moves through
`Connecting...`, `Sign in required`, `Connected`, `Error` and `Stopped`, and the
button says what it does: `Start`, `Stop` or `Sign in`. The first time, the browser
opens on the Tailscale sign-in page; come back to the app afterwards.

> **`/installlaunch` resets the saved identity.** You get a first start: new node
> key, sign-in in the browser. To open the same install again without signing in,
> use `/launch 563a8f70-0e06-4690-b075-3963581a75c8 /targetdevice:de`. The product
> id is in `Tailscale/Package.appxmanifest`.

**The log.** The engine writes `LocalFolder\registro.log` (the screen keeps 200
lines, the file 400). To copy it to the PC:

```
ISETool.exe EnumerateDevices
ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro
ISETool.exe ts xd 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro
```

`ISETool.exe` lives in `C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\IsolatedStorageExplorerTool\`. `de` is a connected phone, `xd` the default emulator.

**The VPN link.** On a phone with interop unlock the app can add and switch a
system connection with one tap, or by itself when the engine connects. What gets
written is decided by `vpn-manifest.txt` in the app folder (the built-in manifest
only touches `HKLM\SOFTWARE\TailscaleWP`), and the same manifest generates the
`provision.bat` payload for CMD Injector: see
[provisioning/README.md](provisioning/README.md).

The WP8.1 Runtime package requests `ID_CAP_INTEROPSERVICES` through
`Tailscale/WindowsPhoneReservedAppInfo.xml`; putting it in
`Package.appxmanifest` is rejected by the AppX schema. This reserved capability
metadata is for local sideload/development and does not itself grant registry
writes or replace CMD Injector.

> **The app cannot write the registry from its own sandbox**, not even on an
> interop-unlocked phone. The automatic path needs CMD Injector installed and
> running, with its telnet service on `127.0.0.1:23`. Without it the app refuses
> to write anything, on purpose.

And the registry does not create a tunnel: it stores configuration and points the
system connection, nothing more.

## Troubleshooting

<details>
<summary><strong>AppDeployCmd says the app could not be started</strong></summary>
<br>

Most of the time the phone screen is locked. Unlock it and run the launch again.
Without the app running there is no log to read, so fix this one first.

</details>

<details>
<summary><strong>The install or launch fails with 0xC0000005 or 0x87B20C0A</strong></summary>
<br>

That is the launcher tool failing on its own, not the app. Run it again. The
second attempt usually goes through.

</details>

<details>
<summary><strong>The log is full of 0x800703E3 aborts</strong></summary>
<br>

That is what a control-channel read looks like when the app is suspended: the app
closes the socket on purpose. The client takes it as a broken channel and the
engine comes back with the same node key, so the tailnet still sees one device.
If the same error shows up while the app is in the foreground, pull the log and
keep the lines around it.

</details>

<details>
<summary><strong>Signing in created a second device in the admin console</strong></summary>
<br>

A device deleted from the admin console keeps its node key on the server, and
reinstalling resets the identity, so the app registers again as a new device.
Launch the existing install with `/launch` instead of `/installlaunch` and the
identity survives.

</details>

<details>
<summary><strong>The VPN button reports an interop or registry error</strong></summary>
<br>

The app can distinguish an unavailable direct ntdll channel from a denied
registry command and from other telnet errors. In `registro.log`, look for
`vpn: telnet presente ma probe non verificato` or `vpn: accesso diretto non
verificato`; the reason is recorded without registry values. A reachable CMD
Injector telnet service does not by itself prove that `reg.exe` can write the
required key.

</details>

## Good to know

- Everything the phone compiles is C# 5: `LangVersion 5` is set on both builds, so
  syntax the phone compiler rejects fails on the PC too.
- The tests run offline and without a Tailscale account, because everything that
  touches the network or randomness is injected (`IRandomSource`, `IStreamConnector`,
  `IHttpFetcher`, `Sleep`).
- The protocol pin has its own gate:
  `tools/check-protocol-pin.py` compares `ProtocolConstants` against the pinned
  `tailscale` and `wireguard-go` sources (revisions in `tools/refs.lock`,
  downloaded into `refs/` by `tools/fetch-refs.py`). If code and source disagree,
  the gate fails.
- The plan documents live in `docs/`, which is not committed to git. On GitHub you
  get the code, the agent skills and this README.

## Security

- No private key goes into the log or on the screen. The CLI prints public keys
  only, and a test checks its output for secret material.
- The handshake is authenticated: the server's manifest verifies against the key
  it publishes at `/key`, so a proxy in the middle cannot impersonate the control
  server. The first message goes out in the clear on port 80 and does not weaken
  it.
- TLS 1.2 is required explicitly, because .NET 4.5 negotiates TLS 1.0 and the
  server refuses it.
- Private keys on the phone belong in the `PasswordVault`. The store that does
  that is planned, not written.

## More

- [.agents/README.md](.agents/README.md): five skills for working on this repo.
  Every path, command and log line they cite is checked against the sources by
  `tools/check-agents.py`, which runs inside `tools/check-all.cmd`.
- [AGENTS.md](AGENTS.md): how commits are written here.
- [License](LICENSE): MIT. The Tailscale source that lands in `refs/` is
  BSD-3-Clause and is not redistributed here; `tools/fetch-refs.py` downloads it.

---

## Italiano

[English](#tailscaleforwp) | **Italiano**

Un client **Tailscale per Windows Phone 8.1**: un'app per il telefono e un motore
portabile, entrambi in C# 5. Il motore parla il protocollo di controllo Tailscale
(ts2021, Noise IK) con `controlplane.tailscale.com`, lo stesso dei client ufficiali.
Non c'è nessun mio server in mezzo. Il traffico non passa ancora da qui: il piano
dati non è scritto.

## Cosa funziona oggi

- **Va bene per:** portare il control plane di un tailnet su un telefono WP8.1:
  accesso, registrazione, elenco dei peer e un registro che sopravvive all'app.
- **Non va bene per:** muovere traffico. Non c'è un piano dati (niente WireGuard,
  niente proxy): oggi nessuna app del telefono passa da qui.

| Parte | Stato | Come è verificato |
|---|---|---|
| Primitive crittografiche (X25519, ChaCha20-Poly1305, BLAKE2s, SHA-256, HKDF, NaCl box) | fatto | Vector RFC 7748 / 8439 / 7693 / 5869 e vector indipendenti TweetNaCl |
| Handshake Noise IK, dialer ts2021 | fatto | `Tailscale.Cli handshake` contro `controlplane.tailscale.com` |
| Registrazione, netmap, URL di accesso | funziona su un telefono | test in memoria sulla macchina a stati, e una corsa sul telefono il 2026-10-11; il primo accesso richiede ancora una persona al browser |
| UI dell'app (accesso, elenco dispositivi, registro) | fatto nel codice | ogni stringa visibile controllata da `tools/check-ui-resources.py`; l'accettazione sul telefono è ancora manuale |
| Registro del telefono (`LocalFolder\registro.log`) | fatto | tirato giù da un telefono vero con `ISETool.exe` il 2026-10-11 |
| Stringhe en-US e it-IT | fatto | `tools/check-ui-resources.py` controlla entrambe |
| Impostazioni del collegamento VPN via registro | fatto | provision e revert provati su una chiave di registro di prova, vedi [provisioning/README.md](provisioning/README.md) |
| Piano dati (WireGuard, proxy) | non iniziato | niente |

## Costruire e verificare

Su un PC Windows servono MSBuild 12.0 (VS2013), lo SDK Windows Phone 8.1 e
Python 3. Niente NuGet, niente dotnet, niente Go.

```
tools\check-all.cmd
```

Controlla prima il pin del protocollo, le stringhe della UI e le skill degli agent,
poi costruisce il core desktop, esegue i test, lancia il selftest offline della CLI
e costruisce il pacchetto ARM dell'app. Al primo problema si ferma. Se tutto va
bene l'ultima riga è `CHECK-ALL OK`.

Uno dei controlli parla con il server vivo: l'output di `handshake` del 2026-11-22
è nella [sezione inglese](#build-and-verify). L'hash cambia a ogni corsa, la chiave
del server e la versione del protocollo no. La risposta del server verifica solo se
il nostro Noise IK e il nostro codice dei cifrari coincidono con i suoi, byte per
byte: una trascrizione sbagliata fallisce qui, non sul telefono.

La build ARM è dove si sentono i vincoli di WP8.1: niente
`System.Security.Cryptography`, niente `System.Net.Sockets`, niente
`Encoding.ASCII`, e `HttpWebRequest` funziona solo in asincrono. Per questo il core
riceve `IRandomSource`, `IStreamConnector`, `IHttpFetcher` e `Sleep` come
dipendenze iniettate: lo stesso codice gira nei test desktop, offline.

## Usarla

**Sul PC.** La CLI parla con il server vivo:

```
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe handshake             # handshake Noise IK con il server di controllo Tailscale
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe probe                 # diagnostica HTTP/2
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe login                 # stampa l'URL di accesso; il passo nel browser richiede una persona
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe selftest              # offline, senza rete
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe provision [manifest] [out-dir]   # scrive provision.bat e deprovision.bat per CMD Injector
```

**Sul telefono.** Installa il pacchetto che ha costruito il gate. Prima sblocca lo
schermo, sennò il comando di lancio si arrende.

```
"C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe" /installlaunch Tailscale\bin\ARM\Release\Tailscale_1.0.0.0_Bundle\Tailscale_1.0.0.0_ARM.appx /targetdevice:de
```

Il motore parte quando si apre l'app. La riga sotto il nome del dispositivo passa
per `Connecting...`, `Sign in required`, `Connected`, `Error` e `Stopped`, e il
pulsante dice quello che fa: `Start`, `Stop` o `Sign in`. La prima volta il browser
si apre sulla pagina di accesso di Tailscale; poi si torna nell'app.

> **`/installlaunch` azzera l'identità salvata.** Si riparte da zero: node key
> nuova, accesso nel browser. Per riaprire la stessa installazione senza rifare
> l'accesso usa `/launch 563a8f70-0e06-4690-b075-3963581a75c8 /targetdevice:de`.
> L'id del prodotto è in `Tailscale/Package.appxmanifest`.

**Il registro.** Il motore scrive `LocalFolder\registro.log` (lo schermo tiene 200
righe, il file 400). Per copiarlo sul PC:

```
ISETool.exe EnumerateDevices
ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro
ISETool.exe ts xd 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro
```

`ISETool.exe` sta in `C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\IsolatedStorageExplorerTool\`. `de` è un telefono collegato, `xd` l'emulatore predefinito.

**Il collegamento VPN.** Su un telefono con interop unlock l'app sa aggiungere e
attivare un collegamento di sistema con un tocco, o da sola quando il motore si
collega. Cosa si scrive lo decide `vpn-manifest.txt` nella cartella dell'app (il
manifest integrato tocca solo `HKLM\SOFTWARE\TailscaleWP`), e lo stesso manifest
genera il payload `provision.bat` per CMD Injector: vedi
[provisioning/README.md](provisioning/README.md).

Il pacchetto WP8.1 Runtime richiede `ID_CAP_INTEROPSERVICES` tramite
`Tailscale/WindowsPhoneReservedAppInfo.xml`; inserirla in
`Package.appxmanifest` viene rifiutato dallo schema AppX. Questi metadati di
capability riservata servono per installazione locale e sviluppo: da soli non
autorizzano la scrittura del registro e non sostituiscono CMD Injector.

> **L'app non può scrivere il registro dalla propria sandbox**, nemmeno con
> l'interop unlock. Il percorso automatico richiede CMD Injector installato e
> avviato, con il servizio telnet su `127.0.0.1:23`. Senza, l'app non scrive
> niente, di proposito.

E il registro non crea alcun tunnel: conserva la configurazione e punta il
collegamento di sistema, niente di più.

## Se qualcosa non va

<details>
<summary><strong>AppDeployCmd dice che l'app non è partita</strong></summary>
<br>

Nove volte su dieci lo schermo del telefono è bloccato. Sbloccalo e rifai il lancio:
senza l'app avviata non c'è nessun registro da leggere, quindi questo si sistema
per primo.

</details>

<details>
<summary><strong>Installazione o lancio falliscono con 0xC0000005 o 0x87B20C0A</strong></summary>
<br>

È lo strumento di lancio che fallisce da solo, non l'app. Riprova: di solito al
secondo tentativo passa.

</details>

<details>
<summary><strong>Nel registro è pieno di abort 0x800703E3</strong></summary>
<br>

È come si presenta una lettura sul canale di controllo quando l'app va in
sospensione: l'app chiude il socket di proposito. Il client lo prende come canale
rotto e il motore riparte con la stessa node key, quindi il tailnet vede sempre un
dispositivo solo. Se lo stesso errore compare con l'app in primo piano, tira giù il
registro e conserva le righe intorno.

</details>

<details>
<summary><strong>L'accesso ha creato un secondo dispositivo nella console admin</strong></summary>
<br>

Un dispositivo cancellato dalla console conserva la node key sul server, e
reinstallare azzera l'identità: l'app si registra di nuovo come dispositivo nuovo.
Avvia l'installazione esistente con `/launch` invece di `/installlaunch` e
l'identità sopravvive.

</details>

<details>
<summary><strong>Il pulsante VPN segnala un errore di interop o del registro</strong></summary>
<br>

L'app distingue un canale ntdll diretto non disponibile, un comando di registro
negato e gli altri errori telnet. In `registro.log`, cerca `vpn: telnet presente
ma probe non verificato` o `vpn: accesso diretto non verificato`; la causa è
registrata senza valori del registro. Il fatto che CMD Injector risponda via
telnet non dimostra da solo che `reg.exe` possa scrivere la chiave richiesta.

</details>

## Buone a sapersi

- Tutto ciò che compila il telefono è C# 5: `LangVersion 5` è impostato su entrambe
  le build, così una sintassi che il compilatore del telefono rifiuta fallisce
  anche sul PC.
- I test girano offline e senza un account Tailscale, perché tutto ciò che tocca la
  rete o il caso è iniettato (`IRandomSource`, `IStreamConnector`, `IHttpFetcher`,
  `Sleep`).
- Il pin del protocollo ha un gate suo:
  `tools/check-protocol-pin.py` confronta `ProtocolConstants` con i sorgenti
  pinnati di `tailscale` e `wireguard-go` (revisioni in `tools/refs.lock`,
  scaricate in `refs/` da `tools/fetch-refs.py`). Se codice e sorgente divergono,
  il gate fallisce.
- I documenti di piano stanno in `docs/`, che non è committato in git. Su GitHub ci
  sono il codice, le skill degli agent e questo README.

## Sicurezza

- Nessuna chiave privata finisce nel registro o a schermo. La CLI mostra solo
  chiavi pubbliche, e un test controlla che il suo output non contenga materiale
  segreto.
- L'handshake è autenticato: il manifest del server verifica contro la chiave che
  pubblica in `/key`, quindi un proxy in mezzo non può impersonare il control
  server. Il primo messaggio in chiaro sulla porta 80 non indebolisce nulla.
- TLS 1.2 è richiesto esplicitamente, perché .NET 4.5 negozia TLS 1.0 e il server
  lo rifiuta.
- Le chiavi private sul telefono stanno nel `PasswordVault`. Il deposito che lo fa
  è previsto, non è scritto.

## Licenza

Questo progetto è distribuito con licenza MIT; vedi [LICENSE](LICENSE). Il sorgente
di Tailscale scaricato in `refs/` resta BSD-3-Clause e non viene ridistribuito qui.
