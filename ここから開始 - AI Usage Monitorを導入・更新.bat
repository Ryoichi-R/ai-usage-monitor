@echo off
setlocal DisableDelayedExpansion
set "SELF=%~dp0"

if "%SELF:~0,2%"=="\\" goto :unc
if not "%~2"=="" goto :too_many
if not exist "%~dp0project\rebuild-ai-usage-monitor.bat" goto :missing
if "%~1"=="" goto :no_arg
goto :one_arg

:no_arg
"%~dp0project\rebuild-ai-usage-monitor.bat"
exit /b 1

:one_arg
"%~dp0project\rebuild-ai-usage-monitor.bat" "%~1"
exit /b 1

:unc
echo UNC paths are not supported. Move the entire folder to a drive-letter path.
pause
exit /b 4

:too_many
echo Only one output parent folder can be specified.
pause
exit /b 2

:missing
echo The project folder or rebuild batch is missing.
echo Move this batch and the project folder together.
pause
exit /b 1
