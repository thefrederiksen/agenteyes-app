@echo off
rem Interactive launcher for the AgentEyes CLI (agenteyes). Keeps the window open (ends in
rem `cmd /k`) so the region-drag overlay and the S/P/Q hotkeys work in a real console.

rem Run from the repo root so recordings\ land there.
cd /d "%~dp0.."

rem Put the built exe on PATH for this session (inherited by the cmd /k below).
rem The projects set ^<Platforms^>x64^</Platforms^>, so a Release build lands in bin\x64\Release\.
rem A stale non-x64 output directory may hold a month-old binary - x64 path ONLY (issue #9).
set "CLIBIN=%~dp0..\src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0"
set "PATH=%CLIBIN%;%PATH%"

if not exist "%CLIBIN%\agenteyes.exe" (
  echo [error] agenteyes.exe not found. Expected: %CLIBIN%\agenteyes.exe
  echo Build it first:
  echo   dotnet build AgentEyes.sln -c Release
  echo.
  pause
  exit /b 1
)

echo ================================================================
echo   AgentEyes CLI - ready. Type a command in THIS window:
echo.
echo     agenteyes screens
echo     agenteyes shot  --screen 2 --region
echo     agenteyes shot  --screen 2
echo     agenteyes audio --screen 2 --loopback
echo     agenteyes video --screen 2 --mic "FDUCE"
echo     agenteyes package recordings\^<folder-it-made^>
echo.
echo   Session hotkeys:  S = screenshot   P = pause   Q = stop
echo   Recordings saved under:  recordings\
echo ================================================================
echo.

rem Drop into an interactive shell that keeps the window open.
cmd /k
