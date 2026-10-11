@echo off
rem Wrapper per MSBuild 12.0 (VS2013), l'unico toolchain presente su questa macchina.
rem Gli switch usano la forma con il trattino: in Git Bash una barra iniziale viene
rem interpretata come percorso e convertita in "C:/Program Files/Git/nologo".
"C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe" %*
