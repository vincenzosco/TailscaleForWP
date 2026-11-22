# Manutenzione — cosa si controlla, quando

Ogni voce ha il **comando esatto** e la data dell'ultima esecuzione. Quando si
esegue una voce, si aggiorna la data: è l'unico modo per sapere se una procedura
è ancora viva. `tools/check-agents.py` verifica che ogni voce abbia una data
valida e che il comando citato esista ancora.

La procedura completa, con il perché di ogni voce, è nella skill
`wp81-tailscale-maintenance`.

## Mensile

- [ ] Gate completo, dal repository pulito: `cmd //c "tools\check-all.cmd"`
      — Ultima esecuzione: 2026-10-12
- [ ] Prova end-to-end su un telefono vero: deploy Debug, accesso, app in primo
      piano qualche minuto, passaggio in secondo piano e ritorno. Poi il registro
      dal dispositivo, invece di copiarlo a mano dallo schermo:
      `ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro`
      (per l'emulatore `ts xd`, stesso product id; per sapere cosa è collegato
      `ISETool.exe EnumerateDevices`). Lo strumento non è nel repository: sta
      nell'SDK, in `IsolatedStorageExplorerTool` dentro `Microsoft SDKs\Windows Phone\v8.1\Tools`
      — Ultima esecuzione: 2026-10-11, su un dispositivo vero. Il registro è stato
      tirato giù con `ISETool.exe ts de` e mostra: registro su file scritto nella
      cartella dell'app, portachiavi salvato e riletto identico, handshake ts2021
      contro `controlplane.tailscale.com`, `http: /machine/register -> 200` con il
      nodo autorizzato, netmap aperta e stato `Authorized`, e l'abort `0x800703E3`
      riconosciuto e registrato dopo una chiusura voluta. Il motore è stato
      riavviato **con la stessa node key**: un solo dispositivo nel tailnet.
      Resta non dimostrato il passo nel browser di un primo login (il dispositivo
      di quel registro era già autorizzato).
      Lo stesso giorno, dopo la correzione della schermata nera, la procedura è
      stata ripetuta: `AppDeployCmd.exe /installlaunch <appx> /targetdevice:de
      /force:INTERNAL` e poi `/launch <productId> /targetdevice:de`, con lo
      schermo del dispositivo **sbloccato** — senza, il comando risponde
      `Avvio dell'applicazione non riuscito. Verificare che lo schermo del
      dispositivo sia sbloccato`, e senza app avviata non c'è registro da
      leggere. Quella corsa non ha nessuna riga `log:eccezione non gestita`.
      Con il nodo autorizzato (accesso completato nel browser) la stessa corsa ha
      attraversato anche il percorso con i dispositivi: `machineAuthorized=True`,
      stream `/machine/map` aperto con 26 KB di netmap, `stato: Connecting ->
      Authorized`, nessuna eccezione, e sullo schermo l'intestazione `Dispositivi`
      con il conteggio e le sue righe e il pannello del registro pieno.
      Il pacchetto usato è quello del gate (build ARM Release), non una build
      Debug da Visual Studio. Lo strumento di lancio ogni tanto fallisce da solo
      (`0xC0000005`, oppure `0x87B20C0A`): ritentare, di solito al secondo
      tentativo passa.
      Il ritorno in primo piano che arriva mentre il ciclo precedente sta uscendo
      (il caso visto il 2026-10-11) non deve più lasciare l'app ferma su
      "Accesso richiesto": la richiesta di avvio aspetta che il motore si liberi,
      e lì il pulsante principale rimette comunque in moto un motore fermo. Nel
      registro **non** deve comparire la riga
      `log:il motore e' gia' in moto: nessuna seconda partenza`; se compare, è il
      caso da riportare insieme alle righe che la precedono.
      La stessa corsa si fa **sull'emulatore**, e per il passaggio in secondo
      piano non serve un account Tailscale: `AppDeployCmd.exe /installlaunch
      <appx x86 del pacchetto> /targetdevice:xd` (avvia da sé l'emulatore
      predefinito se non è in esecuzione: il primo tentativo può rispondere
      `0x80131622`, e basta ritentare), poi di nuovo `/launch <productId>
      /targetdevice:xd` per riportare l'app in primo piano, e il registro si tira
      giù con `ISETool.exe ts xd`. Corsa del 2026-10-11 (nessun account, nodo non
      autorizzato): 100 righe, `stato: Connecting -> NeedLogin`, `log:arresto del
      motore` al passaggio in secondo piano, `log:avvio del motore` al ritorno con
      l'URL salvato e di nuovo `log:browser aperto sull'URL di login` — nessun
      rifiuto e nessuna `log:eccezione non gestita`.
      Lo stesso giorno la corsa è stata ripetuta sul **telefono vero** con il
      pacchetto ARM del gate: 162 righe, **0 rifiuti e 0
      `log:eccezione non gestita`**, e il varco centrato due volte — la riga
      `log:il motore e' ancora in uscita: aspetto che si liberi` compare due volte,
      sempre dopo `stato: NeedLogin -> Stopped` e sempre seguita da
      `log:avvio del motore`. Il terzo ciclo è arrivato fino in fondo al **primo
      login** fatto nel browser: `machineAuthorized=True`, nessun URL, netmap
      aperta su `/machine/map` e `stato: Connecting -> Authorized`. Da qui in poi
      la voce mensile può contare sul fatto che l'accesso di un primo login è
      stato dimostrato almeno una volta (2026-10-11).
      Il 2026-10-12 la stessa prova è stata ripetuta sullo stesso telefono con il
      pacchetto del gate, e ha lasciato due cose da sapere. **Installare il
      pacchetto azzera l'identità salvata**: `AppDeployCmd.exe /installlaunch` ha
      riportato l'app a un primo avvio (node key nuova, risposta di registrazione
      con `authURL=si`, browser aperto da solo una volta), mentre un semplice
      `/launch` sulla stessa installazione riusa la node key e la lascia
      autorizzata — per non farsi riaccedere si usa `/launch`, non
      `/installlaunch`. L'autorizzazione si è poi chiusa da sola con il nome
      dell'account già noto, quindi in quel giro non è stato scritto nessun
      credenziale. **E il rifiuto `log:il motore e' gia' in moto: nessuna seconda
      partenza` non è di per sé un guasto**: attivare l'app più volte mentre un
      ciclo è vivo fa arrivare più richieste di avvio, e il rifiuto è la risposta
      voluta (due client sullo stesso stato si contenderebbero la stessa chiave).
      La differenza sta in ciò che segue: se il ciclo è vivo la netmap continua ad
      arrivare e lo stato resta `Authorized`, mentre il guasto del 2026-10-11
      lasciava l'app **senza** motore. In quella corsa le tre righe di rifiuto sono
      in coda a uno stato `Authorized` e l'app è rimasta collegata; un solo
      `/launch`, ripetuto come controllo, non ne produce nessuna.

