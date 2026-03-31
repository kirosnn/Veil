@echo off
chcp 65001 >nul 2>&1
set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%PWSH%" (
  "%PWSH%" -ExecutionPolicy Bypass -NoProfile -File "%~dp0scripts\dev.ps1"
) else (
  powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0scripts\dev.ps1"
)
