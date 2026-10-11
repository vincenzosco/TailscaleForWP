# Changelog

Formato: `## YYYY-MM-DD — <titolo>`, con "cosa è cambiato", "pin di protocollo",
"verifiche eseguite" e "limiti noti". La voce si scrive quando il lavoro è
finito, non quando è iniziato: le verifiche eseguite sono parte del contenuto.

## 2026-10-11 — Nessuna richiesta senza scadenza, e agenti per mantenerla

**Cosa è cambiato**

- Ogni richiesta al control server ha una scadenza: corta per una registrazione
  che non è una followup, pari al tempo che resta alla finestra di login per una
  followup (il server tiene aperta quella risposta mentre l'utente accede nel
  browser). Prima di questa correzione un server che non rispondeva e non
  chiudeva il socket lasciava il client appeso per sempre, senza eccezione e
  senza una riga di registro.
- Una scadenza si distingue da un socket morto: `ControlTimeoutException`, e una
  followup scaduta è la fine della finestra di login (lo stato resta `NeedLogin`,
  l'URL si può riaprire), non un errore.
- Una netmap che non arriva **mai** si ferma con un errore dopo il tetto dei
  tentativi; una netmap già arrivata e poi persa continua a ritentare senza
  cancellare i peer che l'utente sta guardando.
- `.agents/skills/` con cinque skill (panoramica, funzioni, test e gate, diagnosi
  del control plane, manutenzione) e `tools/check-agents.py`, che verifica ogni
  percorso, simbolo e riga di registro citati: il gate ora fallisce se una skill
  descrive un codice che non esiste più.

**Correzioni al livello telefono (piano 16).** Quattro difetti trovati leggendo il
codice che gira solo sul dispositivo, tutti dello stesso tipo — controlli che non
controllavano e diagnosi che non lasciava traccia:

- il controllo sulla sorgente casuale veniva chiamato e il suo risultato buttato:
  con un generatore che ripete la stessa sequenza l'identità sarebbe stata la
  stessa a ogni avvio, il server avrebbe rifiutato quella node key per sempre e
  l'errore non avrebbe spiegato niente. La decisione è nel core con il suo test e
  l'app **non parte** dicendo perché;
- la verifica del portachiavi non era chiamata da nessuno. Ora si chiama
  all'avvio e il suo esito è usato: se il sistema accetta la scrittura e
  restituisce altro in lettura, il registro dice la conseguenza (l'identità non
  sopravvive al riavvio) invece di fallire in silenzio;
- **la chiave macchina non finisce più in un posto leggibile senza che nessuno lo
  verifichi**: la politica «segreto nel portachiavi, preferenze nelle impostazioni»
  è passata nel core (`VaultBackedStateStore`) con un test che la fissa;
- il registro viveva solo in memoria, quindi ogni guasto sul telefono costava un
debugger collegato. Ora ogni riga va anche in `LocalFolder\registro.log`, con una
coda limitata (400 righe, la politica è nel core con i suoi test) e la procedura
`ISETool.exe ts de|xd 563a8f70-0e06-4690-b075-3963581a75c8 <cartella>` per tirarlo
giù sul PC.

**La prova sul telefono vero (2026-10-11).** L'app è stata eseguita su un
dispositivo WP8.1 reale, e il registro che ora scrive da sola è stato tirato giù
dal dispositivo con `ISETool.exe ts de` (in `C:\temp\registro\IsolatedStore\`).
Quello che il registro mostra, nell'ordine:

- `registro su file: C:\Data\Users\DefApps\...\LocalState\registro.log`: il file
  del registro è scritto davvero sul telefono (piano 16);
- `portachiavi: chiave macchina salvata` due volte e poi `portachiavi: scrittura e
  rilettura verificate`: la verifica del portachiavi è **chiamata** e passa — era
  il controllo che nessuno eseguiva;
- chiavi del server via HTTPS, `ts2021: upgrade accettato su
  http://controlplane.tailscale.com:80`, early payload consumato, e
  `http: /machine/register -> 200` con `machineAuthorized=True`;
- netmap aperta e `stato: Connecting -> Authorized`: la lista peer è viva sul
  dispositivo;
- dopo una chiusura voluta, `canale di controllo: la netmap interrotta (...
  0x800703E3)`: l'abort è riconosciuto e registrato anche sul telefono, e il
  motore è stato riavviato **riusando la stessa node key**, quindi il tailnet
  continua a vedere un solo dispositivo.

Non dimostrati da questa esecuzione: il passo nel browser di un **primo** login
(il dispositivo di quel registro era già autorizzato) e il percorso del piano 17
dopo il tetto dei tentativi (nel registro non compare nessuna riga di budget
esaurito).

**Pin di protocollo:** invariato (`tools/refs.lock`, `tailscale` v1.104.1;
18 costanti verificate).

**Verifiche eseguite:** `cmd //c "tools\check-all.cmd"` verde (agents ok, pin ok,
core desktop, test `ALL PASS`, CLI `selftest`, app ARM con il pacchetto creato,
`CHECK-ALL OK`, exit 0); `handshake` e `probe` contro il control server reale;
`python tools/check-protocol-pin.py` ok; rossi osservati prima di ogni correzione
(il caso della richiesta senza scadenza si presentava come una suite che non
finisce, exit 124 dal tetto di tempo; il piano 16 ha reso rossi i test della
sorgente casuale e del registro prima che le classi esistessero).

**Prova che i quattro test mordono:** tre mutazioni deliberate nelle politiche
appena spostate nel core — `RandomSourceCheck.IsUsable` che restituisce sempre
true, la chiave macchina instradata alle impostazioni invece che al portachiavi, e
il taglio del file disattivato — hanno prodotto **4 fallimenti** nei test del core
(`FAIL caso random: una sorgente che ripete la stessa sequenza e' inutilizzabile`,
`FAIL chiavi/stato: la chiave macchina non finisce nelle impostazioni`, `FAIL
registro: il file si riscrive una volta per taglio, non a ogni riga`, `FAIL
registro: le righe vecchie vengono tagliate`, exit 4). Ripristinate le tre righe,
la stessa suite torna `ALL PASS` con exit 0: i test non passano per costruzione,
passano perché il codice fa la cosa giusta.

**Il motore fermato dal tetto (piano 17).** La traccia incollata dal telefono
(`IOException` con `0x800703E3` -> `Http2Exception` -> `ControlStreamException`) e'
la classe del piano 13, ed e' assorbita: quello che mancava e' cosa succede
**dopo** che il tetto dei tentativi finisce. Il ciclo del client usciva, ma il flag
"avviato" che l'app teneva per conto suo restava acceso: in stato di errore il
pulsante principale chiamava un avvio che usciva subito, e l'app restava bloccata
finche' non la si ammazzava (anche `Suspend()` credeva che il motore fosse in moto).

- la regola sta nel core: `src/Tailscale.Core/Util/EngineRunState.cs` fa girare un
  solo ciclo per volta e libera il motore quando il ciclo esce, anche se esce per
  un'eccezione (il `finally` e' dentro il core, non nell'app); sei casi in
  `src/Tailscale.Core.Tests/EngineRunStateTests.cs`;
- `Tailscale/Services/EngineService.cs` non ha piu' un flag proprio: `IsStarted` e'
  lo stato del ciclo, `Suspend()` e `Resume()` leggono la verita', un secondo
  avvio mentre il ciclo gira viene rifiutato e lo dice, e il ciclo del client non
  gira piu' su un terzo thread;
- i due messaggi terminali non promettono piu' un tentativo che nessuno fa:
  dicono che il motore si e' fermato e si puo' riavviare, e i test fissano
  entrambe le meta' della promessa.

**Verifiche del piano 17:** gate verde (`CHECK-ALL OK`, exit 0: skill, pin, risorse,
anteprima e OCR, test `ALL PASS`, CLI `selftest`, app ARM con il pacchetto); i due
casi dei messaggi visti rossi prima della correzione (`FAILURES: 4`, exit 4) e
verdi dopo; mutazione del `finally` (il ciclo non libera piu' il motore) ->
`FAILURES: 10`, exit 10, ripristino byte a byte dal backup del workspace e
`ALL PASS` con exit 0.

**Limite del piano 17:** la prova sul pulsante richiede un telefono (qui non c'e'
nemmeno l'emulatore): resta la voce mensile di `.agents/MAINTENANCE.md`. La finestra
fra `Start()` e `Suspend()` nello stesso giro della UI thread resta com'e' (rara,
esito benigno) ed e' dichiarata nel piano.

**Limiti noti:** la verifica su un telefono vero resta da fare (nessun emulatore
né dispositivo in questo ambiente, come nei piani precedenti); le scadenze sono
provate su stream in memoria, dove finiscono con una lettura a zero byte invece
che con l'abort `0x800703E3` che il telefono produce. Gli adattatori WinRT
(`SocketStream`, connettore, sorgente casuale, fetcher, portachiavi, impostazioni,
file del registro) restano senza test automatici perché le loro API esistono solo
sul telefono: da loro è uscita la parte che decide, e quella ha i test; la
chiamata la verifica la build ARM.