## Trimestrale

- [ ] Leggere il changelog di Tailscale e decidere se aggiornare il pin
      (`tools/refs.lock`) — Ultima esecuzione: 2026-10-11
- [ ] Il control server risponde come prima:
      `src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake`
      — Ultima esecuzione: 2026-10-11
- [ ] Le costanti di protocollo corrispondono ancora al sorgente venduto:
      `python tools/check-protocol-pin.py` — Ultima esecuzione: 2026-10-11

## Semestrale

- [ ] Rileggere i "Limiti noti" di `README.md` e togliere quelli che non sono
      più veri — Ultima esecuzione: 2026-10-11
- [ ] Rileggere `docs/protocol-notes.md` e confermare che i riferimenti a file e
      riga del sorgente venduto siano ancora corretti
      — Ultima esecuzione: 2026-10-11

## A ogni modifica

- [ ] Nessuna emoji o freccia decorativa: `python tools/check-no-emoji.py`
      — Ultima esecuzione: 2026-10-12
- [ ] Le skill citano codice che esiste: `python tools/check-agents.py`
      — Ultima esecuzione: 2026-10-12
- [ ] Un testo che finisce in un TextBlock non è nullo (un TextBlock non lo
      accetta, e l'eccezione arriva sul thread della UI):
      `python tools/check-ui-null-text.py` — Ultima esecuzione: 2026-10-12
- [ ] Una voce in `.agents/CHANGELOG.md` con cosa è cambiato e quali verifiche
      sono state eseguite — Ultima esecuzione: 2026-10-12

## Quando una voce non si può eseguire

Se una voce richiede un telefono o un servizio che non c'è, la data **non** va
aggiornata: si scrive `non ancora eseguita` e si dice perché. Una data falsa
nella prima riga è il modo più rapido per rendere inutile tutto il resto.
