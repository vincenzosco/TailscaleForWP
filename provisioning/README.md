# Provisioning via interop

Qui sta cio' che serve per il collegamento "VPN via interop" fuori dall'app: lo
script di discovery, il generatore del payload per CMD Injector, e le regole che
vale sia per l'uno che per l'altro.

**Il registro non crea alcun tunnel.** Qui si persiste configurazione e si punta
il collegamento di sistema: il traffico del telefono ci passa solo se qualcosa lo
usa. E' la stessa distinzione del design doc, e va detta ogni volta per non
illudere nessuno.

## I due canali, stesso manifest

| Canale | Chi lo usa | Quando |
|---|---|---|
| L'app (telnet a `127.0.0.1:23`, oppure registro diretto via P/Invoke) | l'utente, dal pulsante nella pagina | tutto in una volta: aggiunge, attiva, disattiva, conferma |
| `provision.bat` / `deprovision.bat` | l'utente, da CMD Injector o da una shell via telnet | quando l'app non ha i diritti, o per il provisioning iniziale |

Gli stessi valori, nello stesso ordine: il manifest. Due posti dove si legge, e
una sola fonte di verita'.

## Il manifest

Formato: una voce per riga, `hive|path|nome|tipo:valore` (tipo: `sz` oppure
`dword`), con tre sezioni: `[connection]` aggiunge la connessione, `[on]` la
attiva, `[off]` la disattiva. Le righe con `#` sono commenti.

L'app legge `vpn-manifest.txt` nella sua cartella se c'e' (lo si copia dentro con
`ISETool.exe`), altrimenti usa quello integrato. La CLI legge un file passato
come argomento.

La whitelist e' chiusa, e il codice rifiuta il resto con il path nel messaggio:

| Radice ammessa | Per cosa |
|---|---|
| `HKLM\SOFTWARE\TailscaleWP` | la chiave dell'app |
| `HKCU\Software\TailscaleWP` | la stessa, per i test sul desktop |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings` | il proxy di sistema, il collegamento documentato su WP8.1 |

Le chiavi del profilo VPN di sistema **non** sono in whitelist: nessuno le ha
ancora verificate su un telefono. Quando `discovery.bat` le porta in luce e
l'evidenza sta in `docs/wp81-registry-findings.md`, la whitelist si allarga con
quella evidenza, non prima.

## La discovery (una volta, sul telefono)

1. Copiare `discovery.bat` sul telefono ed eseguirlo dal CMD Injector (o
   incollarlo in una shell via telnet).
2. Copiare l'output integrale in `docs/wp81-registry-findings.md`.
3. Per ogni elemento: path reale, valore di esempio, e se il framework lo
   rispetta (con la prova: scrittura + lettura + comportamento).

Lo script e' **solo lettura**: `reg query` e basta, e lo dice anche a schermo.

## Il payload per CMD Injector

```
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe provision
```

Scrive in `provisioning\out\`: `provision.bat`, `deprovision.bat`. Con un
argomento si usa un manifest proprio, con due si sceglie la cartella:

```
src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe provision mio-manifest.txt out
```

Cosa fanno i due file, e perche':

- **backup prima della scrittura.** Ogni valore gia' presente finisce in
  `backup-<data>.txt` (accanto ai due file) **prima** di essere toccato, nella
  forma `hive|path|nome|tipo:valore`, con `assente` per i valori che non c'erano.
- **idempotenza.** `reg add /f` non fallisce se il valore c'e' gia': rieseguire
  `provision.bat` lascia lo stato identico alla prima esecuzione.
- **verifica finale.** Ogni valore scritto si rilegge con `reg query`: il batch
  non dichiara fatto quello che non ha visto.
- **deprovision da ogni stato.** `deprovision.bat` ripristina cio' che trova nel
  backup e non presume che tutte le scritture siano avvenute: funziona anche dopo
  un provisioning interrotto a meta'.

## Avvertenza sul proxy di sistema

Puntare `ProxyServer` a `127.0.0.1:<porta>` quando nessuna porta e' in ascolto
**spegne la rete del telefono**: tutte le app che rispettano il proxy di sistema
si ritrovano senza strada. Per questo le righe del proxy nel manifest di default
sono commentate: si decommentano quando c'e' davvero un proxy locale in ascolto,
e si commentano di nuovo prima di toglierlo.

## Il rollback

- Dall'app: il pulsante della pagina disattiva il collegamento, e `Revert()`
  riporta ogni valore come era prima della prima scrittura (assenze comprese).
- Da CMD Injector: `deprovision.bat`.
- Da zero: i valori stanno in `backup-<data>.txt`, leggibili a mano.