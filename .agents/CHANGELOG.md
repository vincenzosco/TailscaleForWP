# Changelog

Formato: `## YYYY-MM-DD — <titolo>`, con "cosa è cambiato", "pin di protocollo",
"verifiche eseguite" e "limiti noti". La voce si scrive quando il lavoro è
finito, non quando è iniziato: le verifiche eseguite sono parte del contenuto.

## 2026-10-12 — Audit su tutta l'app: quattro difetti e le prove che mordono

**Cosa è cambiato**

Un giro di revisione su tutto il codice (crypto, protocollo, canale di
controllo, stato e chiavi, CLI e app) ha prodotto quattro correzioni, ognuna con
un test che *fallisce* se la correzione viene tolta.

- **Il canale di controllo si chiude quando il ciclo del motore esce da solo**
  (`ControlClient.Start`, il `finally` che mancava). Era il difetto gia' nel
  rapporto del 2026-10-12 come BUG-2 e mai corretto: quando l'uscita non passava
  da `Stop()` — netmap mai arrivata dopo il tetto dei tentativi, oppure canale
  sempre rotto — nessuno chiudeva la connessione Noise/HTTP2, e ogni ciclo andato
  a vuoto lasciava aperti un socket e uno stream WinRT. Il canale si chiude nel
  `finally` di `Start()` e non in `Stop()` perche' `Stop()` porta lo stato a
  `Stopped`, mentre l'uscita per budget deve lasciare visibile l'errore che la
  spiega (`netmap non disponibile: ...`).
- **`END_STREAM` non si perde piu' quando il blocco di header e' spezzato.** In
  `src/Tailscale.Core/Protocol/Http2Connection.cs` il flag veniva letto solo sul
  ramo con `END_HEADERS`; se il server spezzava il blocco su una `CONTINUATION`,
  il flag — che sta sull'`HEADERS` e la `CONTINUATION` non ripete — spariva, e il
  corpo non finiva mai: chi leggeva restava appeso su dati che non arrivavano.
  Ora il flag si legge sull'`HEADERS` e si applica quando il blocco e' completo.
- **Lo scrittore JSON separa gli elementi di un array** (`JsonWriter.Raw` in
  `src/Tailscale.Core/Tailcfg/TailcfgTypes.cs`, ora con `Separate()`). Prima due
  elementi scritti di fila producevano `["a""b"]`, che non e' JSON: il caso reale
  e' `MapRequest.Endpoints` con piu' di un indirizzo. La richiesta sarebbe stata
  rifiutata con un 400 che non spiega nulla.
- **Un intero HPACK che trabocca e' un errore di codifica, non un indice**
  (`HpackDecoder.ReadInteger` in `src/Tailscale.Core/Protocol/Hpack.cs`).
  L'accumulatore era un `int`: la continuazione a shift 28 faceva traboccare il
  valore, che tornava negativo e finiva su `Names[negativo]`, cioe' un'
  `IndexOutOfRangeException` che nessun chiamante sa leggere. Ora l'accumulatore
  e' un `long`, il valore oltre `int.MaxValue` e' rifiutato con `HpackException`,
  e `GetIndexed` rifiuta anche gli indici negativi.

**Pin di protocollo**

Nessuna costante di protocollo e' cambiata: `tools/check-protocol-pin.py` verde.

**Verifiche eseguite**

- `cmd //c "tools\check-all.cmd"` verde: `CHECK-ALL OK`, exit 0 (pin di
  protocollo, documentazione, segreti, emoji, risorse e testo della UI non nullo,
  skill degli agent, anteprima e OCR della UI — `OCR OK: 24 stringhe lette su 6
  immagini` —, core, test `ALL PASS`, CLI `CLI SELFTEST OK`, app WP8.1 ARM
  costruita).
