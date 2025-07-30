@echo off
setlocal enabledelayedexpansion

echo Starting VOICEVOX Engine...
echo.

rem Standard VOICEVOX installation paths
rem Version 0.16+ uses vv-engine subdirectory
set "CURRENT_PATH=%USERPROFILE%\AppData\Local\Programs\VOICEVOX\vv-engine"
rem Legacy path for versions up to 0.15
set "LEGACY_PATH=%USERPROFILE%\AppData\Local\Programs\VOICEVOX"

rem Search paths in order of priority
set "SEARCH_PATHS=%CURRENT_PATH%"
set "SEARCH_PATHS=%SEARCH_PATHS%;%LEGACY_PATH%"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files\VOICEVOX\vv-engine"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files\VOICEVOX"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files (x86)\VOICEVOX\vv-engine"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files (x86)\VOICEVOX"
set "SEARCH_PATHS=%SEARCH_PATHS%;%USERPROFILE%\Desktop\VOICEVOX\vv-engine"
set "SEARCH_PATHS=%SEARCH_PATHS%;%USERPROFILE%\Desktop\VOICEVOX"
set "SEARCH_PATHS=%SEARCH_PATHS%;%USERPROFILE%\Downloads\VOICEVOX\vv-engine"
set "SEARCH_PATHS=%SEARCH_PATHS%;%USERPROFILE%\Downloads\VOICEVOX"

rem Check if VOICEVOX is already running on port 50021
netstat -an | findstr ":50021" >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo VOICEVOX Engine is already running on port 50021!
    echo You can now use VoicevoxRunCached.
    pause
    exit /b 0
)

rem First, check if run.exe is in PATH
where run.exe >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found VOICEVOX run.exe in PATH
    echo Starting VOICEVOX Engine...
    start "VOICEVOX Engine" run.exe --host 127.0.0.1 --port 50021
    echo VOICEVOX Engine started!
    echo Waiting for engine to initialize...
    timeout /t 3 /nobreak >nul
    pause
    exit /b 0
)

rem Search in installation directories
for %%p in ("%SEARCH_PATHS:;=" "%") do (
    if exist "%%~p\run.exe" (
        echo Found VOICEVOX run.exe at: %%~p\run.exe
        echo Starting VOICEVOX Engine...
        start "VOICEVOX Engine" "%%~p\run.exe" --host 127.0.0.1 --port 50021
        echo VOICEVOX Engine started!
        echo Waiting for engine to initialize...
        timeout /t 3 /nobreak >nul
        pause
        exit /b 0
    )
)

rem Search recursively in AppData for any VOICEVOX installation
echo Searching in AppData for VOICEVOX installation...
for /f "delims=" %%i in ('dir "%USERPROFILE%\AppData\Local\Programs\run.exe" /s /b 2^>nul') do (
    echo %%i | findstr /i voicevox >nul
    if !ERRORLEVEL! EQU 0 (
        echo Found VOICEVOX run.exe at: %%i
        echo Starting VOICEVOX Engine...
        start "VOICEVOX Engine" "%%i" --host 127.0.0.1 --port 50021
        echo VOICEVOX Engine started!
        echo Waiting for engine to initialize...
        timeout /t 3 /nobreak >nul
        pause
        exit /b 0
    )
)

rem If not found, show error message with correct information
echo.
echo Error: VOICEVOX run.exe not found!
echo.
echo VOICEVOX is typically installed at:
echo   Version 0.16+: %USERPROFILE%\AppData\Local\Programs\VOICEVOX\vv-engine\run.exe
echo   Version 0.15-: %USERPROFILE%\AppData\Local\Programs\VOICEVOX\run.exe
echo.
echo Please make sure VOICEVOX is installed:
echo 1. Download VOICEVOX from: https://voicevox.hiroshiba.jp/
echo 2. Install it using the official installer
echo 3. The engine (run.exe) should be located in:
echo    %USERPROFILE%\AppData\Local\Programs\VOICEVOX\vv-engine\ (v0.16+)
echo    %USERPROFILE%\AppData\Local\Programs\VOICEVOX\ (v0.15-)
echo.
echo If you have a custom installation, make sure run.exe is accessible
echo via PATH environment variable or in one of the searched locations.
echo.
pause
exit /b 1