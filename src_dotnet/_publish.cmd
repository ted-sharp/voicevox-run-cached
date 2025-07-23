@echo off
setlocal enabledelayedexpansion

cd /d %~dp0

echo Publishing VoicevoxRunCached...

rem Get version from project file
echo Extracting version information...
for /f "tokens=3 delims=<>" %%i in ('findstr "<Version>" VoicevoxRunCached\VoicevoxRunCached.csproj') do set PROJECT_VERSION=%%i

if "%PROJECT_VERSION%"=="" (
    echo Warning: Could not extract version from project file. Using default version 1.0.0
    set PROJECT_VERSION=1.0.0
)

echo Found version: %PROJECT_VERSION%
set RELEASE_NAME=VoicevoxRunCached-v%PROJECT_VERSION%-win-x64

rem Clean previous publish directory
if exist "publish" (
    echo Removing previous publish directory...
    rmdir /s /q publish
)

rem Build the project
echo Building the project...
dotnet build .\VoicevoxRunCached\VoicevoxRunCached.csproj -c Release

if %ERRORLEVEL% NEQ 0 (
    echo Build failed with error code %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

rem Publish the application (keep original structure)
echo Building and publishing...
dotnet publish .\VoicevoxRunCached\VoicevoxRunCached.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\VoicevoxRunCached

if %ERRORLEVEL% NEQ 0 (
    echo Publish failed with error code %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

rem Create ZIP archive using PowerShell with version-based naming
echo Creating ZIP archive...
powershell -Command "Compress-Archive -Path '.\publish\VoicevoxRunCached\*' -DestinationPath '.\publish\%RELEASE_NAME%.zip' -Force"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo Publish and ZIP creation completed successfully!
    echo Version: %PROJECT_VERSION%
    echo Output: .\publish\%RELEASE_NAME%.zip
    echo ========================================
    echo.
    
    rem Show ZIP file info
    echo ZIP file created:
    dir ".\publish\%RELEASE_NAME%.zip"
    echo.
    echo Published files in .\publish\VoicevoxRunCached\:
    dir ".\publish\VoicevoxRunCached"
) else (
    echo.
    echo ZIP creation failed with error code %ERRORLEVEL%
)

pause