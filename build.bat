@echo off
rem Compila o ColorShift com o compilador C# que ja vem no .NET Framework 4.x do Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe nao encontrado em %CSC%
  exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:bin\ColorShift.exe ^
  /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
rem Modulos PawnIO (assinados) que dao acesso ao SMBus da memoria: Intel e AMD.
copy /y pawnio\*.bin bin\ >nul
echo Pronto: %~dp0bin\ColorShift.exe
