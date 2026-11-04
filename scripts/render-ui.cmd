@echo off
setlocal
rem Renders every UI page to PNG without showing a window.
rem Output defaults to %TEMP%\mimas-ui; pass a directory as the first argument.
set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%ROOT%\obj" mkdir "%ROOT%\obj"

"%CSC%" -nologo -target:exe -optimize+ -out:"%ROOT%\obj\renderui.exe" ^
  -main:MimasTests.RenderUi ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
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
  "%ROOT%\src\Program.cs" ^
  "%ROOT%\tests\RenderUi.cs"

if errorlevel 1 (
  echo [!] render build failed
  exit /b 1
)

"%ROOT%\obj\renderui.exe" %*
exit /b %errorlevel%