- **Prove che i test mordono** (mutazione del solo difetto, poi ripristino
  verificato con `cmp`, e ogni volta ricostruzione ed esecuzione):
  - senza il `finally` in `Start()` -> **1 fallimento** ("ciclo uscito da solo: il
    canale viene chiuso");
  - senza la lettura di `END_STREAM` sul blocco spezzato -> **1 fallimento**
    (`EndOfStreamException` nel test `continuation + END_STREAM`);
  - senza `Separate()` in `Raw` -> **2 fallimenti** (virgola mancante e
    `JsonParseException`);
  - con `ReadInteger` a 32 bit e `GetIndexed` con il vecchio `index == 0` ->
    **1 fallimento** (`IndexOutOfRangeException`), cioe' esattamente il guasto
    descritto sopra.
- Un caso nuovo per ognuna delle quattro correzioni: il test
  `ChannelIsReleasedWhenTheCycleGivesUp` in `ControlClientStateTests` (con
  l'aiuto `RunChannelSwap` corretto, che si fermava *prima* che il ciclo notasse
  il canale rotto, e faceva arrivare la riga solo riusando per errore il canale
  del ciclo precedente), `ContinuationKeepsEndStream` in `Http2Tests`,
  `RejectsOverlongInteger` in `HpackTests`, e il nuovo file `JsonWriteTests`
  (registrato in `Program.cs`).

**Limiti noti**

- La revisione e' di sorgente: senza una sandbox a livello di sistema non e'
  stato eseguito codice del bersaglio oltre i test del repository (che girano
  offline). Non e' una prova di assenza di difetti; e' la stessa avvertenza che
  il rapporto di bug hunter porta per costruzione.
- **Nessun data plane** (limite dichiarato del README, invariato): `Connesso`
  significa control plane a posto, non traffico fra peer.
- La crypto e' scritta a mano, ma i test la confrontano con i vector di RFC 7748,
  RFC 8439, RFC 7693 e con TweetNaCl, e i percorsi dipendenti dal segreto usano
  scambi a tempo costante (`Sel25519`, `ConstantTimeEquals`); non e' stata scritta
  una nuova analisi di canale laterale.

## 2026-10-12 — Il pulsante diceva "Avvia" e fermava il motore

**Cosa è cambiato**

- L'etichetta e l'azione del pulsante principale erano la **stessa condizione
  scritta due volte**, in due punti di `Tailscale/MainPage.xaml.cs`: l'etichetta in
  `Refresh` (diceva "Avvia" in ogni stato tranne quello del login), l'azione in
  `OnPrimaryButtonClick` (in `Connecting` e `Authorized` chiamava `_engine.Stop()`).
  Il motore però parte da solo in `OnNavigatedTo`, quindi lo stato che si trova
  appena aperta l'app è proprio quello: il tocco con cui si crede di collegarsi era
  quello che scollegava, e l'app restava su "Fermo". Le due stringhe che dovevano
  far combaciare le cose — `StopButton.Content` ("Ferma") e
  `ReopenLoginButton.Content` ("Riapri accesso") — erano nei `.resw` di entrambe le
  lingue e **nessun file di codice le leggeva**.
- La decisione è ora **una sola** e sta nel core: `Decide` in
  `src/Tailscale.Core/Util/PrimaryButtonPolicy.cs` restituisce `Login`, `Restart`,
  `Stop` o `Start`, e `Tailscale/MainPage.xaml.cs#Refresh` la usa per l'etichetta
  mentre `Tailscale/MainPage.xaml.cs#OnPrimaryButtonClick` la esegue.
  `Tailscale/MainPage.xaml.cs#LabelKey` traduce l'azione nella stringa: "Ferma"
  quando il tocco ferma, "Avvia" quando avvia, "Accedi" quando apre la pagina di
  accesso (anche per il riavvio: quello che l'utente vede è il browser che si
  riapre da solo). Nel core perché è lì che questo repository mette le regole, con
  il loro test (`src/Tailscale.Core/Util/EngineRunState.cs` è il precedente).
- Gli strumenti sono allineati: `StopButton/Content` è entrata fra le chiavi che
  `tools/check-ui-resources.py` pretende in entrambe le lingue, e anteprima e OCR
  coprono ora **tre** schermate invece di due (`needLogin`, `authorized`,
  `stopped`), così tutte e tre le etichette del pulsante restano verificate come
  leggibili.

**Verifiche eseguite:** `cmd //c "tools\check-all.cmd"` verde (`CHECK-ALL OK`, exit
0): pin, emoji, risorse della UI (12 chiavi), testo della UI non nullo, `agents
ok`, anteprima di 3 schermate per 2 lingue, `OCR OK: 24 stringhe lette su 6
immagini`, test `ALL PASS`, CLI `SELFTEST OK`, app ARM e pacchetto creati.

**Prova che il test morde:** `Decide` mutato al comportamento vecchio (in
`Connecting` e `Authorized` restituisce `Start` invece di `Stop`) ha prodotto
**4 fallimenti** con exit 4; il ripristino verificato con `cmp` è tornato
`ALL PASS` con exit 0.

**Prova sul telefono vero (2026-10-12), senza riaccedere:** col pacchetto del gate
l'app parte da sola e arriva in `stato: Connecting -> Authorized` con
`machineAuthorized=True` e la netmap aperta su `/machine/map` — 164 righe, nessuna
`log:eccezione non gestita`.

**Limiti noti:** l'etichetta sullo schermo del telefono è un dato per gli occhi
dell'utente: qui sono verificati la decisione (test), la leggibilità delle tre
parole nelle due lingue (OCR) e la corsa dell'app; non i pixel del dispositivo.
Installare il pacchetto `AppDeployCmd.exe /installlaunch` **azzera l'identità
salvata** (nodo nuovo, un giro di autorizzazione nel browser che si chiude da solo
perché la sessione è già nel browser): per non farsi riaccedere si usa `launch`, non
`installlaunch`. Nella stessa corsa compaiono tre `log:il motore e' gia' in moto:
nessuna seconda partenza` in coda a uno stato `Authorized`: sono attivazioni
ripetute dell'app durante il deploy mentre un ciclo era vivo, il rifiuto è voluto,
e la netmap ha continuato ad arrivare (un solo `launch`, ripetuto come controllo,
non ne produce nessuno).

## 2026-10-11 — Il riavvio che non si perde più, e la corsa sull'emulatore senza account

**Cosa è cambiato**

- La richiesta di avvio del motore **non si perde più** se arriva mentre il ciclo
  precedente sta uscendo: `RunWhenFree` in
  `src/Tailscale.Core/Util/EngineRunState.cs` aspetta che il posto si liberi e
  riprova (margine di secondi, da `Tailscale/Services/EngineService.cs#Start`),
  invece di rispondere di no e lasciar cadere la richiesta. La prima attesa si
  registra (`log:il motore e' ancora in uscita: aspetto che si liberi`), cosi' dal
  registro si distingue un avvio che entra subito da uno che ha aspettato. È il
  caso visto su un
  telefono vero il 2026-10-11: il ritorno in primo piano arrivava mentre il ciclo
  precedente usciva, il riavvio veniva rifiutato
  (`log:il motore e' gia' in moto: nessuna seconda partenza`) e la richiesta
  spariva; in `NeedLogin` il pulsante apriva soltanto il browser, quindi l'app
  restava su "Accesso richiesto" con il motore spento.
- In `NeedLogin` il pulsante principale rimette in moto un motore fermo
  (`Tailscale/MainPage.xaml.cs#OnPrimaryButtonClick`): un motore spento non ha
  nessun URL da riaprire, e quello era il modo di restare fermi.
- Il browser si apre da solo **una volta per URL di login**, non più una volta per
  vita della pagina (`Tailscale/MainPage.xaml.cs#Refresh`): un motore riavviato
  riporta lo stesso "Accesso richiesto", e la pagina di accesso si riapre da sola.

**Le prove, senza account**

- `src/Tailscale.Core.Tests/EngineRunStateTests.cs` ha tre casi nuovi: con il
  motore libero non si aspetta, l'avvio che aspetta entra appena il posto si
  libera, e dopo i tentativi previsti si arrende lasciando in pace il ciclo in
  moto. Prova che mordono: `RunWhenFree` ridotto a un `Run` secco (nessuna attesa)
  ha prodotto **5 fallimenti** con exit 5, e il ripristino byte a byte (verificato
  con `cmp`) è tornato `ALL PASS` con exit 0.
- Corsa sull'**emulatore WP8.1** (x86; `AppDeployCmd.exe /installlaunch <appx>
  /targetdevice:xd` e registro tirato giù con `ISETool.exe ts xd`), **senza
  account Tailscale**: il nodo resta non autorizzato
  (`machineAuthorized=False, authURL=si`), quindi la corsa si ferma a `NeedLogin`
  — che è tutto quello che serve per guardare il motore fermarsi e ripartire. 100
  righe: `stato: Connecting -> NeedLogin`, `log:arresto del motore` al passaggio
  in secondo piano, secondo `log:avvio del motore` al ritorno con `stato: c'e' un
  URL di login salvato da un avvio precedente`, e di nuovo
  `log:browser aperto sull'URL di login` (è la riapertura automatica della
  correzione). **0 rifiuti, 0 `log:eccezione non gestita`.**
