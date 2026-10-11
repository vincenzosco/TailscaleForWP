---
name: wp81-tailscale-tests-and-gate
description: Usa quando esegui o aggiungi un test — il runner in casa, gli attrezzi finti, e come si osserva un rosso onesto (compreso il caso che si blocca).
---

# Test e gate

Il runner è in casa: `src/Tailscale.Core.Tests/`, nessuna dipendenza esterna.
Non lancia eccezioni: **conta i fallimenti**, stampa tutti i problemi invece del
primo, e restituisce quel numero come exit code. Quindi l'unica uscita buona è
`ALL PASS` con exit 0, e `FAILURES: n` è già un fallimento anche se il testo
sembra rassicurante.

## Aggiungere un gruppo di test

1. Un file `*Tests.cs` con un metodo `Run()` che elenca i casi:
   `Case("nome del caso", Metodo)` — `Case` cattura le eccezioni e le conta come
   fallimenti, così un errore di battitura in un test non nasconde i successivi.
2. Registrare il gruppo in `src/Tailscale.Core.Tests/Program.cs`:
   `Run("nome", XTests.Run)`. Dimenticarlo è il modo più silenzioso di avere
   test che non girano mai.
3. Asserzioni da `src/Tailscale.Core.Tests/Assert.cs#T`: `T.Eq`, `T.True`,
   `T.False`, `T.Throws`. Contano in `T.Failures`, non lanciano.

## Gli attrezzi che ci sono già

| Attrezzo | Dove | A cosa serve |
|---|---|---|
| `FakeConnection` | `src/Tailscale.Core.Tests/ControlClientTests.cs`, e uno più ricco in `src/Tailscale.Core.Tests/ControlClientStateTests.cs` | canale di controllo che risponde a comando: registrazione, netmap, stato rotto |
| `FlakyConnection` | `src/Tailscale.Core.Tests/ControlClientStateTests.cs#FlakyConnection` | canale che fallisce le prime N volte, e la parte map (`MapMessage`, `MapFails`) |
| `TimeoutConnection` | `src/Tailscale.Core.Tests/ControlClientStateTests.cs#TimeoutConnection` | canale che scade: registra la scadenza chiesta dal client, utile per provare che la scadenza è quella giusta |
| `ScriptedConnection` | `src/Tailscale.Core.Tests/ControlClientStateTests.cs` | risposte a copione (stato, corpo) per il ciclo di registrazione |
| `AlternatingConnection` | `src/Tailscale.Core.Tests/ControlClientStateTests.cs` | canale instabile: fallisce una volta sì e una no |
| `SequenceRandomSource` | `src/Tailscale.Core.Tests/InMemoryDuplex.cs#SequenceRandomSource` | caso deterministico (le chiavi di un test devono essere sempre le stesse) |
| `MemoryStateStore` | `src/Tailscale.Core/Util/StateStore.cs#MemoryStateStore` | store in memoria al posto del file |
| `DelegateLogSink`, `NullLogSink`, `RingLogSink` | `src/Tailscale.Core/Util/Log.cs#DelegateLogSink` | catturare le righe di registro e asserirle con `HasLine(lines, "...")` |

Un test che misura il client non deve dipendere dal tempo reale: le attese
passano da `Sleep` iniettato, e i test lo sostituiscono con un contatore. Un test
che dorme davvero è un test che prima o poi fallisce da solo.

## Osservare un rosso onesto

Compila filtrando l'output **con `pipefail`**, altrimenti l'exit code che leggi è
quello di `tail` e non del compilatore:

```bash
set -o pipefail
cmd //c "tools\msbuild.cmd src\Tailscale.Core.Tests\Tailscale.Core.Tests.csproj -nologo -verbosity:minimal -p:Configuration=Release" 2>&1 | tail -5
echo "build exit=$?"
```

Poi esegui con un tetto di tempo e cattura l'exit code:

```bash
out=$(timeout 120 src/Tailscale.Core.Tests/bin/Release/Tailscale.Core.Tests.exe 2>&1); rc=$?
echo "$out" | tail -10; echo "tests exit=$rc"
```

Le due trappole già costate tempo in questo repository:

- **Il test nuovo è quello che si blocca.** Se una lettura resta appesa perché
  nessuno chiude la richiesta, il sintomo non è `FAIL` ma `exit=124` dal
  `timeout`: la suite non finisce. È il rosso più forte che ci sia — scrivilo
  così nel commit, perché è la prova che senza la correzione il client
  aspetterebbe per sempre.
- **Il build è fallito ma l'eseguibile risponde.** `bin/Release/...exe` resta
  quello vecchio: un test `ALL PASS` dopo un build rosso non ha provato niente.
  L'exit code del build va guardato prima di eseguire.

Ultima verifica: 2026-10-11
