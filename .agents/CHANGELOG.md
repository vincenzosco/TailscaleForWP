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

**Limiti noti:** la verifica su un telefono vero resta da fare (nessun emulatore
né dispositivo in questo ambiente, come nei piani precedenti); le scadenze sono
provate su stream in memoria, dove finiscono con una lettura a zero byte invece
che con l'abort `0x800703E3` che il telefono produce. Gli adattatori WinRT
(`SocketStream`, connettore, sorgente casuale, fetcher, portachiavi, impostazioni,
file del registro) restano senza test automatici perché le loro API esistono solo
sul telefono: da loro è uscita la parte che decide, e quella ha i test; la
chiamata la verifica la build ARM.
