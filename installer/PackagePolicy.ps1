function Get-Sha256($path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-InstallerDestination($repository, $requestedPath) {
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($requestedPath)
    if ($path -notmatch '^[A-Za-z]:\\[^:]*$') { throw 'Output requires an ordinary local drive path.' }
    $destination = [IO.Path]::GetFullPath($path)
    if ($destination -match '[. ](?:\\|$)') { throw 'Output path components may not end in dots or spaces.' }
    $parent = [IO.Directory]::GetParent($destination)
    if (-not $parent -or -not $parent.Exists) { throw 'The output parent directory must exist.' }
    for ($ancestor = $parent; $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Output may not pass through a reparse point: $($ancestor.FullName)"
        }
    }
    $canonicalParent = (Get-Item -LiteralPath $parent.FullName).FullName
    $destination = Join-Path $canonicalParent ([IO.Path]::GetFileName($destination))
    if ($destination.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith("$repository\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output must be outside the checkout.'
    }
    if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
    return $destination
}

function Test-PayloadPath([string] $path) {
    if ($path -notmatch '^[A-Za-z0-9_. /-]+$') { return $false }
    foreach ($component in $path.Split('/')) {
        if ($component -eq '' -or $component -match '[. ]$' -or
            $component -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { return $false }
    }
    return $true
}

function Read-PackageText($entry) {
    if ($entry.Length -gt 5MB) { throw "Package metadata is too large: $($entry.FullName)" }
    $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false, $true))
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Expand-VerifiedPackage($zipPath, $expectedSha, $expectedRevision, $destination) {
    Add-Type -AssemblyName System.IO.Compression
    $root = 'OpenBreak-win-x64/'
    $stream = [IO.File]::OpenRead($zipPath)
    try {
        if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $expectedSha) {
            throw 'Package ZIP SHA256 does not match the expected artifact.'
        }
        $stream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                if (-not $entry.FullName.StartsWith($root, [StringComparison]::Ordinal)) {
                    throw "Unexpected package entry: $($entry.FullName)"
                }
                $relative = $entry.FullName.Substring($root.Length)
                if (-not (Test-PayloadPath $relative) -or ($entry.ExternalAttributes -band 0x400) -or
                    (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                    throw "Unsafe package entry: $relative"
                }
                if ($files.ContainsKey($relative)) { throw "Duplicate package entry: $relative" }
                $files.Add($relative, $entry)
            }

            foreach ($required in @('files.sha256', 'source-checksums.sha256', 'dependencies.lock.json', 'build-info.json',
                'OpenBreak.exe', 'OpenBreak.pri', 'App.xbf', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
                'e_sqlite3.dll', 'OpenBreak.deps.json', 'OpenBreak.runtimeconfig.json')) {
                if (-not $files.ContainsKey($required)) { throw "Package is missing $required." }
            }
            $metadata = Read-PackageText $files['build-info.json'] | ConvertFrom-Json
            if ($metadata.format -ne 1 -or $metadata.dirty -isnot [bool] -or $metadata.dirty -or
                $metadata.revision -ne $expectedRevision -or $metadata.configuration -ne 'Release' -or
                $metadata.runtimeIdentifier -ne 'win-x64') {
                throw 'Package must be a clean Release win-x64 artifact for the expected revision.'
            }
            $checksums = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
            $manifest = Read-PackageText $files['files.sha256']
            foreach ($line in ($manifest.TrimEnd("`r", "`n") -split "`r?`n")) {
                if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') { throw 'Malformed package file manifest.' }
                $hash = $Matches[1]
                $name = $Matches[2]
                if ($name -ieq 'files.sha256' -or -not $files.ContainsKey($name) -or $checksums.ContainsKey($name)) {
                    throw "Unexpected or duplicate manifest entry: $name"
                }
                $checksums.Add($name, $hash)
            }
            if ($checksums.Count -ne $files.Count - 1) { throw 'File manifest does not cover the complete package.' }
            if ($checksums['source-checksums.sha256'] -ne $metadata.sourceChecksumsSha256 -or
                $checksums['dependencies.lock.json'] -ne $metadata.dependenciesLockSha256) {
                throw 'Build provenance checksums do not match the file manifest.'
            }

            if (Test-Path -LiteralPath $destination) { throw 'Package extraction requires an unused directory.' }
            [void][IO.Directory]::CreateDirectory($destination)
            foreach ($name in $files.Keys) {
                $target = Join-Path $destination $name
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                $inputFile = $files[$name].Open()
                try {
                    $outputFile = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
                    try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() }
                } finally { $inputFile.Dispose() }
                if ($name -ine 'files.sha256' -and (Get-Sha256 $target) -ne $checksums[$name]) {
                    throw "Payload checksum mismatch: $name"
                }
            }
            return @{
                revision = $metadata.revision
                manifestSha256 = Get-Sha256 (Join-Path $destination 'files.sha256')
                fileCount = $files.Count
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