- Corsa sul **telefono vero**, con il pacchetto ARM del gate (`/installlaunch ...
  /targetdevice:de /force:INTERNAL`; lo strumento di lancio fallisce da solo ogni
  tanto con `0xC0000005` e basta ritentare): 162 righe di registro, **0 rifiuti e
  0 `log:eccezione non gestita`**. La finestra è stata centrata **due volte**: la
  riga nuova `log:il motore e' ancora in uscita: aspetto che si liberi` sta alle
  righe 51 e 102, sempre subito dopo `stato: NeedLogin -> Stopped` e sempre seguita
  da `log:avvio del motore` — cioè la richiesta ha aspettato invece di sparire, che
  è esattamente il guasto da correggere. E il terzo ciclo è andato oltre: l'identità
  nuova creata dalla reinstallazione (`nodekey:330625fd50...`,
  machineAuthorized=False con URL di login) è stata autorizzata nel browser
  (`machineAuthorized=True`, nessun URL, `nodo autorizzato; ultimo login: <account>`),
  lo stream `/machine/map` si è aperto con 200 e circa 30 KB di netmap, e lo stato è
  passato a `Authorized`: è il **primo login**, che nessun test poteva coprire.

**Verifiche eseguite:** `cmd //c "tools\check-all.cmd"` verde (`CHECK-ALL OK`,
exit 0: skill, pin, risorse, testo della UI non nullo, anteprima e OCR, test
`ALL PASS`, CLI `selftest`, app ARM con il pacchetto creato), più la build x86 per
l'emulatore; mutazione del core osservata rossa (5 fallimenti) e verde dopo il
ripristino.

