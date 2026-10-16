@echo off
rem Builds the solution, then runs the unit tests against the binaries that were just built.
rem - Stops (and skips the tests) if the build fails, so old test DLLs are never tested by mistake.
rem - Exit code: 0 = build and tests passed, otherwise the failing step's exit code.
rem - Logs: artifacts\build.log, artifacts\test.log and artifacts\test-results.trx
setlocal
cd /d "%~dp0.."
if not exist artifacts mkdir artifacts

rem Start from clean logs so an old "passed" result can't be mistaken for this run.
if exist artifacts\build.log del /q artifacts\build.log
if exist artifacts\test.log del /q artifacts\test.log
if exist artifacts\test-results.trx del /q artifacts\test-results.trx

echo Building...
dotnet build SmartMacro.slnx -c Debug -nologo > artifacts\build.log 2>&1
set "BUILD_EXIT=%ERRORLEVEL%"
>> artifacts\build.log echo BUILD_EXIT=%BUILD_EXIT%
if not "%BUILD_EXIT%"=="0" goto build_failed

echo Testing...
rem --no-build is safe here: the build above just succeeded for the same configuration.
dotnet test SmartMacro.Tests\SmartMacro.Tests.csproj -c Debug -nologo --no-build --logger "trx;LogFileName=test-results.trx" --results-directory artifacts > artifacts\test.log 2>&1
set "TEST_EXIT=%ERRORLEVEL%"
>> artifacts\test.log echo TEST_EXIT=%TEST_EXIT%
if not "%TEST_EXIT%"=="0" goto tests_failed

echo OK: build and tests passed. See artifacts\build.log and artifacts\test.log
endlocal & exit /b 0

:build_failed
> artifacts\test.log echo Tests were not run because the build failed (BUILD_EXIT=%BUILD_EXIT%).
>> artifacts\test.log echo TEST_EXIT=NOT_RUN
echo FAILED: the build failed. See artifacts\build.log
endlocal & exit /b %BUILD_EXIT%

:tests_failed
echo FAILED: some tests failed. See artifacts\test.log
endlocal & exit /b %TEST_EXIT%
