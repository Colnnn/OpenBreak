@echo off
setlocal
if not exist "%~dp0Afterbreak.csproj" (
    echo This launcher needs Afterbreak.csproj beside it.
    echo Open the Afterbreak repository folder and run Run-Afterbreak.cmd from there.
    pause
    exit /b 1
)
dotnet build "%~dp0Afterbreak.csproj" -c Debug
if errorlevel 1 goto :build_failed
start "" "%~dp0bin\Debug\net8.0-windows10.0.22621.0\win-x64\Afterbreak.exe"
exit /b %errorlevel%

:build_failed
set "build_status=%errorlevel%"
echo.
echo Afterbreak could not build. See the first build error above.
pause
exit /b %build_status%