**Limiti dichiarati:** il varco fra l'avvio e l'uscita del ciclo non è stato
forzato a mano (dura millisecondi): la prova che la richiesta aspetta è il test
del core, mentre le corse su emulatore e telefono provano che il percorso reale
non si è fermato. Il login Google di un **primo** accesso resta l'unico passo non
automatizzabile (`README.md`, limiti noti 1): le due corse arrivano a `NeedLogin`
e lì si fermano.

## 2026-10-11 — La schermata nera all'avvio: il nome del dispositivo letto un istante troppo presto

**Cosa è cambiato**

- L'app si apriva con lo schermo **nero**, senza una pagina. Al primo disegno,
  `Tailscale/MainPage.xaml.cs#Refresh` (chiamato da `OnNavigatedTo`) leggeva il
  nome del dispositivo da `Tailscale/Services/EngineService.cs#DeviceName`, che il
  motore scrive sul proprio thread di lavoro: fra `Start()` e la prima riga del
  motore quella proprietà era ancora `null`, e un `TextBlock` non accetta un testo
  nullo (`ArgumentNullException` da `TextBlock.put_Text`). L'eccezione arrivava sul
  thread della UI, quindi l'app terminava. `DeviceName` ora non restituisce mai
  nullo — il motore la riempie appena parte e notifica il cambio — e il primo
  disegno mostra il nome vuoto per un istante invece di far morire l'app.
- Quel guasto non lasciava traccia: un'eccezione sul thread della UI portava via
  l'app senza scrivere una riga, quindi la diagnosi costava un debugger collegato.
  `Tailscale/App.xaml.cs#OnUnhandledException` aggancia `UnhandledException` e passa
  l'eccezione a `Tailscale/Services/EngineService.cs#LogException`, che ne registra
  tipo, messaggio, cause e stack nel registro su file. L'eccezione **non** è marcata
  `Handled`: l'errore resta un errore, e nasconderlo renderebbe "funzionante"
  un'app con la schermata nera.
