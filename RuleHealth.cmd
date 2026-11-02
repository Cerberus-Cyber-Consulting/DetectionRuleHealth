@echo off
rem Double-click to start RuleHealth: builds if needed, serves the UI on http://localhost:5180
rem with the tenant data in .\data, and opens the browser. Close this window (or Ctrl+C) to stop.
rem Everything else (adding a tenant, collect, extract, validate) is done in the UI.
cd /d "%~dp0"

rem Reload PATH from the registry. Explorer and VS Code hand out the PATH they had when they started,
rem so tools installed since then (dotnet, the Azure CLI) would be missing for this window.
set "MACHINE_PATH="
set "USER_PATH="
for /f "skip=2 tokens=2,*" %%A in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path 2^>nul') do set "MACHINE_PATH=%%B"
for /f "skip=2 tokens=2,*" %%A in ('reg query "HKCU\Environment" /v Path 2^>nul') do set "USER_PATH=%%B"
rem "call set" expands %SystemRoot%-style entries stored unexpanded in the registry.
if defined MACHINE_PATH call set "PATH=%MACHINE_PATH%;%USER_PATH%"
set "PATH=%ProgramFiles%\dotnet;%PATH%"

if not exist data mkdir data
dotnet run --project src\RuleHealth.Cli -- serve --data data --open %*
if errorlevel 1 pause
