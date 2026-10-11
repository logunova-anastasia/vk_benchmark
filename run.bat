@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
dotnet run --project "src\WukongBenchmarkRunner\WukongBenchmarkRunner.csproj" -c Release -- %*
set "exitcode=%errorlevel%"
if not "%exitcode%"=="0" echo Runner returned an error. Check report.txt in the latest results folder.
pause
exit /b %exitcode%
