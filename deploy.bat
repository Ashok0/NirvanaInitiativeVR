@echo off
setlocal

set CONFIGURATION=Release
set OUTPUT_DIR=bin\%CONFIGURATION%\net6.0
set MODS_DIR=C:\Program Files (x86)\Steam\steamapps\common\AI The Somnium Files 2\Mods\

echo === Building NirvanaVR (%CONFIGURATION%) ===
dotnet build -c %CONFIGURATION%
if errorlevel 1 (
    echo Build failed - aborting deploy.
    exit /b 1
)

echo === Deploying to Mods folder ===
xcopy "%OUTPUT_DIR%\NirvanaVR.dll" "%MODS_DIR%" /Y
if exist "%OUTPUT_DIR%\openvr_api.dll" (
    xcopy "%OUTPUT_DIR%\openvr_api.dll" "%MODS_DIR%" /Y
)

echo === Done ===
endlocal
