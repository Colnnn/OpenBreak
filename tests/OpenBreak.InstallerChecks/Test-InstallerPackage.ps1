Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'installer\PackagePolicy.ps1')
Add-Type -AssemblyName System.IO.Compression
$revision = 'a' * 40
$utf8 = [Text.UTF8Encoding]::new($false)
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = [IO.Directory]::CreateDirectory((Join-Path $temp "OpenBreakInstallerChecks-$([Guid]::NewGuid().ToString('N'))")).FullName

function Assert-Rejected($name, $reason, [scriptblock] $operation) {
    try { $null = & $operation } catch {
        if ($_.Exception.Message -notlike "*$reason*") { throw }
        Write-Host "PASS $name"
        return
    }
    throw "Expected rejection: $name"
}

function Write-ZipEntry($archive, $root, $name, $value) {
    $entry = $archive.CreateEntry("$root/$name")
    $writer = [IO.StreamWriter]::new($entry.Open(), $utf8)
    try { $writer.Write($value) } finally { $writer.Dispose() }
}

function Get-TextHash($value) {
    $content = [IO.MemoryStream]::new($utf8.GetBytes($value))
    try { return (Get-FileHash -InputStream $content).Hash.ToLowerInvariant() } finally { $content.Dispose() }
}

function New-Fixture($name, [switch] $Dirty, [switch] $ChangeFile, [switch] $OmitHash,
    [switch] $DuplicateHash, [switch] $BadProvenance, [string] $ExtraEntry, [string] $Root = 'OpenBreak-win-x64') {
    $files = [ordered]@{
        'OpenBreak.exe' = 'synthetic app'
        'OpenBreak.pri' = 'synthetic resources'
        'App.xbf' = 'synthetic markup'
        'coreclr.dll' = 'synthetic runtime'
        'Microsoft.UI.Xaml.dll' = 'synthetic WinUI runtime'
        'e_sqlite3.dll' = 'synthetic database engine'
        'OpenBreak.deps.json' = '{}'
        'OpenBreak.runtimeconfig.json' = '{}'
        'dependencies.lock.json' = '{}'
        'source-checksums.sha256' = 'synthetic source digest'
        'ThirdParty/Runtime/NOTICE.txt' = 'synthetic dependency notice'
    }
    $metadata = [ordered]@{
        format = 1
        dirty = [bool]$Dirty
        revision = $revision
        configuration = 'Release'
        runtimeIdentifier = 'win-x64'
        sourceChecksumsSha256 = Get-TextHash $files['source-checksums.sha256']
        dependenciesLockSha256 = Get-TextHash $files['dependencies.lock.json']
    }
    if ($BadProvenance) { $metadata.dependenciesLockSha256 = '0' * 64 }
    $files['build-info.json'] = $metadata | ConvertTo-Json
    $manifest = foreach ($file in $files.GetEnumerator()) {
        if (-not $OmitHash -or $file.Key -ne 'OpenBreak.exe') {
            "$(Get-TextHash $file.Value)  $($file.Key)"
        }
    }
    if ($DuplicateHash) { $manifest += $manifest[0] }
    if ($ChangeFile) { $files['OpenBreak.exe'] = 'changed after hashing' }

    $path = Join-Path $scratch "$name.zip"
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in $files.GetEnumerator()) { Write-ZipEntry $archive $Root $file.Key $file.Value }
        if ($ExtraEntry) { Write-ZipEntry $archive $Root $ExtraEntry 'extra file' }
        Write-ZipEntry $archive $Root 'files.sha256' (($manifest -join "`n") + "`n")
    } finally { $archive.Dispose() }
    return $path
}

function Expand-Fixture($path) {
    Expand-VerifiedPackage $path (Get-Sha256 $path) $revision (Join-Path $scratch ([IO.Path]::GetFileNameWithoutExtension($path)))
}

