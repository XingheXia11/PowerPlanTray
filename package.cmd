@echo off
rem Build, then create dist\PowerPlanTray_<version>.zip.
rem The zip contains a single versioned exe (PowerPlanTray_<version>.exe);
rem no duplicate exe is left in the project root.
setlocal
call "%~dp0build.cmd"
if errorlevel 1 (
    echo Package FAILED: build error
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
if errorlevel 1 (
    echo Package FAILED
    exit /b 1
)
endlocal
