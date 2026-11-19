@echo off
rem REAArchive launcher — 直接运行构建产物（不联网、不更新）
setlocal
set "EXE=%~dp0src\REAArchive\bin\Release\net9.0-windows\REAArchive.exe"
if not exist "%EXE%" (
  echo [!] 尚未构建。先运行： pwsh scripts\build.ps1
  pause
  exit /b 2
)
start "" "%EXE%" %*
