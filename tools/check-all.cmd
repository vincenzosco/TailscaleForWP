@echo off
setlocal
cd /d "%~dp0.."
set MSB="C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe"
set LOGDIR=.superpowers\logs
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

rem I controlli su protocollo, documentazione e segreti girano PRIMA dei test:
rem un pin sbagliato o una skill che cita un file inesistente deve fallire subito.
if exist tools\check-protocol-pin.py (
  echo [0/5] Pin di protocollo
  python tools\check-protocol-pin.py || exit /b 1
)
if exist tools\check-docs.py (
  echo [0/5] Documentazione
  python tools\check-docs.py || exit /b 1
)
if exist tools\check-secrets.py (
  echo [0/5] Segreti
  python tools\check-secrets.py || exit /b 1
)
if exist tools\check-no-emoji.py (
  echo [0/5] Emoji
  python tools\check-no-emoji.py || exit /b 1
)
if exist tools\check-ui-resources.py (
  echo [0/5] Risorse della UI
  python tools\check-ui-resources.py || exit /b 1
)

rem Le skill degli agent devono descrivere il codice che c'e' davvero: un file
rem rinominato o una riga di registro cambiata senza aggiornare la skill e' una
rem istruzione che manda a cercare qualcosa che non esiste.
if exist tools\check-agents.py (
  echo [0/5] Skill degli agent
  python tools\check-agents.py || exit /b 1
)

rem Anteprima e OCR dipendono da Pillow e da Tesseract, che non sono prerequisiti
rem del progetto: se mancano si dice che il controllo e' saltato (non "passato"),
rem perche' un salto silenzioso sarebbe una verifica dichiarata e non fatta.
if exist tools\render-ui-preview.py (
  echo [0/5] Anteprima della UI
  python -c "import PIL" >nul 2>nul
  if errorlevel 1 (
    echo [0/5] Anteprima della UI: SALTATA, Pillow non installato ^(pip install pillow^)
  ) else (
    python tools\render-ui-preview.py || exit /b 1
  )
)

if exist tools\ocr-check-ui.py (
  echo [0/5] OCR della UI
  python -c "import pytesseract, PIL" >nul 2>nul
  if errorlevel 1 (
    echo [0/5] OCR della UI: SALTATO, pytesseract non installato ^(pip install pytesseract^)
  ) else (
    python tools\ocr-check-ui.py || exit /b 1
  )
)

echo [1/5] Core desktop
%MSB% src\Tailscale.Core\Tailscale.Core.Desktop.csproj -nologo -verbosity:minimal -p:Configuration=Release
if errorlevel 1 exit /b 1

echo [2/5] Tests
%MSB% src\Tailscale.Core.Tests\Tailscale.Core.Tests.csproj -nologo -verbosity:minimal -p:Configuration=Release
if errorlevel 1 exit /b 1

echo [3/5] Run tests
src\Tailscale.Core.Tests\bin\Release\Tailscale.Core.Tests.exe
if errorlevel 1 (
  echo TEST FALLITI
  exit /b 1
)

if not exist Tailscale\Tailscale.csproj goto :skipapp
echo [4/5] CLI
if exist src\Tailscale.Cli\Tailscale.Cli.csproj (
  %MSB% src\Tailscale.Cli\Tailscale.Cli.csproj -nologo -verbosity:minimal -p:Configuration=Release
  if errorlevel 1 exit /b 1
  rem selftest e' offline: la pipeline non deve dipendere dalla rete verso tailscale.
  src\Tailscale.Cli\bin\Release\Tailscale.Cli.exe selftest
  if errorlevel 1 (
    echo CLI SELFTEST FALLITO
    exit /b 1
  )
)

echo [5/5] App WP8.1 ARM
%MSB% Tailscale\Tailscale.csproj -nologo -verbosity:minimal -p:Configuration=Release -p:Platform=ARM
if errorlevel 1 exit /b 1

:skipapp
echo CHECK-ALL OK
