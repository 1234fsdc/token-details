@echo off
rem TokenDetailsNative: 单进程 C# 版（系统自带 csc，无需安装任何 SDK）
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc not found & exit /b 1 )
set GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
for /f "delims=" %%i in ('dir /b /s "%GAC%\UIAutomationClient.dll" 2^>nul') do if not defined UIC set "UIC=%%i"
for /f "delims=" %%i in ('dir /b /s "%GAC%\UIAutomationTypes.dll" 2^>nul') do if not defined UIT set "UIT=%%i"
for /f "delims=" %%i in ('dir /b /s "%GAC%\WindowsBase.dll" 2^>nul') do if not defined WB set "WB=%%i"
if not exist dist mkdir dist
"%CSC%" -nologo -target:winexe -optimize+ -unsafe -r:"%UIC%" -r:"%UIT%" -r:"%WB%" -r:System.Drawing.dll -r:System.Core.dll -r:System.Web.Extensions.dll -out:dist\TokenDetailsNative.exe TokenDetailsNative.cs
if errorlevel 1 ( echo BUILD_FAIL & exit /b 1 )
copy /y sqlite3.dll dist\ >nul
echo BUILD_OK dist\TokenDetailsNative.exe
