# Manutenzione — cosa si controlla, quando

Ogni voce ha il **comando esatto** e la data dell'ultima esecuzione. Quando si
esegue una voce, si aggiorna la data: è l'unico modo per sapere se una procedura
è ancora viva. `tools/check-agents.py` verifica che ogni voce abbia una data
valida e che il comando citato esista ancora.

La procedura completa, con il perché di ogni voce, è nella skill
`wp81-tailscale-maintenance`.

## Mensile

- [ ] Gate completo, dal repository pulito: `cmd //c "tools\check-all.cmd"`
      — Ultima esecuzione: 2026-10-11
- [ ] Prova end-to-end su un telefono vero: deploy Debug, accesso, app in primo
      piano qualche minuto, passaggio in secondo piano e ritorno. Poi il registro
      dal dispositivo, invece di copiarlo a mano dallo schermo:
      `ISETool.exe ts de 563a8f70-0e06-4690-b075-3963581a75c8 C:\temp\registro`
      (per l'emulatore `ts xd`, stesso product id; per sapere cosa è collegato
      `ISETool.exe EnumerateDevices`). Lo strumento non è nel repository: sta
      nell'SDK, in `IsolatedStorageExplorerTool` dentro `Microsoft SDKs\Windows Phone\v8.1\Tools`
      — Ultima esecuzione: non ancora eseguita

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
      — Ultima esecuzione: 2026-10-11
- [ ] Le skill citano codice che esiste: `python tools/check-agents.py`
      — Ultima esecuzione: 2026-10-11
- [ ] Una voce in `.agents/CHANGELOG.md` con cosa è cambiato e quali verifiche
      sono state eseguite — Ultima esecuzione: 2026-10-11

## Quando una voce non si può eseguire

Se una voce richiede un telefono o un servizio che non c'è, la data **non** va
aggiornata: si scrive `non ancora eseguita` e si dice perché. Una data falsa
nella prima riga è il modo più rapido per rendere inutile tutto il resto.
