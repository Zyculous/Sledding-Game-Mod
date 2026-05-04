@echo off
REM SledCoopMod Windows installer launcher.
REM Wraps install_sledcoopmod.ps1 so Windows users can just double-click this
REM file. PowerShell is bundled with Windows; no Python is required.

setlocal

set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%install_sledcoopmod.ps1"

if not exist "%PS1%" (
    echo Could not find install_sledcoopmod.ps1 next to this batch file.
    echo Expected: "%PS1%"
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "EXITCODE=%ERRORLEVEL%"

REM Only pause on error if PowerShell didn't already pause for the user.
if not "%EXITCODE%"=="0" (
    echo.
    echo Installer exited with code %EXITCODE%.
    pause
)

endlocal & exit /b %EXITCODE%
