@echo off
rem Builds the solution and runs the unit tests, writing results to artifacts\*.log
rem (used by the AI developer to verify changes on this PC).
setlocal
cd /d "%~dp0.."
if not exist artifacts mkdir artifacts
echo Building...
dotnet build SmartMacro.slnx -c Debug -nologo > artifacts\build.log 2>&1
>> artifacts\build.log echo BUILD_EXIT=%ERRORLEVEL%
echo Testing...
dotnet test SmartMacro.Tests\SmartMacro.Tests.csproj -c Debug -nologo --no-build > artifacts\test.log 2>&1
>> artifacts\test.log echo TEST_EXIT=%ERRORLEVEL%
echo Done. See artifacts\build.log and artifacts\test.log
endlocal
