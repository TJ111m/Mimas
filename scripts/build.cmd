@echo off
setlocal
rem Build Mimas.exe with the .NET Framework compiler that ships with Windows.
rem No SDK, no NuGet, no admin rights required.

set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [!] csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"

"%CSC%" -nologo -target:winexe -optimize+ -platform:anycpu ^
  -out:"%ROOT%\bin\Mimas.exe" ^
  -win32icon:"%ROOT%\assets\mimas.ico" ^
  -r:System.dll ^
  -r:System.Core.dll ^
  -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll ^
  "%ROOT%\src\Json.cs" ^
  "%ROOT%\src\Rewriter.cs" ^
  "%ROOT%\src\AppConfig.cs" ^
  "%ROOT%\src\ProcessWatcher.cs" ^
  "%ROOT%\src\LogHub.cs" ^
  "%ROOT%\src\ProxyServer.cs" ^
  "%ROOT%\src\AppHost.cs" ^
  "%ROOT%\src\AgentIntegration.cs" ^
  "%ROOT%\src\Diagnostics.cs" ^
  "%ROOT%\src\Ui\Theme.cs" ^
  "%ROOT%\src\Ui\Controls.cs" ^
  "%ROOT%\src\Ui\LiveLogView.cs" ^
  "%ROOT%\src\Ui\MainForm.cs" ^
  "%ROOT%\src\Ui\SetupWizard.cs" ^
  "%ROOT%\src\Program.cs"

if errorlevel 1 (
  echo [!] build failed
  exit /b 1
)

echo [ok] bin\Mimas.exe
