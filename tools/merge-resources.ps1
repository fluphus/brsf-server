# Requires Windows PowerShell 5.1 or PowerShell 7+.
[CmdletBinding()]
param([string]$Directory = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SafeName($Value) {
    if ($Value -isnot [string] -or $Value -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $Value.EndsWith('.')) {
        throw 'Manifest filenames must be simple local filenames'
    }
    return $Value
}

function Get-PositiveSize($Value) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -le 0) {
        throw 'Manifest sizes must be positive integers'
    }
    return [long]$Value
}

$temporary = $null
try {
    if ([string]::IsNullOrEmpty($Directory)) { $Directory = $PSScriptRoot }
    $folder = (Resolve-Path -LiteralPath $Directory).ProviderPath
    $manifest = Get-Content -LiteralPath (Join-Path $folder 'resources-parts.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($manifest.formatVersion -isnot [int] -and $manifest.formatVersion -isnot [long]) -or $manifest.formatVersion -ne 1) {
        throw 'Unsupported manifest version'
    }
    $outputName = Get-SafeName $manifest.output
    $output = Join-Path $folder $outputName
    $total = Get-PositiveSize $manifest.size
    if ($manifest.parts -isnot [array] -or $manifest.parts.Count -eq 0) { throw 'Manifest has no parts' }
    $names = @{}
    $names[$outputName] = $true
    [decimal]$sum = 0
    foreach ($part in $manifest.parts) {
        $name = Get-SafeName $part.name
        if ($names.ContainsKey($name)) { throw "Duplicate or conflicting manifest filename: $name" }
        $names[$name] = $true
        $sum += Get-PositiveSize $part.size
        if ($part.sha256 -isnot [string] -or $part.sha256 -cnotmatch '^[0-9a-fA-F]{64}$') { throw "Invalid SHA-256 for $name" }
    }
    if ($sum -ne $total) { throw 'Manifest part sizes do not match total size' }
    if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
    $candidate = Join-Path $folder ('.brs-merge-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $target = [IO.File]::Open($candidate, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $temporary = $candidate
    try {
        $buffer = New-Object byte[] (1024 * 1024)
        foreach ($part in $manifest.parts) {
            Write-Host ('Verifying ' + $part.name)
            $source = [IO.File]::Open((Join-Path $folder $part.name), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($source.Length -ne $part.size) { throw ('Part size mismatch: ' + $part.name) }
                $hash = [Security.Cryptography.SHA256]::Create()
                try {
                    [long]$count = 0
                    while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $count += $read
                        if ($count -gt $part.size) { throw ('Part size mismatch: ' + $part.name) }
                        [void]$hash.TransformBlock($buffer, 0, $read, $buffer, 0)
                        $target.Write($buffer, 0, $read)
                    }
                    [void]$hash.TransformFinalBlock($buffer, 0, 0)
                    $actual = [BitConverter]::ToString($hash.Hash).Replace('-', '').ToLowerInvariant()
                    if ($count -ne $part.size -or $actual -ne $part.sha256.ToLowerInvariant()) { throw ('Part size or SHA-256 mismatch: ' + $part.name) }
                } finally { $hash.Dispose() }
            } finally { $source.Dispose() }
        }
        $target.Flush($true)
    } finally { $target.Dispose() }
    # Same-directory move is atomic and refuses to replace an existing output.
    [IO.File]::Move($temporary, $output)
    $temporary = $null
    Write-Host ('Resource package ready: ' + $output)
} catch {
    [Console]::Error.WriteLine('Merge failed: ' + $_.Exception.Message)
    exit 1
} finally {
    if ($null -ne $temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