- La skill di diagnosi ha il caso nuovo: `log:eccezione non gestita`, con la sua
  riga nella tabella sintomo -> significato -> azione e la spiegazione nel testo.
- Un controllo nuovo nel gate: `tools/check-ui-null-text.py` impedisce che un testo
  nullo arrivi a un `TextBlock` della pagina. Legge i TextBlock dal XAML, le
  assegnazioni della pagina, e **rilegge** la dichiarazione delle proprieta' che
  accetta come mai nulle: la correzione di `DeviceName` e' verificata dal gate, non
  solo scritta. E non e' vuoto per costruzione: se il XAML non dichiara piu'
  TextBlock, o se una delle assegnazioni che la pagina deve fare sparisce, si
  fallisce invece di non controllare niente.

**Come è stato trovato, e la prova sul dispositivo**

Il registro tirato giù dal telefono vero (build con la sola diagnostica,
installata con `AppDeployCmd.exe /install ... /force:INTERNAL` e lanciata con
`/launch`), con la riga del motore **prima** e quella del dispositivo **dopo**:

```
avvio del motore
registro su file: C:\Data\Users\DefApps\...\LocalState\registro.log
eccezione non gestita: System.ArgumentNullException: Value cannot be null.
   at Windows.UI.Xaml.Controls.TextBlock.put_Text(String value)
   at Tailscale.MainPage.Refresh()
   at Tailscale.MainPage.OnNavigatedTo(NavigationEventArgs e)
dispositivo: Windows Phone
```

L'ordine è la diagnosi: il motore ha scritto `avvio del motore` sul thread di
lavoro, il disegno della pagina è arrivato **prima** che il motore scrivesse il
nome, e in mezzo c'era la proprietà ancora nulla.

**Verifiche eseguite**

- `cmd //c "tools\check-all.cmd"` verde con la correzione dentro (agents ok,
  pin ok, risorse della UI, anteprima e OCR, test core `ALL PASS`, CLI `selftest`,
  app ARM con il pacchetto creato, `CHECK-ALL OK`, exit 0).
- Il guasto è stato **riprodotto su un dispositivo vero** prima della correzione
  (la traccia qui sopra, dal registro sul telefono) e la build corretta è stata
  installata sul dispositivo con successo (`/install ... /force:INTERNAL`, exit 0).
- Il controllo nuovo morde, e la prova sono quattro mutazioni deliberate, ognuna
  con il messaggio che nomina file, riga e controllo:
  - `DeviceName` riportata a proprietà automatica, cioè **il difetto originale**:
    `FAIL: Tailscale/MainPage.xaml.cs:88: DeviceText.Text prende un testo che può
    essere nullo (DeviceName non garantisce più il non-nullo)`, e il gate esce 1 —
    collegato, non solo presente;
  - una cosa che può essere nulla presa di peso (`Netmap.Peers[0].Hostname`):
    `FAIL ... il controllo non sa dimostrare che non sia nulla`;
  - l'assegnazione di `LogText.Text` tolta: `FAIL: LogText.Text non viene più
    riempito (il testo resta muto)`;
  - il nome `DeviceText` tolto dal XAML: stesso `FAIL`, perché quel testo non
    verrebbe più riempito.
  Ogni mutazione è stata ripristinata dal backup e il ripristino verificato
  **byte a byte** (`cmp`), con il controllo di nuovo verde e il gate di nuovo
  `CHECK-ALL OK` (exit 0).

**La controprova, sullo stesso dispositivo e sulla stessa app**

Il lancio richiede lo schermo del telefono **sbloccato**: senza, il comando
risponde `Avvio dell'applicazione non riuscito. Verificare che lo schermo del
dispositivo sia sbloccato`, e senza app avviata non c'è registro da leggere. La
sequenza che funziona è `AppDeployCmd.exe /installlaunch <appx> /targetdevice:de
/force:INTERNAL`, poi `/launch <productId> /targetdevice:de`, e il registro si
tira giù con `ISETool.exe ts de`. Le prime righe delle due corse:

```
PRIMA (build senza la correzione)          DOPO (build corretta)
avvio del motore                           avvio del motore
registro su file: C:\Data\Users\...        registro su file: C:\Data\Users\...
eccezione non gestita:                     dispositivo: Windows Phone
  System.ArgumentNullException:            portachiavi: chiave macchina salvata
  Value cannot be null.                    portachiavi: scrittura e rilettura verificate
   at TextBlock.put_Text(String value)     stato: Stopped -> Connecting
   at Tailscale.MainPage.Refresh()         ...
   at Tailscale.MainPage.OnNavigatedTo     http: /machine/register -> 200
