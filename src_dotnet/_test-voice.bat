@echo off
setlocal enabledelayedexpansion

rem Quick test runner for VoicevoxRunCached
rem Usage:
rem   _test-voice.bat                 -> plays default sample text
rem   _test-voice.bat "好きな文章"       -> plays given text
rem   _test-voice.bat speakers        -> list available speakers
rem   _test-voice.bat --init          -> initialize filler cache
rem   _test-voice.bat --clear         -> clear audio cache
rem   _test-voice.bat "文章" --verbose   -> pass-through any options

set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%VoicevoxRunCached"

if not exist "%PROJECT_DIR%\VoicevoxRunCached.csproj" (
  echo Project not found: %PROJECT_DIR%
  exit /b 1
)

pushd "%PROJECT_DIR%" >nul 2>&1

if "%~1"=="" (
  set "TEXT=テストメッセージです。"
  echo Running: VoicevoxRunCached "!TEXT!"
  dotnet run -- "!TEXT!"
) else (
  echo Running: VoicevoxRunCached %*
  dotnet run -- %*
)

set ERR=%ERRORLEVEL%
popd >nul 2>&1

exit /b %ERR%


