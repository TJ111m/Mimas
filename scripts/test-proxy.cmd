@echo off
setlocal
rem Lifecycle + logging tests. Binds real local ports, no UI, no upstream needed
rem for most cases (one test does one real request, which may fail harmlessly).
set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%ROOT%\obj" mkdir "%ROOT%\obj"

"%CSC%" -nologo -target:exe -optimize+ -out:"%ROOT%\obj\proxytests.exe" ^
  -r:System.dll -r:System.Core.dll ^
  "%ROOT%\src\Json.cs" ^
  "%ROOT%\src\Rewriter.cs" ^
  "%ROOT%\src\AppConfig.cs" ^
  "%ROOT%\src\ProcessWatcher.cs" ^
  "%ROOT%\src\LogHub.cs" ^
  "%ROOT%\src\ProxyServer.cs" ^
  "%ROOT%\tests\ProxyTests.cs"

if errorlevel 1 (
  echo [!] proxy test build failed
  exit /b 1
)

pushd "%ROOT%\obj"
proxytests.exe %*
set RC=%errorlevel%
popd
exit /b %RC%
