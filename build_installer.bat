@echo off
setlocal enabledelayedexpansion

:: ==========================================================================
::  STORM REMOTE CONTROL - Build and Package Installer
::  Usage: build_installer.bat [--skip-publish] [--skip-installer]
:: ==========================================================================

set "ROOT=%~dp0"
set "PROJECT=%ROOT%StormRemoteControl\StormRemoteControl\StormRemoteControl.csproj"
set "ISS_FILE=%ROOT%Installer\StormRemoteControl.iss"
set "OUTPUT_DIR=%ROOT%installer_output"

set "SKIP_PUBLISH=0"
set "SKIP_INSTALLER=0"

:: --- Parse arguments -------------------------------------------------------
:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="--skip-publish"   set "SKIP_PUBLISH=1"   & shift & goto :parse_args
if /I "%~1"=="--skip-installer" set "SKIP_INSTALLER=1"  & shift & goto :parse_args
echo [WARN] Unknown argument: %~1
shift
goto :parse_args
:args_done

:: --- Banner ----------------------------------------------------------------
echo.
echo  ============================================================
echo   STORM REMOTE CONTROL - Build and Package (v1.0.0)
echo  ============================================================
echo.

:: --- Step 1: dotnet publish ------------------------------------------------
if "%SKIP_PUBLISH%"=="1" (
    echo [SKIP] dotnet publish -- skipped via --skip-publish flag.
    echo.
) else (
    echo [1/2] Publishing Release ^| x64 ^| self-contained ...
    echo.

    dotnet publish "%PROJECT%" ^
        -c Release ^
        -r win-x64 ^
        -p:Platform=x64 ^
        -p:PublishReadyToRun=true ^
        --self-contained true ^
        -o "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish"

    if errorlevel 1 (
        echo.
        echo [ERROR] dotnet publish failed. Aborting.
        exit /b 1
    )

    if not exist "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\STORM REMOTE CONTROL.exe" (
        echo [ERROR] STORM REMOTE CONTROL.exe not found in publish dir!
        exit /b 1
    )

    echo [INFO] Copying missing WinUI 3 resources to publish dir...
    xcopy "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\*.xbf" "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\" /Y /I
    xcopy "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\*.pri" "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\" /Y /I
    xcopy "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Assets\*.*" "%ROOT%StormRemoteControl\StormRemoteControl\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\Assets\" /E /Y /I

    echo.
    echo [OK] Publish succeeded.
    echo.
)

:: --- Step 2: Inno Setup Compiler -------------------------------------------
if "%SKIP_INSTALLER%"=="1" (
    echo [SKIP] Inno Setup compiler -- skipped via --skip-installer flag.
    echo.
    goto :done
)

echo [2/2] Compiling installer with Inno Setup ...
echo.

:: Try to locate iscc.exe
set "ISCC="

:: Check PATH first
where iscc.exe >nul 2>&1
if not errorlevel 1 (
    set "ISCC=iscc.exe"
    goto :found_iscc
)

:: Common install locations
for %%P in (
    "C:\Program Files (x86)\Inno Setup\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
    "C:\Program Files\Inno Setup 5\ISCC.exe"
) do (
    if exist %%P (
        set "ISCC=%%~P"
        goto :found_iscc
    )
)

echo [ERROR] Could not find Inno Setup Compiler (ISCC.exe).
echo         Please install Inno Setup 6 from https://jrsoftware.org/isinfo.php
echo         or add ISCC.exe to your PATH.
exit /b 1

:found_iscc
echo Using: %ISCC%
echo.

"%ISCC%" "%ISS_FILE%"

if errorlevel 1 (
    echo.
    echo [ERROR] Inno Setup compilation failed. Aborting.
    exit /b 1
)

echo.
echo [OK] Installer created successfully.
echo.

:done
:: --- Summary ---------------------------------------------------------------
if exist "%OUTPUT_DIR%\STORM_REMOTE_CONTROL_1.0.0_Setup.exe" (
    echo  ============================================================
    echo   OUTPUT: %OUTPUT_DIR%\STORM_REMOTE_CONTROL_1.0.0_Setup.exe
    echo  ============================================================
) else (
    echo  [INFO] Installer output created in %OUTPUT_DIR%
)

echo.
echo Done.
endlocal
exit /b 0
