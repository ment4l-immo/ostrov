@echo off
rem Publishes Ostrov to dist\ and starts it. Close the island first (tray -> Exit) or this script will close it.
cd /d "%~dp0"
taskkill /im Ostrov.exe /f >nul 2>&1
dotnet publish src\Ostrov -c Release -o dist --self-contained false -p:DebugType=none -p:DebugSymbols=false
if errorlevel 1 (
  echo.
  echo Build failed. See errors above.
  pause
  exit /b 1
)
start "" "%~dp0dist\Ostrov.exe"
