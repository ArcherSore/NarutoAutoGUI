@echo off
chcp 65001 >nul
setlocal

set "NEED_VC=1"
set "NEED_DOTNET=1"
set "ELEVATE_ARGS="
if "%~1"=="--repair" goto elevate

reg query "HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" /v Installed /reg:64 >nul 2>&1
if "%errorlevel%"=="0" set "NEED_VC=0"
if exist "%ProgramW6432%\dotnet\shared\Microsoft.WindowsDesktop.App\10.*" set "NEED_DOTNET=0"
if not "%NEED_VC%%NEED_DOTNET%"=="00" goto elevate

echo 运行库都已安装，不需要再安装。
set "ANSWER="
set /p "ANSWER=如果程序仍提示缺少运行库，输入 R 后按回车重新修复；否则直接按回车退出："
if /i not "%ANSWER%"=="R" exit /b 0
set "NEED_VC=1"
set "NEED_DOTNET=1"
set "ELEVATE_ARGS=-ArgumentList '--repair'"

:elevate
net session >nul 2>&1
if "%errorlevel%"=="0" goto elevated
echo 正在请求管理员权限...
set "SELF=%~f0"
powershell -NoProfile -Command "Start-Process -FilePath '%SELF:'=''%' %ELEVATE_ARGS% -Verb RunAs"
exit /b

:elevated
set "FAILED=0"
if "%NEED_VC%"=="1" call :repair "Microsoft Visual C++ 运行库" "Microsoft.VCRedist.2015+.x64" "https://aka.ms/vs/17/release/vc_redist.x64.exe"
if "%NEED_DOTNET%"=="1" call :repair ".NET 10 桌面运行库" "Microsoft.DotNet.DesktopRuntime.10" "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

echo.
if "%FAILED%"=="0" goto done
echo 部分运行库未能安装。请在浏览器中打开下面的链接，下载并安装后再启动程序：
echo   https://aka.ms/vs/17/release/vc_redist.x64.exe
echo   https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe
goto finish

:done
echo 运行库已安装完成。如果程序仍然无法运行，请重启电脑后再试。

:finish
echo.
pause
exit /b %FAILED%

:repair
echo.
echo ==== 正在安装 %~1 ====
:: winget reports failures as negative codes, which "if errorlevel 1" would treat as success.
winget install --id "%~2" --exact --source winget --override "/repair /passive /norestart" --accept-package-agreements --accept-source-agreements --force
if "%errorlevel%"=="0" exit /b 0

echo winget 未能完成安装，改为直接下载...
set "INSTALLER=%TEMP%\NarutoAutoGUI-dependency-%RANDOM%.exe"
curl.exe -fL --retry 2 -o "%INSTALLER%" "%~3"
if not "%errorlevel%"=="0" goto download_failed
"%INSTALLER%" /repair /passive /norestart
set "CODE=%errorlevel%"
del "%INSTALLER%" >nul 2>&1
:: 3010 and 1641 ask for a restart; 1638 means a newer version is already installed.
for %%c in (0 3010 1641 1638) do if "%CODE%"=="%%c" exit /b 0
echo %~1 安装失败，错误码 %CODE%。
set "FAILED=1"
exit /b 1

:download_failed
echo %~1 下载失败。
set "FAILED=1"
exit /b 1
