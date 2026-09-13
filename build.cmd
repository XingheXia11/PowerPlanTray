@echo off
rem Rebuild PowerPlanTray.exe with the csc.exe that ships with Windows (.NET Framework)
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found
    exit /b 1
)
"%CSC%" /nologo /optimize+ /target:winexe /out:PowerPlanTray.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /win32icon:app.ico /win32manifest:app.manifest PowerPlanTray.cs
if errorlevel 1 (
    echo Build FAILED
    exit /b 1
)
echo Build OK: PowerPlanTray.exe
endlocal
