@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>nul
title StudentAge QQ AI Moments Diagnose and Repair
set "QQAI_PS1=%~dp0diagnose_fix_qqai_moments.ps1"
set "QQAI_ARG1=%~1"
set "QQAI_ARG2=%~2"
set "QQAI_ARG3=%~3"
set "QQAI_ARG4=%~4"
set "QQAI_ARG5=%~5"
set "QQAI_ARG6=%~6"
set "QQAI_ARG7=%~7"
set "QQAI_ARG8=%~8"
if not exist "%QQAI_PS1%" (
  echo Missing diagnose_fix_qqai_moments.ps1 next to this BAT.
  pause >nul
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $enc=New-Object System.Text.UTF8Encoding -ArgumentList $false; $raw=[System.IO.File]::ReadAllText($env:QQAI_PS1,$enc); $sb=[ScriptBlock]::Create($raw); & $sb @args" %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Diagnose/repair failed. Please send the generated qqai_diagnose_fix log to the author.
  pause >nul
)
exit /b %ERR%
