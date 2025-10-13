@echo off
REM Complete Code Quality Fix & Verification
REM Batch wrapper for _fix-all.ps1

powershell -ExecutionPolicy Bypass -File "%~dp0_fix-all.ps1" %*

PAUSE
