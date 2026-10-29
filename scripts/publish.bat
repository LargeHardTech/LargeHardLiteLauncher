@echo off
setlocal
cd /d "%~dp0.."

echo ============================================
echo   LargeHardLiteLauncher  NativeAOT Publish
echo ============================================
echo.

rem MSVC / Windows SDK paths -- required by the AOT linker.
rem If you get LNK1181, these three lines are what you need to fix.
rem Version folders change when VS/SDK gets updated.
set "MSVC=D:\visual studio\vs2026\VC\Tools\MSVC\14.51.36231"
set "SDK=C:\Program Files (x86)\Windows Kits\10"
set "SDKVER=10.0.26100.0"

if not exist "%MSVC%\lib\x64" (
    echo [X] MSVC toolchain not found: %MSVC%
    echo     Open this bat and fix the MSVC line to match your machine.
    pause
    exit /b 1
)

set "LIB=%MSVC%\lib\x64;%SDK%\lib\%SDKVER%\um\x64;%SDK%\lib\%SDKVER%\ucrt\x64"
set "INCLUDE=%MSVC%\include;%SDK%\Include\%SDKVER%\ucrt;%SDK%\Include\%SDKVER%\um;%SDK%\Include\%SDKVER%\shared"

echo [1/3] Compiling and linking (Release / NativeAOT, a few minutes)...
dotnet publish -c Release -r win-x64 --self-contained
if errorlevel 1 (
    echo.
    echo [X] Publish FAILED
    echo     If the error is MSB3021 / MSB3027, the launcher is still running.
    echo     Close it and try again.
    pause
    exit /b 1
)

set "PUBDIR=bin\Release\net10.0\win-x64\publish"

echo.
echo [2/3] Removing debug symbols (*.pdb, ~70 MB, useless at runtime)...
del /q "%PUBDIR%\*.pdb" 2>nul

echo [3/3] Done
echo.
echo Output: %CD%\%PUBDIR%
echo --------------------------------------------
for %%f in ("%PUBDIR%\*") do echo   %%~nxf   %%~zf bytes
echo --------------------------------------------
echo Release build contains NO self-test code:
echo   - 6 test files excluded (SelfTest, InheritTest, LaunchTest, ...)
echo   - test entry points removed by #if !PUBLISH_BUILD
echo.
pause
