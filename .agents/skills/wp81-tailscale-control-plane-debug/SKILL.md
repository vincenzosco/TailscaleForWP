---
name: wp81-tailscale-control-plane-debug
description: Usa quando l'app non si connette, la lista peer resta vuota o nel Registro compare un'eccezione — la catena reale delle tracce, il significato e cosa fare.
---

# Leggere un guasto del control plane

Questa skill non è un elenco di eccezioni da temere: è il modo di capire, dal
Registro dell'app o dall'output della CLI, se quella che si sta guardando è una
cosa **attesa** che il client ha già assorbito, oppure un guasto vero.

## 1. La catena, dalla traccia vera

Il caso che arriva più spesso dal telefono è una lettura interrotta dal sistema
(`HResult 0x800703E3`, "operazione di I/O interrotta perché il thread è uscito o
perché l'applicazione l'ha chiesta"): l'app viene sospesa, o una scadenza chiude
il canale. La traccia risale così:

```
Tailscale.Services.SocketStream.Read                     (Tailscale/Services/PhonePlatform.cs#SocketStream)
  System.Runtime.WindowsRuntime ... EndRead
Tailscale.Core.Protocol.NoiseHandshake.ReadExact         (src/Tailscale.Core/Protocol/NoiseHandshake.cs#ReadExact)
Tailscale.Core.Protocol.NoiseTransport.ReadFrame         (src/Tailscale.Core/Protocol/NoiseTransport.cs#ReadFrame)
Tailscale.Core.Control.NoiseFrameStream.Read             (src/Tailscale.Core/Control/NoiseFrameStream.cs#NoiseFrameStream)
Tailscale.Core.PushbackStream.Read
Tailscale.Core.Protocol.Http2Frame.Read                  (src/Tailscale.Core/Protocol/Http2Frames.cs#Http2Frame)
Tailscale.Core.Protocol.Http2Connection.ReadFrame        (src/Tailscale.Core/Protocol/Http2Connection.cs#ReadFrame)
```

`Http2Connection.ReadFrame` la avvolge in `Http2Exception` con
`log:lettura di un frame HTTP/2 fallita: ` e `InnerException: System.IO.IOException`;
durante la netmap, `NoiseControlConnection.ReadMapMessage` la avvolge ancora in
`ControlStreamException` con `log:canale di controllo interrotto durante `.

Due dettagli che fanno perdere tempo se non si sanno:

- lo stesso guasto si presenta **due volte** con la stessa traccia: una per lo
  stream della netmap, una per il trasporto sottostante. Non sono due problemi;
- l'`HResult` esterno è `-2146232800` e quello interno `-2147023901`: sono la
  stessa cosa vista da due strati. Cercare la differenza fra i due numeri non
  porta da nessuna parte; il testo (`0x800703E3`) è quello che conta.

## 2. Il significato, e perché spesso non c'è niente da correggere

Il client **conosce** questa classe di guasto:
`src/Tailscale.Core/Control/NoiseControlConnection.cs#IsChannelFailure` la
riconosce (lettura interrotta dal sistema, socket morto, canale già chiuso),
`src/Tailscale.Core/Control/NoiseControlConnection.cs#ChannelFailure` dichiara il
canale rotto, e `src/Tailscale.Core/Control/ControlClient.cs#RecoverFromChannelFailure`
lo rifà, fino a `src/Tailscale.Core/Control/ControlClient.cs#MaxChannelRetries`
tentativi consecutivi.

Quindi: una traccia come quella sopra **non è di per sé un guasto da correggere**.
È il segnale che il canale è stato rifatto — e la prova sta nella riga che segue,
`log:lo rifaccio (`, che dice a che tentativo siamo. Quello che va corretto è
solo la sua causa: l'app sospesa dall'utente, oppure nessuna risposta dal server.

Se invece il canale è stato chiuso da una scadenza, lo stesso percorso produce un
`ControlTimeoutException` e la riga
`log:la richiesta non ha ricevuto risposta entro la scadenza`: è diverso da un
socket morto, ed è per questo che il client lo distingue.

## 3. La tabella: sintomo, significato, azione

| Riga di registro | Significato | Azione |
|---|---|---|
| `log:canale di controllo interrotto durante ` + `log:la connessione e' caduta o il sistema ha interrotto la lettura` | lettura interrotta: sospensione dell'app, socket chiuso, o scadenza non attribuibile. Il canale viene rifatto | nessuna, se la riga seguente è `log:lo rifaccio (` |
| `log:la richiesta non ha ricevuto risposta entro la scadenza` | la scadenza di quella richiesta è passata: prima di questa correzione il client aspettava per sempre | guarda quale richiesta era; per una followup è la fine della finestra di login |
| `log:nessun dato per` + `log:la netmap` + `log:s, chiudo il canale` | il watchdog della netmap (120s, `src/Tailscale.Core/Protocol/ProtocolConstants.cs#MapStreamWatchdogSeconds`) ha chiuso un canale muto | atteso su una rete che si ferma: il ciclo si riconnette |
| `log:stream della netmap chiuso; riconnessione` | il ciclo della netmap riparte **senza** cancellare i peer già mostrati | atteso |
| `log:netmap non disponibile dopo ` | la netmap non è **mai** arrivata e i tentativi sono finiti (`src/Tailscale.Core/Control/ControlClient.cs#ReadMapOnce`): il client si ferma e lo dice | è il caso da riportare, con le righe precedenti |
| `log:netmap non disponibile: nuovo tentativo fra poco` | è lo stato mostrato all'utente in quel caso | riportare insieme al registro |
| `log:canale di controllo non disponibile dopo ` | il canale non torna: budget esaurito durante la registrazione | guasto vero: raccogliere le righe precedenti, non è una sospensione |
| `log:login non completato entro ` | la finestra di login è finita senza che il browser risultasse usato (`src/Tailscale.Core/Control/ControlClient.cs#RemainingLoginSeconds`) | l'URL resta valido: l'utente lo riapre dal pulsante |
| `log:nodo autorizzato; ultimo login: ` | la registrazione è conclusa: da qui in poi la netmap si legge da sola | nessuna |

## 4. Cosa fare quando si riporta il problema

- Le **ultime 20 righe del Registro dell'app**, copiate come sono: sono la prova
  di dove il percorso è finito.
- La **traccia completa** se c'è un'eccezione, con il nome del tipo.
- Cosa stava facendo l'app: primo accesso, ritorno dal browser, app lasciata in
  primo piano, passaggio in secondo piano e ritorno.

E cosa **non** incollare mai: chiavi private di qualunque forma, e l'URL di login
completo (basta dire "URL di login aperto"). Il controllo `tools/check-agents.py`
cerca questi pattern nelle skill proprio perché è facile farlo per sbaglio.

## 5. Cosa **non** è un guasto

- La sospensione dell'app che interrompe una lettura: è l'app che chiude il
  socket di proposito. Al ritorno in primo piano riparte da sola con l'identità
  salvata, e il device nel tailnet resta uno.
- Un `AuthURL` che aspetta: il server tiene aperta la followup mentre l'utente
  accede nel browser. È voluto.
- L'errore `device with nodekey:... already exists`: il client genera
  un'identità nuova e riprova; al primo login dopo una cancellazione dal
  pannello admin compare un device nuovo, ed è corretto.
- La prima registrazione dopo un login non completato: l'URL salvato viene
  ripulito solo quando il nodo risulta autorizzato.

Per un confronto dal PC, la CLI stampa le stesse fasi:
`src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe handshake`,
`src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe probe`,
`src/Tailscale.Cli/bin/Release/Tailscale.Cli.exe login`.

Ultima verifica: 2026-10-11
