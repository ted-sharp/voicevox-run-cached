@echo off
setlocal enabledelayedexpansion

cd /d %~dp0

echo Publishing VoicevoxRunCached...

rem Clean previous publish directory
if exist "publish" (
    echo Removing previous publish directory...
    rmdir /s /q publish
)

rem Build the project
echo Building the project...
dotnet build .\VoicevoxRunCached\VoicevoxRunCached.csproj -c Release

rem Publish the application
echo Building and publishing...
dotnet publish .\VoicevoxRunCached\VoicevoxRunCached.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\VoicevoxRunCached

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Publish completed successfully.
) else (
    echo.
    echo Operation failed with error code %ERRORLEVEL%
)

pause