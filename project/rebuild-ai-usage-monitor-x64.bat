@echo off
setlocal DisableDelayedExpansion
set "SELF=%~dp0"
if "%SELF:~0,2%"=="\\" (
  echo UNC paths are not supported. Move the entire folder to a drive-letter path.
  pause
  exit /b 4
)
cd /d "%~dp0"

where dotnet.exe >nul 2>&1
if errorlevel 1 (
  echo .NET SDK ^(dotnet.exe^) was not found.
  echo Install the .NET 10 SDK and try again.
  pause
  exit /b 1
)

where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 ^(pwsh.exe^) was not found.
  echo Install PowerShell 7 and try again.
  pause
  exit /b 1
)

if not "%~2"=="" (
  echo Only one output parent folder can be specified.
  pause
  exit /b 2
)

if "%~1"=="" (
  pwsh.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\rebuild-ai-usage-monitor.ps1" -Runtime win-x64 -SelectOutputRoot -RevealOutput
) else (
  pwsh.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\rebuild-ai-usage-monitor.ps1" -Runtime win-x64 -OutputRoot "%~f1" -RevealOutput
)
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="3" (
  echo AI Usage Monitor x64 rebuild was cancelled. No files were generated.
  pause
  exit /b 3
)
if not "%EXIT_CODE%"=="0" (
  echo AI Usage Monitor x64 rebuild failed. Review the error above.
  pause
  exit /b %EXIT_CODE%
)

echo AI Usage Monitor x64 rebuild completed successfully.
pause
exit /b 0
