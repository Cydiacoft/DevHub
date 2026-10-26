@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
set "DEVHUB_ROOT=%~dp0"
if exist "%~dp0.devhub\build\publish\DevHub\DevHub.App.exe" (
  "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0.devhub\build\publish\DevHub\Start-DevHub.ps1" -RootDirectory "%~dp0."
  if errorlevel 1 (
    echo 启动失败，请双击同目录下的 DevHub 快捷方式，或打开发布目录中的 DevHub.App.exe。
    pause
    exit /b 1
  )
) else (
  echo 请先在 PowerShell 中运行: .\build.ps1 -Task Publish
  pause
  exit /b 1
)
