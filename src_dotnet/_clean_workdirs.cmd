@echo off
setlocal enabledelayedexpansion

cd /d %~dp0

echo Cleaning build artifacts and temporary files...

rem Define directories to clean
set "CLEAN_DIRS=.vs bin obj publish logs tmp cache"

rem Clean directories recursively
for /d /r %%d in (%CLEAN_DIRS%) do (
    if exist "%%d" (
        echo Removing directory: "%%d"
        rd /s /q "%%d"
    )
)

echo.
echo Clean completed successfully.
pause
