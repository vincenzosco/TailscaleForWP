# Agents — skill per lavorare su TailscaleForWP

Questa cartella contiene le skill che servono a **mantenere l'app, aggiungerle
funzioni e leggerne i guasti** senza rileggere il repository da zero. Servono a
due lettori: una persona che arriva sul progetto per la prima volta, e un agente
che riceve un compito e non ha visto né il codice né i piani.

## Indice

| Skill (`name`) | Quando si attiva | Cosa risolve |
|---|---|---|
| `wp81-tailscale-overview` | qualunque lavoro in questo repository | la mappa dei file, i vincoli che decidono il codice (C# 5, zero NuGet, profilo WP8.1), il gate unico |
| `wp81-tailscale-feature` | "aggiungi una funzione / cambia il comportamento dell'app" | la checklist end-to-end: piano, test prima, core, collegamento all'app, risorse, README, gate |
| `wp81-tailscale-tests-and-gate` | "esegui i test / aggiungi un test" | il runner in casa, gli attrezzi finti, come si osserva un RED onesto |
| `wp81-tailscale-control-plane-debug` | "l'app non si connette / c'e' un'eccezione nel Registro" | il catalogo sintomo -> significato -> azione, con la traccia `0x800703E3` e le righe di registro |
| `wp81-tailscale-maintenance` | "manutenzione periodica / aggiorna il pin / il gate e' vecchio" | il calendario e la procedura di aggiornamento del sorgente pinnato |

## Come si caricano

- Con il tool `skill`, **per nome**: il nome è lo slug della cartella
  (`.agents/skills/<slug>/SKILL.md`), quindi `skill wp81-tailscale-control-plane-debug`.
- Con `npx skills add`, che installa e legge da `.agents/skills/`.

Nessuna skill è scritta per essere letta come documento: sono istruzioni, con i
comandi da copiare e i file da aprire.

## La regola che le tiene vere

Ogni percorso, comando, simbolo (citato come `<percorso>/<file>.cs#<Membro>`) e
riga di registro (marcata `log:<testo>`) citato qui dentro è verificato dal
codice vero da
`tools/check-agents.py`, che gira dentro `tools\check-all.cmd` prima dei test.

Quindi: **se cambi il codice, aggiorna la skill.** Se l'aggiornamento manca, il
gate fallisce e dice quale skill e quale riga è diventata falsa. È l'unico modo
che una documentazione del genere non marcisca in silenzio — una skill che cita
un file rinominato o una riga di registro cambiata è peggio di nessuna skill.

Ogni skill porta in fondo la riga `Ultima verifica: YYYY-MM-DD`, il giorno in
cui i suoi comandi sono stati eseguiti davvero nella forma in cui sono scritti.

## Cosa queste skill non possono fare

Nessuna di esse sostituisce **la verifica su un telefono vero**: in questo
ambiente non c'è né emulatore né dispositivo, quindi ciò che si prova è la
catena su stream in memoria. La procedura di verifica sul dispositivo (deploy,
percorso di accesso, lettura delle ultime righe del Registro) è descritta nel
piano `docs/superpowers/plans/2026-10-11-wp81-tailscale-14-request-deadlines.md`,
Task 4, e resta l'unica prova del caso reale.
