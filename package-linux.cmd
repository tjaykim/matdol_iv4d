@echo off
setlocal
cd /d "%~dp0"

set PUB=%CD%\artifacts\publish\linux-x64
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet publish matdol.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "%PUB%"
if errorlevel 1 ( echo [matdol] publish failed & exit /b 1 )

dotnet run --project installer\linux\packager -c Release -- "%PUB%" "%CD%" "%CD%\artifacts\installers"
if errorlevel 1 ( echo [matdol] packaging failed & exit /b 1 )
endlocal
