@echo off
setlocal

cd /d "%~dp0"

set "PROJECT=RamLimiter.csproj"
set "CONFIG=Release"
set "PLATFORM=AnyCPU"
set "MSBUILD_EXE="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not "%~1"=="" set "CONFIG=%~1"
if not "%~2"=="" set "PLATFORM=%~2"

if exist "%VSWHERE%" (
    for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
    )
)

if not defined MSBUILD_EXE (
    for %%I in (
        "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    ) do (
        if not defined MSBUILD_EXE if exist %%~I set "MSBUILD_EXE=%%~I"
    )
)

if not defined MSBUILD_EXE (
    for /f "delims=" %%I in ('where msbuild.exe 2^>nul') do (
        if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
    )
)

if not defined MSBUILD_EXE (
    for %%I in (
        "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
        "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    ) do (
        if not defined MSBUILD_EXE if exist %%~I set "MSBUILD_EXE=%%~I"
    )
)

if not defined MSBUILD_EXE (
    echo ERROR: Could not find MSBuild.exe.
    echo Install Visual Studio Build Tools or Visual Studio with MSBuild and .NET Framework 4.7.2 targeting pack.
    echo https://visualstudio.microsoft.com/downloads/
    exit /b 1
)

echo Using MSBuild:
echo %MSBUILD_EXE%
echo.
echo Building %PROJECT% ^(%CONFIG%^|%PLATFORM%^)
echo.

"%MSBUILD_EXE%" "%PROJECT%" /t:Build /m /nologo /verbosity:minimal /p:Configuration=%CONFIG%;Platform=%PLATFORM%
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

echo.
echo Build succeeded.
echo Output: "%CD%\bin\%CONFIG%\RAMLimiter.exe"
exit /b 0
