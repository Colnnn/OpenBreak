[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ExpectedRevision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repository 'installer\PackagePolicy.ps1')

# Build-Windows has already checked the ZIP manifest against the clean revision.
$installerDirectory = Join-Path $BuildDirectory 'installer'
$receipt = Get-Content -LiteralPath (Join-Path $installerDirectory 'installer-build-info.json') -Raw | ConvertFrom-Json
if ($receipt.source.revision -ne $ExpectedRevision -or $receipt.source.dirty -ne $false -or
    $receipt.packageRevision -ne $ExpectedRevision -or $receipt.development -ne $false) {
    throw 'The build receipt must identify a clean preview candidate at the expected revision.'
}
if ($receipt.version -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') {
    throw 'The installer build receipt must contain a numeric version.'
}

$zipName = 'OpenBreak-win-x64.zip'
$setupName = "OpenBreak-$($receipt.version)-win-x64-Setup.exe"
$zip = Join-Path $BuildDirectory "package\$zipName"
$setup = Join-Path $installerDirectory $setupName
$stage = Get-InstallerDestination $repository (Join-Path $BuildDirectory 'preview')
[void][IO.Directory]::CreateDirectory($stage)
Copy-Item -LiteralPath $zip, $setup -Destination $stage

$zipHash = Get-Sha256 (Join-Path $stage $zipName)
$setupHash = Get-Sha256 (Join-Path $stage $setupName)
if ($zipHash -ne $receipt.packageSha256 -or $setupHash -ne $receipt.installerSha256) {
    throw 'The staged ZIP or installer checksum differs from the build receipt.'
}
$checksums = "$setupHash  $setupName`n$zipHash  $zipName`n"
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
if ($env:GITHUB_OUTPUT) {
    "zip=$(Join-Path $stage $zipName)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8
    "installer=$(Join-Path $stage $setupName)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8
    "checksums=$(Join-Path $stage 'SHA256SUMS.txt')" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8
}
Write-Host "Preview artifacts for ${ExpectedRevision}: $stage"
