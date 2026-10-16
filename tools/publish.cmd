@echo off
rem Builds the SmartMacro release: runs the tests, then publishes a self-contained
rem single-file app for 64-bit Windows and zips it into the "release" folder.
setlocal
cd /d "%~dp0.."
set VERSION=1.0.0
set OUT=publish\SmartMacro-%VERSION%-win-x64
set ZIP=release\SmartMacro-%VERSION%-win-x64.zip

if exist "%OUT%" rmdir /s /q "%OUT%"
if not exist release mkdir release

echo [1/3] Running tests...
dotnet test SmartMacro.Tests\SmartMacro.Tests.csproj -c Release -nologo > release\test.log 2>&1
if errorlevel 1 (
  echo Tests FAILED. See release\test.log
  pause
  exit /b 1
)

echo [2/3] Publishing (this can take a few minutes the first time)...
dotnet publish SmartMacro.csproj -c Release -r win-x64 --self-contained true -nologo ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:SatelliteResourceLanguages=en -p:DebugType=none -o "%OUT%" > release\publish.log 2>&1
if errorlevel 1 (
  echo Publish FAILED. See release\publish.log
  pause
  exit /b 1
)

copy /y README.md "%OUT%\README.md" >nul

echo [3/3] Zipping...
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Zipping FAILED.
  pause
  exit /b 1
)

echo.
echo Done! Release package: %ZIP%
echo App folder: %OUT%
pause
endlocal
