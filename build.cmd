@echo off
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: .NET Framework 4.x C# compiler not found.
    pause
    exit /b 1
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "if (Get-Process -Name TCWeChatRedirector -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
    echo ERROR: TCWeChatRedirector.exe is running.
    echo Stop it from the tray menu or run the uninstall CMD first.
    pause
    exit /b 1
)

"%CSC%" /nologo /codepage:65001 /target:winexe /out:"%~dp0TCWeChatRedirector.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0TCWeChatRedirector.cs"
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)
echo Built: %~dp0TCWeChatRedirector.exe
pause