try {
    $launchSource = Get-Content (Join-Path $repo 'LaunchPipe.cs') -Raw
    $startupPolicy = Get-Content (Join-Path $repo 'installer\InstallPolicy.iss') -Raw
    $setupSource = Get-Content (Join-Path $repo 'installer\OpenBreak.iss') -Raw
    $version = ([xml](Get-Content (Join-Path $repo 'OpenBreak.csproj') -Raw)).Project.PropertyGroup.Version
    $flag = [regex]::Match($launchSource, 'StartupFlag = "([^"]+)";').Groups[1].Value
    $suffix = [regex]::Match($startupPolicy, "QuietStartupSuffix = '([^']+)';").Groups[1].Value
    $startLink = [regex]::Match($setupSource, '(?ms)^\[Icons\]\r?\n(.*?)^\[').Groups[1].Value
    if (!$flag -or $suffix -cne " $flag" -or !$startLink -or $startLink -match '\bParameters\s*:' -or
        $setupSource -notmatch 'RegWriteStringValue\(HKCU, RunKey, RunName, QuietStartupCommand\(') {
        throw 'The installer must pass the quiet launch flag only for the startup registration.'
    }
    Write-Host 'PASS installer source keeps the Start Menu launch normal and the Run launch quiet'

    $mutex = [regex]::Match($launchSource, 'InstanceMutexName = @"([^"]+)"').Groups[1].Value
    $setupMutex = [regex]::Match($setupSource, '#define ProductMutex "([^"]+)"').Groups[1].Value
    if (!$mutex -or $setupMutex -cne $mutex -or
        !$setupSource.Contains('#define ProductId "Afterbreak.5572F77B-412F-48B5-AD89-247ECE6B356E"') -or
        !$setupSource.Contains('#define InstallPath "{localappdata}\Programs\Afterbreak"') -or
        !$setupSource.Contains("RunName = '{#ProductId}';")) {
        throw 'Setup must share the instance guard and recognize the existing uninstall and startup registration.'
    }
    Write-Host 'PASS existing install, startup and single-instance identities remain compatible'

    $versionCheck = [regex]::Match($setupSource, '(?ms)^procedure CheckInstallVersion\b.*?^end;').Value
    if ([version]$version -le [version]'0.1.0' -or $versionCheck -notmatch 'CompareInstallVersions\(Current, Candidate\) > 0') {
        throw 'The OpenBreak upgrade must remain newer than the previous branded installer and reject downgrades.'
    }
    Write-Host 'PASS the OpenBreak installer version prevents reinstallation by the previous installer'

    $startupCommand = [regex]::Match($startupPolicy, '(?ms)^function IsOwnedStartupCommand\b.*?^end;').Value
    if (!$setupSource.Contains('#define AppExecutable "OpenBreak.exe"') -or
        !$startupPolicy.Contains("LegacyAppExecutable = 'Afterbreak.exe';") -or
        !$startupCommand.Contains("IsOwnedVersionedExecutable(InstallRoot, Command, '{#AppExecutable}')") -or
        !$startupCommand.Contains('IsOwnedVersionedExecutable(InstallRoot, Command, LegacyAppExecutable)')) {
        throw 'The Run entry policy must recognize both executable names under the existing version-slot checks.'
    }
    Write-Host 'PASS startup ownership policy recognizes current and legacy executable names'

    $legacyCleanup = [regex]::Match($setupSource, '(?ms)^procedure RetireLegacyShortcut\b.*?^end;').Value
    $legacyUninstall = [regex]::Match($setupSource, '(?ms)^procedure CurUninstallStepChanged\b.*?^end;').Value
    $shortcutReader = [regex]::Match($setupSource, '(?ms)^function ReadNormalShortcutTarget\b.*?^end;').Value
    $beforeInstall = [regex]::Match($setupSource, '(?ms)^procedure InitializeWizard\b.*?^end;').Value
    $postInstall = [regex]::Match($setupSource, '(?ms)^procedure CurStepChanged\b.*?^end;').Value
    if ($postInstall -notmatch '(?s)Step <> ssPostInstall then\s+Exit;\s+Committed := True;.*RetireLegacyShortcut;' -or
        $beforeInstall -notmatch '(?s)OpenBreakOnlyLog := not HadInstall or\s+\(HasOpenBreakOnlyLog and.*CompareText\(DisplayName, ''OpenBreak''\)' -or
        $postInstall -notmatch 'if OpenBreakOnlyLog then\s+Marker := 1;' -or
        $postInstall -notmatch 'RegWriteDWordValue\(HKCU, UninstallKey, ''\{#FreshLogValue\}'', Marker\)' -or
        $legacyCleanup -notmatch 'if OpenBreakOnlyLog then\s+Exit;' -or
        $legacyCleanup -notmatch '(?s)if IsOwnedLegacyShortcut\(Link\) then\s+LegacyShortcutCleanupFailed := not DeleteFile\(Link\)' -or
        $legacyUninstall -notmatch 'not HasOpenBreakOnlyLog and PathExistsChecked\(LegacyLink\) and not IsOwnedLegacyShortcut\(LegacyLink\)') {
        throw 'Shortcut activation must precede legacy cleanup, and an inherited uninstall log must protect unrelated legacy links.'
    }
    Write-Host 'PASS legacy shortcut cleanup occurs after activation and inherited uninstall guards unrelated links'
    if ($shortcutReader -notmatch '(?s)Arguments := Shortcut\.Arguments;.*if Arguments = '''' then\s+Result := Shortcut\.TargetPath;') {
        throw 'Shortcut ownership requires an argument-free application launch.'
    }
    Write-Host 'PASS custom Start Menu shortcut arguments disqualify shortcut ownership'

    $good = New-Fixture 'good'
    $verified = Expand-Fixture $good
    if ($verified.revision -ne $revision -or $verified.fileCount -ne 13 -or
        (Get-Content (Join-Path $scratch 'good\ThirdParty\Runtime\NOTICE.txt') -Raw) -cne 'synthetic dependency notice') {
        throw 'Clean package did not preserve the exact payload.'
    }
    Write-Host 'PASS clean package preserves the verified payload and notices'
    Assert-Rejected 'previous package root' 'Unexpected package entry' {
        Expand-Fixture (New-Fixture 'old-brand' -Root 'Afterbreak-win-x64')
    }
    Assert-Rejected 'wrong expected ZIP digest' 'ZIP SHA256' {
        Expand-VerifiedPackage $good ('0' * 64) $revision (Join-Path $scratch 'wrong-digest')
    }
    Assert-Rejected 'wrong package revision' 'expected revision' {
        Expand-VerifiedPackage $good (Get-Sha256 $good) ('b' * 40) (Join-Path $scratch 'wrong-revision')
    }
    Assert-Rejected 'dirty package' 'clean Release' { Expand-Fixture (New-Fixture 'dirty' -Dirty) }
    Assert-Rejected 'tampered payload' 'checksum mismatch' { Expand-Fixture (New-Fixture 'tampered' -ChangeFile) }
    Assert-Rejected 'incomplete manifest' 'complete package' { Expand-Fixture (New-Fixture 'omitted' -OmitHash) }
    Assert-Rejected 'duplicate manifest path' 'duplicate manifest' { Expand-Fixture (New-Fixture 'duplicate-hash' -DuplicateHash) }
    Assert-Rejected 'bad source provenance' 'provenance checksums' { Expand-Fixture (New-Fixture 'provenance' -BadProvenance) }
    Assert-Rejected 'case-alias ZIP entry' 'Duplicate package entry' { Expand-Fixture (New-Fixture 'duplicate-file' -ExtraEntry 'openbreak.EXE') }
    foreach ($path in @('../outside', 'name:stream', 'NUL.dll', 'file. /child')) {
        Assert-Rejected "unsafe ZIP path $path" 'Unsafe package entry' {
            Expand-Fixture (New-Fixture 'unsafe' -ExtraEntry $path)
        }
        Move-Item -LiteralPath (Join-Path $scratch 'unsafe.zip') -Destination (Join-Path $scratch "$([Guid]::NewGuid()).zip")
    }
    Assert-Rejected 'immutable existing extraction' 'unused directory' { Expand-Fixture $good }
    Assert-Rejected 'existing output rejected' 'already exists' { Get-InstallerDestination $repo $scratch }
    Assert-Rejected 'output in checkout rejected' 'outside the checkout' { Get-InstallerDestination $repo (Join-Path $repo 'installer-guard-probe') }
    Assert-Rejected 'extended output alias rejected' 'ordinary local drive' { Get-InstallerDestination $repo "\\?\$repo\installer-guard-probe" }
    Assert-Rejected 'ambiguous output alias rejected' 'dots or spaces' { Get-InstallerDestination $repo "$repo \installer-guard-probe" }
} finally {
    if ([IO.Path]::GetDirectoryName($scratch) -eq $temp.TrimEnd('\') -and
        [IO.Path]::GetFileName($scratch) -like 'OpenBreakInstallerChecks-*') {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
