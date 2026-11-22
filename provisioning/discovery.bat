@echo off
rem discovery.bat - SOLO LETTURA. Questo script non modifica nulla.
rem
rem Serve una volta sola, su un telefono interop-unlocked, per scoprire dove sta
rem cio' che il manifest puo' descrivere: il proxy di sistema, la chiave
rem dell'app, e se il telefono ha chiavi del profilo VPN di sistema.
rem
rem Come si esegue: dal CMD Injector, oppure incollando i comandi in una shell
rem via telnet. L'output si copia integrale in docs/wp81-registry-findings.md:
rem e' l'evidenza che serve prima di aggiungere qualsiasi chiave di sistema alla
rem whitelist. Nessun reg add, nessun reg delete, nessuna scrittura.

echo === sistema
ver
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v CurrentVersion 2>nul
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ProductName 2>nul

echo === proxy di sistema (il collegamento documentato)
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings" 2>nul

echo === chiave dell'app
reg query "HKLM\SOFTWARE\TailscaleWP" 2>nul

echo === autostart, se esiste
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" 2>nul

echo === chiavi che parlano di VPN (ricerca, sempre sola lettura)
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion" /s /f "VPN" 2>nul
reg query "HKLM\SOFTWARE\Microsoft" /f "VPNv2" /s 2>nul

echo === fine. Se una query risponde "non trovato", la chiave non c'e':
echo     e' una risposta, non un errore, e si scrive cosi' nel documento.