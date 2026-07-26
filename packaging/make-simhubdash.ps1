# packaging/make-simhubdash.ps1 -- pack a DashStudio dash folder into a single
# .simhubdash file (the format SimHub's Dash Studio "Export Dashboard" produces,
# and which users import by double-clicking with SimHub running).
#
# A .simhubdash is a plain ZIP whose entries are prefixed with the dash folder
# name and use BACKSLASH separators, e.g. `Uniflag Overlay\Uniflag Overlay.djson`
# -- verified against a real SimHub-exported sample and the sidecar-name patterns
# SimHub.Plugins.dll keys off during import. We drop SimHub's local edit-history
# (`_Backups`) so it never ships.
#
# Runs under both Windows PowerShell 5.1 (local `just package`) and PowerShell
# Core (the CI release job on ubuntu). It is the single source of truth for the
# packaging both paths perform -- do not inline the zip logic in either caller.

param(
    [Parameter(Mandatory = $true)] [string] $DashFolder,
    [Parameter(Mandatory = $true)] [string] $OutFile
)

$ErrorActionPreference = 'Stop'

# ZipArchive/ZipFile need explicit Add-Type on .NET Framework; on .NET Core the
# types load on demand and the Add-Type is a harmless no-op (swallow the error).
try { Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop } catch {}
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop } catch {}

if (-not (Test-Path -LiteralPath $DashFolder -PathType Container)) {
    throw "dash folder not found: $DashFolder"
}

$dashName = Split-Path -Path $DashFolder -Leaf
$baseFull = (Resolve-Path -LiteralPath $DashFolder).Path.TrimEnd('\', '/')

# Every file under the dash folder except SimHub's edit-history backups. Sorted
# for a stable entry order; directories contribute nothing (ZIP holds files).
$files = Get-ChildItem -LiteralPath $DashFolder -Recurse -File |
    Where-Object { $_.FullName.Substring($baseFull.Length) -notmatch '[\\/]_Backups[\\/]' } |
    Sort-Object FullName
if (-not $files) { throw "no files to pack under: $DashFolder" }

$outDir = Split-Path -Path $OutFile -Parent
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
if (Test-Path -LiteralPath $OutFile) { Remove-Item -LiteralPath $OutFile -Force }

$fs  = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        # Path relative to the dash folder, folder-name-prefixed, backslashes.
        $rel = $f.FullName.Substring($baseFull.Length).TrimStart('\', '/').Replace('/', '\')
        $entryName = "$dashName\$rel"
        $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $es = $entry.Open()
        try {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            $es.Write($bytes, 0, $bytes.Length)
        } finally { $es.Dispose() }
    }
} finally {
    $zip.Dispose(); $fs.Dispose()
}

# Guard: reopen and assert the entries came out folder-prefixed with backslashes
# (catches any platform that silently normalises separators before we ship it),
# and that the dashboard payload itself is present.
$check = [System.IO.Compression.ZipFile]::OpenRead($OutFile)
try {
    $names = $check.Entries | ForEach-Object { $_.FullName }
    $prefix = "$dashName\"
    $bad = $names | Where-Object { -not $_.StartsWith($prefix) -or $_.Contains('/') }
    if ($bad) { throw "unexpected entry names (want '$prefix' prefix, backslashes): $($bad -join ', ')" }
    if (-not ($names | Where-Object { $_ -match '\.djson$' })) {
        throw "no .djson entry in the package -- '$DashFolder' is not a dash folder?"
    }
} finally {
    $check.Dispose()
}

Write-Host "wrote $OutFile ($((Get-Item -LiteralPath $OutFile).Length) bytes, $($files.Count) entries)"
