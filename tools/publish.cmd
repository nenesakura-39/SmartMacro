@echo off
rem Builds the KuroLogic SmartMacro release: runs the tests, publishes a self-contained single-file
rem app for 64-bit Windows, adds the approved public documents, and zips it into the "release" folder.
rem
rem Version: read from <Version> in SmartMacro.csproj (change it there, not here).
rem
rem Public documents come ONLY from the release-docs folder, and only these five files:
rem   EULA.txt, LICENSE.txt, THIRD-PARTY-NOTICES.txt, DOTNET-THIRD-PARTY-NOTICES.txt, README.txt
rem Nothing else (source, docs\, PROJECT_STATUS.md, audit notes, logs, .pdb files) goes into the ZIP.
rem
rem Safety: if a document is missing, or still contains [PLACEHOLDER], [REVIEW], [VERIFY] or DRAFT,
rem the script stops. For a private test build you can run:  tools\publish.cmd --allow-drafts
rem That build is named ...-DRAFT-NOT-FOR-RELEASE.zip and must not be uploaded.
rem
rem Extra outputs next to the ZIP:
rem   release\SmartMacro-<version>-win-x64.zip.sha256.txt  (SHA-256 to post next to the download link)
rem   release\bundle-versions.txt                          (Microsoft parts inside the EXE + versions)
rem The script also checks that THIRD-PARTY-NOTICES.txt names every one of those parts and versions,
rem and stops a real release if it doesn't (for example after a .NET update).
setlocal EnableDelayedExpansion
cd /d "%~dp0.."

