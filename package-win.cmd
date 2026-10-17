@echo off
setlocal
cd /d "%~dp0"

set VER=
for /f "usebackq delims=" %%v in (`dotnet msbuild matdol.csproj -nologo -getProperty:Version`) do set VER=%%v
if not defined VER ( echo [matdol] could not read Version from matdol.csproj - .NET SDK 8.0.100 or later required & exit /b 1 )
echo [matdol] version %VER%

set PUB=%CD%\artifacts\publish\win-x64
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet publish matdol.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "%PUB%"
if errorlevel 1 ( echo [matdol] publish failed & exit /b 1 )

set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo [matdol] Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup
  exit /b 1
)

"%ISCC%" /Qp /DMyAppVersion=%VER% "/DSourceDir=%PUB%" installer\windows\matdol.iss
if errorlevel 1 ( echo [matdol] installer compile failed & exit /b 1 )
mkdir "%PUB%\legal" 2>nul
copy /y LICENSE "%PUB%\legal\LICENSE.txt" >nul
copy /y Assets\Legal\Privacy.txt "%PUB%\legal\" >nul
copy /y Assets\Legal\ThirdPartyNotices.txt "%PUB%\legal\" >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path '%PUB%\*' -DestinationPath '%CD%\artifacts\installers\matdol-%VER%-win-x64-portable.zip'"
if errorlevel 1 ( echo [matdol] portable zip failed & exit /b 1 )
copy /y "artifacts\installers\matdol-%VER%-win-x64-setup.exe" "artifacts\installers\matdol-win-x64-setup.exe" >nul
copy /y "artifacts\installers\matdol-%VER%-win-x64-portable.zip" "artifacts\installers\matdol-win-x64-portable.zip" >nul
if errorlevel 1 ( echo [matdol] copy to version-less names failed & exit /b 1 )
echo.
echo [matdol] done: artifacts\installers\matdol-%VER%-win-x64-setup.exe and -portable.zip
echo [matdol]       plus version-less copies matdol-win-x64-setup.exe and matdol-win-x64-portable.zip
endlocal
