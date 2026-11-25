# Build OpenBreak from source

Use Windows 11 x64, [Git](https://git-scm.com/download/win), [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows), and the x64 [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) version pinned in [`global.json`](../global.json), currently **10.0.404**. You’ll need access to the NuGet feed in [`NuGet.Config`](../NuGet.Config) for restore. Dependencies are restored in locked mode.

In PowerShell, clone the repository and run the build and checks.

```powershell
git clone https://github.com/Colnnn/OpenBreak.git
Set-Location OpenBreak
dotnet --version
.\build\Build-OpenBreak.ps1 -Action Build
.\build\Build-OpenBreak.ps1 -Action Test
```

If you installed the pinned SDK outside your PATH, pass `-DotNetPath` with the full path to its `dotnet.exe` on each command. The test action runs the placement, reminder, session, storage, launch, and installer-policy checks.

To build and start the app, run `.\build\Build-OpenBreak.ps1 -Action Launch`. Quit any running copy from the system tray first. By default, a source build uses the same local task database as the installed app. See [Manage local data](usage.md#manage-local-data) before switching between builds.

## Package for Windows

From a clean checkout, choose a new output directory outside the repository with an existing parent directory. For example, if the repository is outside your Windows temp directory, use the following command.

```powershell
$packageDirectory = Join-Path $env:TEMP "OpenBreak-package-$([guid]::NewGuid().ToString('N'))"
.\build\Build-OpenBreak.ps1 -Action Package -OutputDirectory $packageDirectory
```

This writes a self-contained `OpenBreak-win-x64.zip`, its SHA-256 checksum, and the unpacked app. The package includes dependency notices and build provenance.

The installer requires the official [Inno Setup 7.1.1 x64 compiler](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1). Pass the path to its `ISCC.exe` using `-CompilerPath`. The build helper checks the compiler hash and carries the source revision into the package and the installer receipt.

```powershell
$revision = git rev-parse HEAD
$buildDirectory = Join-Path $env:TEMP "OpenBreak-build-$([guid]::NewGuid().ToString('N'))"
$compilerPath = Read-Host 'Full path to the pinned ISCC.exe'
.\.github\scripts\Build-Windows.ps1 -ExpectedRevision $revision -OutputDirectory $buildDirectory -CompilerPath $compilerPath
```

For a hosted preview candidate, run **Stage Windows preview** in GitHub Actions. Select the source ref and enter its full commit SHA. The workflow runs the Windows checks and makes the installer, standalone ZIP, and `SHA256SUMS.txt` available as a run artifact for review.
