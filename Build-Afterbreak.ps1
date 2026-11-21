[CmdletBinding()]
param(
    [ValidateSet('Build', 'Test', 'Package', 'Launch')]
    [string] $Action = 'Build',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $DotNetPath,
    [string] $OutputDirectory,
    [switch] $AllowDirty,
    [switch] $CaptureMotionStudy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($CaptureMotionStudy -and $Action -ne 'Launch') {
    throw '-CaptureMotionStudy is only available with -Action Launch.'
}
$repository = $PSScriptRoot
$config = Join-Path $repository 'NuGet.Config'
$project = Join-Path $repository 'Afterbreak.csproj'
$sdkVersion = (Get-Content (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk.version
$runtimeVersion = '10.0.15'
$rid = 'win-x64'
$payloadName = "Afterbreak-$rid"
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Checked($executable, [string[]] $arguments) {
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$executable failed with exit code $LASTEXITCODE ($($arguments -join ' '))."
    }
}

function Get-PackageDestination {
    if (-not $OutputDirectory) { throw 'Supply -OutputDirectory with a new directory outside the checkout.' }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if ($path -notmatch '^[A-Za-z]:\\[^:]*$') {
        throw 'Package output requires an ordinary local drive path.'
    }
    $destination = [IO.Path]::GetFullPath($path)
    if ($destination -match '[. ](?:\\|$)') {
        throw 'Package output path components may not end in dots or spaces.'
    }
    $parent = [IO.Directory]::GetParent($destination)
    if (-not $parent -or -not $parent.Exists) { throw 'The package parent directory must exist.' }
    if ($destination.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith("$repository\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package output must be outside the checkout.'
    }
    if (Test-Path -LiteralPath $destination) { throw "Package output already exists: $destination" }
    for ($ancestor = $parent; $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Package output may not pass through a reparse point: $($ancestor.FullName)"
        }
    }
    return $destination
}

function Get-Hash($path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceState {
    $changes = @(Invoke-Checked 'git' @('--no-optional-locks', 'status', '--porcelain', '--untracked-files=all'))
    if ($changes.Count -and -not $AllowDirty) { throw 'Packaging requires a clean checkout. Use -AllowDirty for a development candidate.' }

    # Windows checkout paths cannot contain line breaks.
    [string[]] $paths = @(Invoke-Checked 'git' @('-c', 'core.quotepath=false', 'ls-files', '--cached', '--others', '--exclude-standard') |
        Select-Object -Unique)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $records = foreach ($path in $paths) {
        $file = Join-Path $repository $path
        if (Test-Path -LiteralPath $file -PathType Leaf) { "$(Get-Hash $file)  $path" } else { "missing  $path" }
    }
    return [ordered]@{
        revision = Invoke-Checked 'git' @('rev-parse', 'HEAD')
        dirty = [bool]$changes.Count
        sourceChecksums = ($records -join "`n") + "`n"
        status = $changes -join "`n"
    }
}

function Copy-DependencyNotices($payload) {
    $assets = Get-Content (Join-Path $repository 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $packages = @($assets.packageFolders.PSObject.Properties.Name)
    $notices = [IO.Directory]::CreateDirectory((Join-Path $payload 'ThirdParty')).FullName
    $runtimePackages = @("microsoft.netcore.app.runtime.win-x64/$runtimeVersion")
    $runtimePackages += @($assets.libraries.PSObject.Properties.Name |
        Where-Object { $_ -match '^Microsoft\.(WindowsAppSDK[^/]*|Web\.WebView2|Windows\.AI\.MachineLearning)/' })
    foreach ($name in $runtimePackages) {
        $packagePath = $name.ToLowerInvariant()
        $package = $packages | ForEach-Object { Join-Path $_ $packagePath } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $package) { throw "Restored package not found: $name" }
        $destination = [IO.Directory]::CreateDirectory((Join-Path $notices $packagePath)).FullName
        $files = @(Get-ChildItem -LiteralPath $package -File |
            Where-Object { $_.Name -match '^(license|notice|third-?party-?notices)\.txt$' })
        if ($files.Count -eq 0) { throw "Restored package contains no license files: $name" }
        foreach ($file in $files) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    Get-ChildItem (Join-Path $repository 'third-party') -Force |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $notices -Recurse }
}

function Write-Artifact($stage, $payload, $source) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $sourcePath = Join-Path $payload 'source-checksums.sha256'
    [IO.File]::WriteAllText($sourcePath, $source.sourceChecksums, $utf8)
    $info = [ordered]@{
        format = 1
        revision = $source.revision
        dirty = $source.dirty
        configuration = $Configuration
        sdk = $sdkVersion
        runtime = $runtimeVersion
        runtimeIdentifier = $rid
        sourceChecksumsSha256 = Get-Hash $sourcePath
        dependenciesLockSha256 = Get-Hash (Join-Path $repository 'packages.lock.json')
    }
    [IO.File]::WriteAllText((Join-Path $payload 'build-info.json'), ($info | ConvertTo-Json) + "`n", $utf8)
    Copy-Item -LiteralPath (Join-Path $repository 'packages.lock.json') -Destination (Join-Path $payload 'dependencies.lock.json')

    [string[]] $paths = @(Get-ChildItem -LiteralPath $payload -File -Force -Recurse |
        ForEach-Object { $_.FullName.Substring($payload.Length + 1).Replace('\', '/') })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $checksums = foreach ($path in $paths) { "$(Get-Hash (Join-Path $payload $path))  $path" }
    $manifest = 'files.sha256'
    [IO.File]::WriteAllText((Join-Path $payload $manifest), ($checksums -join "`n") + "`n", $utf8)
    $paths += $manifest
    [Array]::Sort($paths, [StringComparer]::Ordinal)

    $zipName = "$payloadName.zip"
    $zipPath = Join-Path $stage $zipName
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in $paths) {
            $entry = $archive.CreateEntry("$payloadName/$path", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputFile = [IO.File]::OpenRead((Join-Path $payload $path))
            try {
                $outputEntry = $entry.Open()
                try { $inputFile.CopyTo($outputEntry) } finally { $outputEntry.Dispose() }
            } finally {
                $inputFile.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
    [IO.File]::WriteAllText((Join-Path $stage "$zipName.sha256"), "$(Get-Hash $zipPath)  $zipName`n", $utf8)
}

function Publish-Package($destination, $source) {
    $stage = Join-Path ([IO.Path]::GetDirectoryName($destination)) ".afterbreak-$([Guid]::NewGuid().ToString('N'))"
    [void][IO.Directory]::CreateDirectory($stage)
    $payload = Join-Path $stage $payloadName
    try {
        Invoke-Checked $sdk @('publish', $project, '-c', $Configuration, '--no-restore', '-r', $rid,
            '--self-contained', 'true', '-o', $payload)
        foreach ($name in @('Afterbreak.exe', 'Afterbreak.pri', 'App.xbf', 'coreclr.dll', 'hostpolicy.dll', 'Microsoft.UI.Xaml.dll', 'e_sqlite3.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
                throw "Self-contained publish is missing $name."
            }
        }
        $runtime = Get-Content (Join-Path $payload 'Afterbreak.runtimeconfig.json') -Raw | ConvertFrom-Json
        $framework = $runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }
        $dependencies = Get-Content (Join-Path $payload 'Afterbreak.deps.json') -Raw | ConvertFrom-Json
        if (-not $framework -or $framework.version -ne $runtimeVersion -or $dependencies.runtimeTarget.name -notlike "*/$rid") {
            throw "Expected a self-contained .NET $runtimeVersion $rid publish."
        }
        Copy-DependencyNotices $payload
        Write-Artifact $stage $payload $source
        if ((Get-SourceState | ConvertTo-Json -Compress) -cne ($source | ConvertTo-Json -Compress)) {
            throw 'Checkout changed while packaging. Retry from a stable checkout.'
        }
        [IO.Directory]::Move($stage, $destination)
        Write-Host "Package: $destination"
    } catch {
        throw "Package failed. Staging files are at $stage. $($_.Exception.Message)"
    }
}

# Resolve a caller-supplied path before switching the working directory for global.json.
if (-not $DotNetPath) { $DotNetPath = $env:AFTERBREAK_DOTNET }
if ($DotNetPath) { $DotNetPath = (Get-Item -LiteralPath $DotNetPath).FullName }
if ($Action -eq 'Package') {
    if ($Configuration -ne 'Release') { throw 'Package requires Release configuration.' }
    $destination = Get-PackageDestination
}
$previousCertSetting = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$previousTelemetrySetting = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousDotNetRoot = $env:DOTNET_ROOT_X64
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location -LiteralPath $repository
try {
    if ($Action -eq 'Package') { $source = Get-SourceState }
    if (-not $DotNetPath) {
        $localSdk = Join-Path (Split-Path -Parent $repository) ".tools\dotnet-$sdkVersion\dotnet.exe"
        $DotNetPath = if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $localSdk } else {
            (Get-Command dotnet.exe -CommandType Application).Source
        }
    }
    $sdk = $DotNetPath
    $env:DOTNET_ROOT_X64 = Split-Path -Parent $sdk
    if ((Invoke-Checked $sdk @('--version')) -ne $sdkVersion) {
        throw "Use .NET SDK $sdkVersion via -DotNetPath or AFTERBREAK_DOTNET."
    }
    if ($Action -eq 'Test') {
        foreach ($check in @('Placement', 'Reminder', 'Session', 'Storage')) {
            $name = "Afterbreak.${check}Checks"
            $suite = Join-Path $repository "tests\$name\$name.csproj"
            Invoke-Checked $sdk @('restore', $suite, '--locked-mode', '--configfile', $config)
            Invoke-Checked $sdk @('run', '--project', $suite, '-c', $Configuration, '--no-restore')
        }
        & (Join-Path $repository 'tests\Afterbreak.InstallerChecks\Test-InstallerPackage.ps1')
    } else {
        Invoke-Checked $sdk @('restore', $project, '--locked-mode', '--configfile', $config)
        if ($Action -eq 'Package') { Publish-Package $destination $source } else {
            Invoke-Checked $sdk @('build', $project, '-c', $Configuration, '--no-restore')
            if ($Action -eq 'Launch') {
                $assembly = Invoke-Checked $sdk @('msbuild', $project, "-p:Configuration=$Configuration", '-getProperty:TargetPath')
                $application = [IO.Path]::ChangeExtension($assembly, '.exe')
                $launch = @{ FilePath = $application; WorkingDirectory = (Split-Path -Parent $application) }
                if ($CaptureMotionStudy) { $launch.ArgumentList = '--capture-motion-study' }
                Start-Process @launch
            }
        }
    }
} finally {
    Pop-Location
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertSetting
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetrySetting
    $env:DOTNET_ROOT_X64 = $previousDotNetRoot
}
