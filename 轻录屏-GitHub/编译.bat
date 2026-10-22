@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
  echo 编译失败，请查看上面的提示。
) else (
  echo 编译完成。请将 ffmpeg.exe 放在轻录屏.exe 旁边后运行。
)
pause
