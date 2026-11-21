[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageZip,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $ExpectedPackageSha256,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $PackageRevision,
    [string] $CompilerPath,
    [switch] $AllowDevelopment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer\PackagePolicy.ps1')

$repo = $PSScriptRoot
$compilerVersion = '7.1.1'
$expectedCompilerSha = 'e0f9f1dc32032650c6629f6dee41eb6d20ba140e4e3c07c25d8a24f9ba781644'
$version = ([xml](Get-Content (Join-Path $repo 'Afterbreak.csproj') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$' -or
    @($version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) {
    throw 'Afterbreak.csproj must specify a numeric major.minor.patch version.'
}

function Get-InstallerSource {
    $head = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the installer source revision.' }
    $changes = @(& git -C $repo --no-optional-locks status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Could not check whether installer source is clean.' }
    $hashes = [ordered]@{}
    foreach ($path in @('Afterbreak.csproj', 'Build-Installer.ps1', 'installer/Afterbreak.iss',
        'installer/InstallPolicy.iss', 'installer/PackagePolicy.ps1')) {
        $hashes[$path] = Get-Sha256 (Join-Path $repo $path)
    }
    return [ordered]@{ revision = $head; dirty = [bool]$changes.Count; files = $hashes }
}

$destination = Get-InstallerDestination $repo $OutputDirectory
$source = Get-InstallerSource
if (-not $PackageRevision) { $PackageRevision = $source.revision }
if (($source.dirty -or ($PackageRevision -ne $source.revision)) -and -not $AllowDevelopment) {
    throw 'Use matching clean installer source and application package. -AllowDevelopment marks a compile-only candidate.'
}
if (-not $CompilerPath) { $CompilerPath = $env:AFTERBREAK_ISCC }
if (-not $CompilerPath) {
    $CompilerPath = Join-Path (Split-Path -Parent $repo) ".tools\inno-$compilerVersion\ISCC.exe"
}
$compiler = (Get-Item -LiteralPath $CompilerPath).FullName
if ((Get-Sha256 $compiler) -ne $expectedCompilerSha) {
    throw "Use the pinned official Inno Setup $compilerVersion x64 ISCC.exe."
}
$reportedVersion = & $compiler --version
if ($LASTEXITCODE -ne 0 -or $reportedVersion -ne $compilerVersion) {
    throw "Could not verify Inno Setup $compilerVersion."
}

$zip = (Get-Item -LiteralPath $PackageZip).FullName
$parent = [IO.Path]::GetDirectoryName($destination)
$stage = Join-Path $parent ".afterbreak-installer-$([Guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($stage)
$payload = Join-Path $stage 'payload'
$artifact = Join-Path $stage 'artifact'
try {
    $package = Expand-VerifiedPackage $zip $ExpectedPackageSha256 $PackageRevision $payload
    [void][IO.Directory]::CreateDirectory($artifact)
    $script = Join-Path $repo 'installer\Afterbreak.iss'
    & $compiler "/DAppVersion=$version" "/DPayloadRoot=$payload" "/DManifestHash=$($package.manifestSha256)" "/O$artifact" $script
    if ($LASTEXITCODE -ne 0) { throw "ISCC exited with code $LASTEXITCODE." }

    $name = "Afterbreak-$version-win-x64-Setup.exe"
    $installer = Join-Path $artifact $name
    $installerSha = Get-Sha256 $installer
    $metadata = [ordered]@{
        format = 1
        version = $version
        development = [bool]$AllowDevelopment
        source = $source
        packageRevision = $package.revision
        packageSha256 = $ExpectedPackageSha256.ToLowerInvariant()
        payloadManifestSha256 = $package.manifestSha256
        payloadFileCount = $package.fileCount
        compiler = $compilerVersion
        compilerSha256 = $expectedCompilerSha
        installerSha256 = $installerSha
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $artifact "$name.sha256"), "$installerSha  $name`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $artifact 'installer-build-info.json'), ($metadata | ConvertTo-Json -Depth 5) + "`n", $utf8)
    if ((Get-InstallerSource | ConvertTo-Json -Depth 5 -Compress) -cne ($source | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Installer source changed during compilation.'
    }
    [IO.Directory]::Move($artifact, $destination)
    Write-Host "Installer: $(Join-Path $destination $name)"
    Write-Host "Verified compile inputs: $stage"
} catch {
    throw "Installer build failed. Staging files are at $stage. $($_.Exception.Message)"
}
