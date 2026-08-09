@echo off
REM SeeMe 一键构建脚本（.NET 8 SDK）
REM 用法：双击 build.cmd 或在终端运行

set DOTNET_ROOT=D:\allll\dotnet-sdk-8
"D:\allll\dotnet-sdk-8\dotnet.exe" build -c Release

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ===== 构建成功 =====
    echo 输出: bin\Release\net8.0-windows\SeeMe.exe
) else (
    echo.
    echo ===== 构建失败 =====
    exit /b %ERRORLEVEL%
)
