@echo off
setlocal
dotnet build "%~dp0Afterbreak.csproj" -c Debug
if errorlevel 1 goto :build_failed
start "" "%~dp0bin\Debug\net8.0-windows10.0.22621.0\win-x64\Afterbreak.exe"
exit /b %errorlevel%

:build_failed
set "build_status=%errorlevel%"
echo.
echo Afterbreak could not build. Check the output above for the missing SDK or package source.
pause
exit /b %build_status%
