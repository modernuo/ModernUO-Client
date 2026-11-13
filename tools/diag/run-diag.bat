@echo off
setlocal
set "CUO_DIAG=1"
set "DOTNET_GCHeapHardLimit=0x200000000"
rem Optional first argument: directory holding a Release client (for example a NativeAOT publish).
set "CLIENT_DIR=%~1"
if not defined CLIENT_DIR set "CLIENT_DIR=%~dp0..\..\bin\Release"
if not exist "%CLIENT_DIR%\cuo.exe" (
    echo Client missing: "%CLIENT_DIR%\cuo.exe"
    echo Build Release first, or pass the NativeAOT publish directory.
    exit /b 1
)
pushd "%CLIENT_DIR%" || exit /b 1
start "CUO diagnostics" /wait cuo.exe
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
