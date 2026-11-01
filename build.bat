@echo off
rem Builds the single dist\MarsForge.exe (needs the .NET 10 SDK: https://dotnet.microsoft.com/download).
rem 1) publishes the app into build\app, 2) builds the small launcher, 3) packs the app inside the launcher.
cd /d "%~dp0"
echo Building MarsForge... (the first time takes a few minutes)
if exist dist rmdir /s /q dist
if exist build rmdir /s /q build
dotnet publish src\MarsForge.App\MarsForge.App.csproj -c Release -r win-x64 --self-contained true -o build\app > build.log 2>&1
if errorlevel 1 goto failed
dotnet build src\MarsForge.Launcher\MarsForge.Launcher.csproj -c Release -o build\launcher >> build.log 2>&1
if errorlevel 1 goto failed
echo Packing everything into one MarsForge.exe...
start "" /wait build\launcher\MarsForge.exe --pack build\app dist\MarsForge.exe
if errorlevel 1 goto failed
rmdir /s /q build
echo Done: dist\MarsForge.exe
exit /b 0
:failed
echo Build FAILED. See build.log
exit /b 1
