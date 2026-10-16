@echo off
rem Builds the KuroLogic SmartMacro release: runs the tests, publishes a self-contained single-file
rem app for 64-bit Windows, adds the approved public documents, and zips it into the "release" folder.
rem
rem Public documents come ONLY from the release-docs folder, and only these five files:
rem   EULA.txt, LICENSE.txt, THIRD-PARTY-NOTICES.txt, DOTNET-THIRD-PARTY-NOTICES.txt, README.txt
rem Nothing else (source, docs\, PROJECT_STATUS.md, audit notes, logs, .pdb files) goes into the ZIP.
rem
rem Safety: if a document is missing, or still contains [PLACEHOLDER], [REVIEW], [VERIFY] or DRAFT,
rem the script stops. For a private test build you can run:  tools\publish.cmd --allow-drafts
rem That build is named ...-DRAFT-NOT-FOR-RELEASE.zip and must not be uploaded.
setlocal EnableDelayedExpansion
cd /d "%~dp0.."
set VERSION=1.0.0
set DOCS=release-docs
set DOCFILES=EULA.txt LICENSE.txt THIRD-PARTY-NOTICES.txt DOTNET-THIRD-PARTY-NOTICES.txt README.txt
set SUFFIX=
if /i "%~1"=="--allow-drafts" set SUFFIX=-DRAFT-NOT-FOR-RELEASE
set OUT=publish\SmartMacro-%VERSION%-win-x64%SUFFIX%
set ZIP=release\SmartMacro-%VERSION%-win-x64%SUFFIX%.zip

echo [1/5] Checking the public documents in %DOCS%\ ...
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
if defined DOCPROBLEM (
  if "%SUFFIX%"=="" (
    echo.
    echo The public documents are not final. Finish them first ^(see RELEASE_CHECKLIST.md^),
    echo or run "tools\publish.cmd --allow-drafts" for a private test build.
    pause
    exit /b 1
  )
  echo   Continuing because of --allow-drafts. This build is NOT for release.
)

if exist "%OUT%" rmdir /s /q "%OUT%"
if not exist release mkdir release

echo [2/5] Running tests...
dotnet test SmartMacro.Tests\SmartMacro.Tests.csproj -c Release -nologo > release\test.log 2>&1
if errorlevel 1 (
  echo Tests FAILED. See release\test.log
  pause
  exit /b 1
)

echo [3/5] Publishing (this can take a few minutes the first time)...
dotnet publish SmartMacro.csproj -c Release -r win-x64 --self-contained true -nologo ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:SatelliteResourceLanguages=en -p:DebugType=none -o "%OUT%" > release\publish.log 2>&1
if errorlevel 1 (
  echo Publish FAILED. See release\publish.log
  pause
  exit /b 1
)

echo [4/5] Adding the public documents...
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

echo [5/5] Zipping...
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Zipping FAILED.
  pause
  exit /b 1
)

echo.
echo Done! Release package: %ZIP%
echo It contains:
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead((Resolve-Path '%ZIP%')).Entries | ForEach-Object { '  ' + $_.FullName + '  (' + $_.Length + ' bytes)' }"
echo.
echo Open the ZIP and check it by hand before uploading (RELEASE_CHECKLIST.md).
if not "%SUFFIX%"=="" echo REMINDER: this is a DRAFT build. Do not upload it.
pause
endlocal
