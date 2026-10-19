@echo off
REM ------------------------------------------------------------------
REM  Builds CharmsBar.exe using the C# compiler that is already part of
REM  Windows (.NET Framework 4.x). No internet, no Visual Studio needed.
REM ------------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe - is .NET Framework 4.x installed?
    pause
    exit /b 1
)

REM stop a running copy so the exe can be overwritten
taskkill /f /im CharmsBar.exe >nul 2>&1

"%CSC%" /nologo /target:winexe /optimize+ /debug- /platform:anycpu ^
    /out:CharmsBar.exe /win32manifest:app.manifest ^
    /reference:System.dll /reference:System.Core.dll ^
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    CharmsBar.cs StartScreen.cs

if errorlevel 1 (
    echo.
    echo BUILD FAILED - copy the error lines above and send them to me.
    pause
    exit /b 1
)

echo.
echo Built CharmsBar.exe successfully.
echo Double-click it to start. Right-click the tray icon for "Run at startup" / "Exit".
pause
