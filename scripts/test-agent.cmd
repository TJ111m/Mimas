@echo off
setlocal
rem Offline tests for the ZCode config rewriter. Never touches the real config.
set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%ROOT%\obj" mkdir "%ROOT%\obj"

"%CSC%" -nologo -target:exe -optimize+ -out:"%ROOT%\obj\agenttests.exe" ^
  -r:System.dll -r:System.Core.dll ^
  "%ROOT%\src\Json.cs" ^
  "%ROOT%\src\AgentIntegration.cs" ^
  "%ROOT%\tests\AgentTests.cs"

if errorlevel 1 (
  echo [!] agent test build failed
  exit /b 1
)

"%ROOT%\obj\agenttests.exe"
exit /b %errorlevel%
