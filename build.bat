@echo off
rem 打包 Token Details → dist\TokenDetails.exe
rem 用法：双击或命令行运行 build.bat；完成后手动启动 dist\TokenDetails.exe
cd /d "%~dp0"

rem 停掉运行中的旧 exe，避免 PyInstaller 写 dist 被文件锁拦住
powershell -NoProfile -Command "Get-Process TokenDetails -ErrorAction SilentlyContinue | Stop-Process -Force"

python -m PyInstaller --onefile --noconsole --icon token_speed.ico --add-data "token_speed.ico;." --name TokenDetails token_speed.py
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo.
echo OK: %~dp0dist\TokenDetails.exe
