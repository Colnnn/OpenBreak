[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ExpectedRevision,
    [string] $DotNetPath,
    [string] $CompilerPath,
    [switch] $DownloadCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repository 'installer\PackagePolicy.ps1')

function Get-PortableCompiler($output) {
    $url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
    $sha256 = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
    $download = Join-Path $output 'innosetup-7.1.1-x64.exe'
    $directory = Join-Path $output 'inno-7.1.1'
    $log = Join-Path $output 'inno-extraction.log'

    Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec 120
    if ((Get-Sha256 $download) -ne $sha256) { throw 'Inno Setup download checksum does not match the pinned release.' }

    $arguments = @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=`"$directory`"", "/LOG=`"$log`"")
    $process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Portable Inno Setup extraction failed with exit code $($process.ExitCode). See $log." }

    $compiler = Join-Path $directory 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Portable Inno Setup did not provide ISCC.exe.' }
    return $compiler
}

$revision = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the source revision.' }
if ($revision -ne $ExpectedRevision) { throw 'The checked-out revision differs from the expected revision.' }
$changes = @(& git -C $repository --no-optional-locks status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Windows CI requires a clean checkout.' }
if ($DownloadCompiler -and ($CompilerPath -or $env:GITHUB_ACTIONS -ne 'true')) {
    throw 'Use the portable compiler download on GitHub Actions, or pass a verified -CompilerPath for local runs.'
}

$output = Get-InstallerDestination $repository $OutputDirectory
[void][IO.Directory]::CreateDirectory($output)
if ($DownloadCompiler) { $CompilerPath = Get-PortableCompiler $output }

$package = Join-Path $output 'package'
$installer = Join-Path $output 'installer'
$build = Join-Path $repository 'Build-Afterbreak.ps1'
& $build -Action Test -Configuration Release -DotNetPath $DotNetPath
& $build -Action Package -Configuration Release -OutputDirectory $package -DotNetPath $DotNetPath

$zip = Join-Path $package 'Afterbreak-win-x64.zip'
$packageHash = Get-Sha256 $zip
& (Join-Path $repository 'Build-Installer.ps1') -PackageZip $zip -ExpectedPackageSha256 $packageHash `
    -PackageRevision $revision -OutputDirectory $installer -CompilerPath $CompilerPath

$info = Get-Content -LiteralPath (Join-Path $installer 'installer-build-info.json') -Raw | ConvertFrom-Json
$setupHash = Get-Sha256 (Join-Path $installer "Afterbreak-$($info.version)-win-x64-Setup.exe")
if ($setupHash -ne $info.installerSha256) { throw 'The compiled installer checksum does not match its build receipt.' }
$results = @(
    '### Windows build verification'
    "- Source revision: $revision"
    "- Release package SHA-256: $packageHash"
    "- Installer SHA-256: $setupHash"
)
$results | Write-Host
if ($env:GITHUB_STEP_SUMMARY) { $results | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 }
