@echo off
title Galactic Tune-Up Launcher
cd /d "%~dp0"

:: 1. Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :init
) else (
    goto :elevate
)

:elevate
echo Requesting Administrator privileges to initialize the Galactic Tune-Up background service...
powershell -Command "Start-Process -FilePath '%0' -Verb RunAs"
exit /b

:init
echo ==================================================
echo         GALACTIC TUNE-UP LAUNCHER        
echo ==================================================
echo.

:: 2. Check if build exists, compile if missing
set "ENGINE_EXE=TuneUpEngine\bin\Debug\net11.0-windows\TuneUpEngine.exe"
set "UI_EXE=TuneUpUI\bin\Debug\net11.0-windows\TuneUpUI.exe"

if not exist "%ENGINE_EXE%" (
    echo [Build] Build binaries missing. Compiling the solution...
    dotnet build TuneUpApp.slnx
    if errorlevel 1 (
        echo [Error] Compilation failed! Please verify your .NET SDK installation.
        pause
        exit /b
    )
) else (
    if not exist "%UI_EXE%" (
        echo [Build] Build binaries missing. Compiling the solution...
        dotnet build TuneUpApp.slnx
        if errorlevel 1 (
            echo [Error] Compilation failed! Please verify your .NET SDK installation.
            pause
            exit /b
        )
    )
)

:: The background engine is now automatically started completely silently and elevated by the GUI.
:: No manual launching is needed here.

echo [Launch] Starting premium user interface...
:: Start the UI in the foreground
start "" "%UI_EXE%"

echo [Launch] Galactic Tune-Up is running.
timeout /t 3 >nul
exit