dispositivo: Windows Phone                 stato: Connecting -> NeedLogin
                                           browser aperto sull'URL di login
```

Sulla corsa corretta: **nessuna** riga `log:eccezione non gestita` (0 su 51
righe di registro), e il percorso arriva in fondo — chiavi del server,
`ts2021: upgrade accettato`, `http: /machine/register -> 200`, `registrazione:
nodeKeyExpired=False, machineAuthorized=False, authURL=si`, `stato: Connecting ->
NeedLogin` e `log:browser aperto sull'URL di login`. Quest'ultima riga è la prova
che il disegno è arrivato fino in fondo: la scrive
`Tailscale/Services/EngineService.cs#OpenLogin`, chiamata **dentro**
`Tailscale/MainPage.xaml.cs#Refresh` sul ramo `NeedLogin`, e nessuna eccezione la
segue — quindi `Refresh` completa, `FillPeers` e `FillLog` compresi. La vecchia
build moriva alla prima riga di quello stesso metodo.

**Effetti collaterali dell'installazione, da sapere:** reinstallare il pacchetto
azzera lo stato locale dell'app, quindi l'identità è nuova (node key diversa da
quella della corsa precedente) e il telefono è di nuovo da autorizzare: la corsa
finisce in `NeedLogin` e l'app apre il browser sull'URL di login. Il tailnet può
mostrare un dispositivo in più, da togliere dal pannello di amministrazione.

**Una riga da non leggere come un guasto nuovo:** l'ultima della corsa,
`log:il motore e' gia' in moto: nessuna seconda partenza`, viene dal mio secondo
lancio a raffica (`/installlaunch` seguito subito da `/launch`) che ha riportato
l'app in primo piano mentre il ciclo precedente stava ancora uscendo: è la
finestra già dichiarata fra `Start()` e la fine del ciclo (piano 17), qui
provocata a mano.

**Il percorso Authorized, sullo stesso dispositivo.** Con il nodo autorizzato
(accesso completato nel browser) la corsa sul telefono attraversa tutto quello che
la schermata deve disegnare:

```
registrazione: nodeKeyExpired=False, machineAuthorized=True, authURL=no
nodo autorizzato; ultimo login: <l'account dell'utente>
map: apro lo stream su /machine/map (watchdog 120s, 363 byte di richiesta)
map: risposta http 200, content-type=application/octet-stream   (26 KB di netmap)
stato: Connecting -> Authorized
```

293 righe di registro e **nessuna** riga `log:eccezione non gestita`; dopo
`Authorized` non segue nessun arresto, quindi quella corsa è ancora viva.
`PublishNetmap()` ha girato, quindi `NetmapChanged` -> `RaiseChanged` -> il
dispatcher -> `Refresh()` con la netmap in mano, `FillPeers` e `FillLog` compresi
(un'eccezione nel disegno lascerebbe una riga: l'aggancio c'è apposta). Sul
telefono l'intestazione `Dispositivi` mostra il conteggio con le sue righe, e il
pannello del registro è pieno.

Una cosa che questa prova ha insegnato, e che ora è scritta nella skill di
diagnosi: il rifiuto di un secondo avvio (`log:il motore e' gia' in moto: nessuna
seconda partenza`) può capitare a un ritorno in primo piano che arriva mentre il
ciclo esce, e in `NeedLogin` il pulsante principale apre il browser invece di
avviare il motore — da lì si esce solo con un avvio **nuovo** dell'app. È la
finestra già dichiarata nel piano 17, e questa è la sua conseguenza pratica: prima
la skill la lasciava intendere come benigna.


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
