---
name: wp81-tailscale-maintenance
description: Usa quando fai manutenzione periodica, aggiorni il sorgente pinnato di Tailscale, o il gate non gira da un po' — il calendario verificabile e la procedura di bump.
---

# Manutenzione: cosa si controlla, quando, e come si aggiorna il pin

Il calendario vive in `.agents/MAINTENANCE.md`, con una riga per voce: comando
esatto e `Ultima esecuzione: YYYY-MM-DD`. La data non è decorazione: senza,
nessuno sa quando una voce è stata eseguita l'ultima volta, e una procedura che
nessuno esegue è peggio di nessuna procedura. Il controllo
`tools/check-agents.py` verifica che ogni voce abbia la data e che il comando
esista ancora.

## Le cadenze

- **Mensile** — il gate `cmd //c "tools\check-all.cmd"` e la verifica su un
  telefono vero: servono anche a scoprire una rottura **dall'altra parte**, cioè
  un cambiamento del server Tailscale o del certificato, che nessun test locale
  può prevedere.
- **Trimestrale** — leggere il changelog di Tailscale e decidere se aggiornare
  il pin; verificare che il certificato del control server non sia cambiato; se
  il server ha cambiato qualcosa di osservabile, la CLI lo dice:
  `src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake`.
- **Semestrale** — rileggere i "Limiti noti" del `README.md` e confermare che
  siano ancora veri. Un limite non più vero è un bug della documentazione: va
  tolto, non lasciato lì perché "tanto è vecchio".
- **A ogni modifica** — `python tools/check-no-emoji.py` e
  `python tools/check-agents.py` (che girano comunque nel gate), e una voce in
  `.agents/CHANGELOG.md`.

## Aggiornare il pin: l'ordine non è negoziabile

Il pin vive in `tools/refs.lock` (repo, tipo di riferimento, `ref`, `sha256`), il
sorgente viene materializzato in `refs/` da `tools/fetch-refs.py`, e le costanti
di protocollo sono confrontate con quel sorgente da
`tools/check-protocol-pin.py`. Da qui l'ordine:

1. Leggi il changelog upstream e cerca cambi a `controlbase`, `ts2021`,
   `controlhttp`, `tailcfg`. Se non ce ne sono, il bump può aspettare.
2. Aggiorna `tools/refs.lock` e materializza: `python tools/fetch-refs.py`.
3. Fai **fallire** il controllo, di proposito: `python
   tools/check-protocol-pin.py`. Se il sorgente è cambiato, questo comando è il
   punto in cui te ne accorgi, **prima** dei test. Leggi il mismatch.

   Attenzione a cosa significa "farlo fallire": lo script confronta le costanti
   `ProtocolConstants` con il **sorgente dentro `refs/`**, non con la stringa
   `ref` di `tools/refs.lock`. Quindi cambiare solo il `ref` nel lock **non** fa
   fallire niente — il fallimento arriva dopo `tools/fetch-refs.py`, quando in
   `refs/` c'è davvero il sorgente nuovo. Per verificare che il controllo sia
   ancora vivo, il modo onesto è modificare per un momento un valore nel
   sorgente venduto (per esempio `msgTypeRecord` in
   `refs/tailscale/control/controlbase/messages.go`), leggere il `[FAIL]` con il
   valore atteso, e rimettere il file com'era.
4. Aggiorna `src/Tailscale.Core/Protocol/ProtocolConstants.cs` e
   `docs/protocol-notes.md` (ogni valore con file e riga del sorgente venduto).
5. `cmd //c "tools\check-all.cmd"` verde, compresa la build ARM.
6. Se la funzione tocca la connettività, verifica su un telefono vero.
7. Aggiorna `.agents/CHANGELOG.md` con data, pin e verifiche eseguite.

Se un passo fallisce si torna indietro: **niente pin a metà**, perché un pin
aggiornato a metà è un repository in cui nessuno sa più quale sorgente descrive
il codice.

## La regola che tiene insieme tutto

Il codice è la fonte della verità; README, skill e calendario lo seguono, non il
contrario. Quando qualcosa non torna, la domanda giusta non è "come aggiorno il
documento perché passi" ma "cosa è cambiato davvero nel codice".

Ultima verifica: 2026-10-11