set VERSION=
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "$v = ([xml](Get-Content 'SmartMacro.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1; if ($v) { $v.Trim() }"`) do set VERSION=%%V
if not defined VERSION (
  echo Could not read ^<Version^> from SmartMacro.csproj.
  pause
  exit /b 1
)

set DOCS=release-docs
set DOCFILES=EULA.txt LICENSE.txt THIRD-PARTY-NOTICES.txt DOTNET-THIRD-PARTY-NOTICES.txt README.txt
set SUFFIX=
if /i "%~1"=="--allow-drafts" set SUFFIX=-DRAFT-NOT-FOR-RELEASE
set OUT=publish\SmartMacro-%VERSION%-win-x64%SUFFIX%
set ZIP=release\SmartMacro-%VERSION%-win-x64%SUFFIX%.zip
set DEPS=bin\Release\net10.0-windows10.0.19041.0\win-x64\SmartMacro.deps.json

echo KuroLogic SmartMacro %VERSION%%SUFFIX%
echo [1/6] Checking the public documents in %DOCS%\ ...
set DOCPROBLEM=
for %%F in (%DOCFILES%) do (
  if not exist "%DOCS%\%%F" (
    echo   MISSING: %DOCS%\%%F
    set DOCPROBLEM=1
  ) else (
    for %%M in ("[PLACEHOLDER" "[REVIEW" "[VERIFY" "DRAFT") do (
      findstr /l /i /c:%%M "%DOCS%\%%F" >nul && (
        echo   NOT FINAL: %DOCS%\%%F still contains %%~M
        set DOCPROBLEM=1
      )
    )
  )
)
rem The EULA and README must name the version that is being built.
findstr /l /c:"%VERSION%" "%DOCS%\EULA.txt" >nul || (
  echo   NOT FINAL: %DOCS%\EULA.txt does not mention version %VERSION%
  set DOCPROBLEM=1
)
findstr /l /c:"%VERSION%" "%DOCS%\README.txt" >nul || (
  echo   NOT FINAL: %DOCS%\README.txt does not mention version %VERSION%
  set DOCPROBLEM=1
)
if defined DOCPROBLEM (
  if "%SUFFIX%"=="" (
    echo.
    echo The public documents are not final. Finish them first,
    echo or run "tools\publish.cmd --allow-drafts" for a private test build.
    pause
    exit /b 1
  )
  echo   Continuing because of --allow-drafts. This build is NOT for release.
)

rem The app icon (Assets\SmartMacro.ico, made by tools\generate-icon.py) is built into SmartMacro.exe.
rem Check it has every Windows size, so the taskbar / Explorer / Alt+Tab never show a blurry, scaled frame.
echo       Checking Assets\SmartMacro.ico ...
powershell -NoProfile -Command "$p='Assets\SmartMacro.ico'; if (-not (Test-Path $p)) { Write-Host '  ICON MISSING: run python tools\generate-icon.py'; exit 1 }; $b=[IO.File]::ReadAllBytes($p); $n=[BitConverter]::ToUInt16($b,4); $s=@(); for ($i=0; $i -lt $n; $i++) { $w=[int]$b[6+16*$i]; if ($w -eq 0) { $w=256 }; $s+=$w }; $miss=@(16,24,32,48,64,128,256 | Where-Object { $s -notcontains $_ }); if ($miss.Count -gt 0) { Write-Host ('  ICON INCOMPLETE: missing ' + ($miss -join ', ') + ' px. Run python tools\generate-icon.py'); exit 1 }; Write-Host ('  Icon OK: ' + ($s -join ', ') + ' px')"
if errorlevel 1 (
  pause
  exit /b 1
)

rem Close any running SmartMacro.exe instance first so it cannot lock files in publish\ or leave an old build open.
tasklist /fi "imagename eq SmartMacro.exe" 2>nul | find /i "SmartMacro.exe" >nul && (
  echo       Closing running SmartMacro.exe instance before publishing...
  taskkill /f /im SmartMacro.exe >nul 2>&1
  powershell -NoProfile -Command "Start-Sleep -Milliseconds 400"
)

if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%\SmartMacro.exe" (
  echo Could not remove old %OUT%\SmartMacro.exe ^(file is locked^). Close SmartMacro and try again.
  pause
  exit /b 1
)
if not exist release mkdir release
rem A real release replaces any old private test build of the same version, so the two can't be mixed up.
if "%SUFFIX%"=="" (
  if exist "publish\SmartMacro-%VERSION%-win-x64-DRAFT-NOT-FOR-RELEASE" rmdir /s /q "publish\SmartMacro-%VERSION%-win-x64-DRAFT-NOT-FOR-RELEASE"
  if exist "release\SmartMacro-%VERSION%-win-x64-DRAFT-NOT-FOR-RELEASE.zip" del /q "release\SmartMacro-%VERSION%-win-x64-DRAFT-NOT-FOR-RELEASE.zip"
)

echo [2/6] Running tests...
dotnet test SmartMacro.Tests\SmartMacro.Tests.csproj -c Release -nologo > release\test.log 2>&1
if errorlevel 1 (
  echo Tests FAILED. See release\test.log
  pause
  exit /b 1
)

echo [3/6] Publishing (this can take a few minutes the first time)...
dotnet publish SmartMacro.csproj -c Release -r win-x64 --self-contained true -nologo ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:SatelliteResourceLanguages=en -p:DebugType=none -o "%OUT%" > release\publish.log 2>&1
if errorlevel 1 (
  echo Publish FAILED. See release\publish.log
  pause
  exit /b 1
)

echo [4/6] Adding the public documents...
for %%F in (%DOCFILES%) do (
  if exist "%DOCS%\%%F" copy /y "%DOCS%\%%F" "%OUT%\%%F" >nul
)

rem Only SmartMacro.exe and the five documents may be in the package. Stop if anything else appeared.
set EXTRA=
for %%F in ("%OUT%\*") do (
  set NAME=%%~nxF
  set ALLOWED=
  if /i "!NAME!"=="SmartMacro.exe" set ALLOWED=1
  for %%D in (%DOCFILES%) do if /i "!NAME!"=="%%D" set ALLOWED=1
  if not defined ALLOWED (
    echo   UNEXPECTED FILE in the package: !NAME!
    set EXTRA=1
  )
)
for /d %%D in ("%OUT%\*") do (
  echo   UNEXPECTED FOLDER in the package: %%~nxD
  set EXTRA=1
)
if defined EXTRA (
  echo Packaging stopped: remove the unexpected files above or update this script on purpose.
  pause
  exit /b 1
)

echo [5/6] Checking THIRD-PARTY-NOTICES.txt against the Microsoft components inside SmartMacro.exe...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\bundle-versions.ps1" -DepsJson "%DEPS%" -Out "release\bundle-versions.txt" -Notices "%DOCS%\THIRD-PARTY-NOTICES.txt"
if errorlevel 1 (
  echo   The notices don't match this build. Details: release\bundle-versions.txt
  echo   Update the names / versions in %DOCS%\THIRD-PARTY-NOTICES.txt, then run this again.
  if "%SUFFIX%"=="" (
    pause
    exit /b 1
  )
  echo   Continuing because of --allow-drafts. This build is NOT for release.
) else (
  echo   OK - every bundled component is listed. Details: release\bundle-versions.txt
)

echo [6/6] Zipping...
if exist "%ZIP%" del /q "%ZIP%"
if exist "%ZIP%.sha256.txt" del /q "%ZIP%.sha256.txt"
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Zipping FAILED.
  pause
  exit /b 1
)
powershell -NoProfile -Command "$h = (Get-FileHash '%ZIP%' -Algorithm SHA256).Hash; ($h + '  ' + (Split-Path '%ZIP%' -Leaf)) | Set-Content -Path '%ZIP%.sha256.txt' -Encoding ASCII"

echo.
echo Done!
powershell -NoProfile -Command "$exe = Get-Item '%OUT%\SmartMacro.exe'; Write-Host ('  Standalone EXE : ' + $exe.FullName + '  (built ' + $exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + ')')"
echo   Release ZIP    : %ZIP%
echo It contains:
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path '%ZIP%')); $z.Entries | ForEach-Object { '  ' + $_.FullName + '  (' + $_.Length + ' bytes)' }; $z.Dispose()"
if exist "%ZIP%.sha256.txt" (
  echo SHA-256:
  type "%ZIP%.sha256.txt"
)
echo.
echo Open the ZIP and check it by hand before uploading.
if not "%SUFFIX%"=="" echo REMINDER: this is a DRAFT build. Do not upload it.
pause
endlocal
