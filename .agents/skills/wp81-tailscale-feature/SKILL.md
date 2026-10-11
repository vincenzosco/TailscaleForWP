---
name: wp81-tailscale-feature
description: Usa quando aggiungi una funzione o cambi il comportamento dell'app — la checklist end-to-end, da test rosso a gate verde, passando da core, app, risorse e README.
---

# Aggiungere una funzione, da capo a fondo

Sette passi, in quest'ordine. Saltare il primo o il secondo è il modo in cui
questo progetto si è già fatta male una volta: codice scritto senza sapere quale
test lo avrebbe difeso.

## 1. Prima il piano, poi il codice

- Se la funzione cambia il comportamento, scrivi **spec** e **piano**:
  `docs/superpowers/specs/` per la specifica, `docs/superpowers/plans/` per il
  piano (skill `writing-plans`). Per una cosa che tocca una sola schermata il
  piano è una pagina, non un documento: il piano esiste per fissare i nomi, i
  valori esatti e i test, non per riempire spazio.
- Ogni requisito del piano deve avere il suo test nel task che possiede il
  codice. Se non sai quale test proverebbe la funzione, la funzione non è
  ancora definita.

## 2. Il test prima (rosso)

- Il test sta in `src/Tailscale.Core.Tests/`, in un gruppo con `Case("nome",
  Metodo)` dentro `Run()`.
- Registra il gruppo in `src/Tailscale.Core.Tests/Program.cs`
  (`Run("nome", XTests.Run)`), altrimenti non gira e il verde è finto.
- Asserzioni e attrezzi: vedi la skill `wp81-tailscale-tests-and-gate`. Il
  ciclo rosso -> verde va osservato davvero, con l'exit code, non dedotto.

## 3. Il core, con le dipendenze iniettate

La funzione vive in `src/Tailscale.Core/` e non tocca direttamente né la rete,
né il caso, né il tempo: usa `IRandomSource`, `IStreamConnector`,
`IHttpFetcher`, `Sleep`, `ConnectionFactory` — per esempio
`src/Tailscale.Core/Control/ControlClient.cs#ControlClientOptions` è già il
posto dove il chiamante decide queste cose. È la ragione per cui la macchina a
stati si prova su stream in memoria, senza telefono e senza account Tailscale.

Cosa non fare:

- **Niente `async/await`** per "risolvere" un'attesa: nel core l'attesa è
  `Sleep` iniettato, e su WP8.1 il ciclo resta sincrono.
- **Niente pacchetti NuGet**: se una libreria sembra necessaria, o si scrive
  quel che serve o si sposta la funzione fuori dal core.
- **Niente gestione di un guasto nuovo senza una riga di registro**: chi legge
  il Registro dell'app deve poter capire dal log cosa è successo. La mappa delle
  righe esistenti è nella skill `wp81-tailscale-control-plane-debug`; una riga
  nuova va aggiunta lì, altrimenti il controllo del Task 7 del piano 15 non la
  vede.
- **Niente decisioni in un file che si compila solo per il telefono**: se una
  scelta (cosa salvare, quando tagliare una coda, cosa fare quando una lettura
  restituisce altro) sta in `Tailscale/Services/`, nessuno la verifica finché non
  arriva un dispositivo — ed è così che sono nati i quattro guasti trovati
  leggendo il codice del telefono. La scelta va nel core con il suo test;
  nell'app resta la chiamata WinRT, e a verificarla è la build ARM.
- **Niente chiavi private nei log**, mai, nemmeno in forma abbreviata se
  abbreviare lascia indovinare.

## 4. Collegare all'app

| File | Responsabilità |
|---|---|
| `Tailscale/Services/EngineService.cs` | il motore dentro l'app: avvia e ferma il client, espone stato e lista peer alla schermata |
| `Tailscale/Services/PhonePlatform.cs` | adattatori WP8.1: socket, sorgente casuale, fetcher, attesa |
| `Tailscale/Services/PhoneStores.cs` | persistenza sul telefono: **solo** gli adattatori (`PasswordVaultAdapter`, `LocalSettingsAdapter`), senza decisioni — la politica «segreto nel portachiavi, preferenze nelle impostazioni» sta in `src/Tailscale.Core/Util/VaultBackedStateStore.cs` |
| `Tailscale/Services/PhoneLogFile.cs` | il file del registro: l'unico punto che tocca `StorageFile` e `FileIO`; quando tagliare e cosa fare se fallisce è in `src/Tailscale.Core/Util/TextFileLog.cs` |
| `Tailscale/MainPage.xaml` + `Tailscale/MainPage.xaml.cs` | la schermata: stato, login, lista peer, pannello Registro |
| `Tailscale/Resources/Strings/it-IT/Resources.resw` | le stringhe visibili all'utente, in italiano |

Una stringa nuova nella UI non può restare senza risorsa: `python
tools/check-ui-resources.py` (dentro il gate) confronta le stringhe usate con le
risorse e fallisce se una manca. Nessuna emoji: `python tools/check-no-emoji.py`.

## 5. Aggiornare il README, in entrambe le lingue

`README.md` ha una parte inglese e una italiana, e non è un riassunto
facoltativo: sezione **Stato** se cambia cosa è fatto, sezione **Limiti noti** se
cambia un limite (anche in meglio: un limite non più vero è un bug della
documentazione, non un dettaglio). Aggiornare una lingua sola lascia il README
in contraddizione con se stesso.

## 6. Aggiornare le skill

Se la funzione cambia un comando, un percorso, un simbolo o una riga di registro
citati in `.agents/skills/`, aggiorna la skill nello stesso commit. Il gate lo
pretende comunque: `python tools/check-agents.py` verifica ogni citazione contro
il codice e nomina la skill falsa.

## 7. Il gate, e poi il telefono

```bash
cmd //c "tools\check-all.cmd"
```

Atteso: `agents ok`, `pin ok`, i test `ALL PASS`, la build ARM, `CHECK-ALL OK`,
exit 0. Se la funzione tocca la connettività, il gate **non** è la prova finale:
serve la verifica su un telefono vero (deploy, percorso di accesso, ultime 20
righe del Registro). Finché non è fatta, va dichiarata come non fatta — in
questo ambiente non c'è né emulatore né dispositivo, quindi la catena è provata
solo su stream in memoria.

Ultima verifica: 2026-10-11
