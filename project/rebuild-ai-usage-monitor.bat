@echo off
setlocal DisableDelayedExpansion
set "SELF=%~dp0"

if "%SELF:~0,2%"=="\\" goto :unc
if not "%~2"=="" goto :too_many

if defined PROCESSOR_ARCHITEW6432 (
  set "NATIVE_ARCH=%PROCESSOR_ARCHITEW6432%"
) else (
  set "NATIVE_ARCH=%PROCESSOR_ARCHITECTURE%"
)

if /i "%NATIVE_ARCH%"=="AMD64" goto :x64
if /i "%NATIVE_ARCH%"=="ARM64" goto :arm64
goto :unsupported

:x64
if not exist "%~dp0rebuild-ai-usage-monitor-x64.bat" goto :missing
if "%~1"=="" goto :x64_no_arg
"%~dp0rebuild-ai-usage-monitor-x64.bat" "%~1"
exit /b 1
:x64_no_arg
"%~dp0rebuild-ai-usage-monitor-x64.bat"
exit /b 1

:arm64
if not exist "%~dp0rebuild-ai-usage-monitor-arm64.bat" goto :missing
if "%~1"=="" goto :arm64_no_arg
"%~dp0rebuild-ai-usage-monitor-arm64.bat" "%~1"
exit /b 1
:arm64_no_arg
"%~dp0rebuild-ai-usage-monitor-arm64.bat"
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
echo The required architecture-specific rebuild batch is missing.
pause
exit /b 1

:unsupported
echo Unsupported native Windows architecture: "%NATIVE_ARCH%"
echo Supported architectures: AMD64 and ARM64.
pause
exit /b 1
