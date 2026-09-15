@echo off
rem One-click setup: creates the three power plans (movie / AI / gaming).
rem Calls setup_plans.ps1 next to this file. Safe to run multiple times.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_plans.ps1"
pause
