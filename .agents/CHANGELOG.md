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

**Pin di protocollo:** invariato (`tools/refs.lock`, `tailscale` v1.104.1;
18 costanti verificate).

**Verifiche eseguite:** `cmd //c "tools\check-all.cmd"` verde (core desktop,
test `ALL PASS`, CLI `selftest`, app ARM); `handshake` e `probe` contro il
control server reale; `python tools/check-protocol-pin.py` ok; rossi osservati
prima di ogni correzione (il caso della richiesta senza scadenza si presentava
come una suite che non finisce, exit 124 dal tetto di tempo).

**Limiti noti:** la verifica su un telefono vero resta da fare (nessun emulatore
né dispositivo in questo ambiente, come nei piani precedenti); le scadenze sono
provate su stream in memoria, dove finiscono con una lettura a zero byte invece
che con l'abort `0x800703E3` che il telefono produce.
