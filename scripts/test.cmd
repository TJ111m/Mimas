@echo off
setlocal
set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%ROOT%\obj" mkdir "%ROOT%\obj"

"%CSC%" -nologo -target:exe -optimize+ -out:"%ROOT%\obj\tests.exe" ^
  -r:System.dll -r:System.Core.dll ^
  "%ROOT%\src\Json.cs" "%ROOT%\src\Rewriter.cs" "%ROOT%\tests\RewriterTests.cs"

if errorlevel 1 (
  echo [!] test build failed
  exit /b 1
)

"%ROOT%\obj\tests.exe"
exit /b %errorlevel%
