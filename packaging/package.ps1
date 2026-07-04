# packaging/package.ps1 -- assemble the release zip locally.
#
# Mirrors the packaging job in .github/workflows/release.yml: the zip layout
# and the only-one-DLL audit must stay identical in both places. Invoked by
# `just package` (which builds the plugin DLL and the UF2 first).
#
# Zip layout (exactly these four top-level entries):
#   UniflagPlugin.dll
#   uniflag.uf2
#   Uniflag Overlay.simhubdash   (the DashStudio overlay, packed by
#                                 make-simhubdash.ps1 -- double-click to import)
#   INSTALL.md

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot   # this script lives in packaging/
Set-Location $root

# Version: `git describe` when the working tree is clean, else "dev".
$dirty = git status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status failed -- not a git checkout?' }
if ($dirty) {
    $version = 'dev'
} else {
    $version = (git describe --tags --always | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $version) { $version = 'dev' }
}

$dll  = Join-Path $root 'plugin\src\bin\Release\net48\UniflagPlugin.dll'
$uf2  = Join-Path $root 'target\uniflag.uf2'
$dash = Join-Path $root 'overlay\dash\Uniflag Overlay'
$md   = Join-Path $root 'packaging\INSTALL.md'
foreach ($f in $dll, $uf2, $dash, $md) {
    if (-not (Test-Path $f)) { throw "missing input: $f (run 'just package', which builds first)" }
}

# Stage
$stage = Join-Path $root 'target\package'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage
Copy-Item $uf2 $stage
Copy-Item $md $stage
# Pack the overlay dash into a single double-click-to-import .simhubdash
# (shared with the CI release job -- keep the generator single-sourced).
& (Join-Path $PSScriptRoot 'make-simhubdash.ps1') `
    -DashFolder $dash -OutFile (Join-Path $stage 'Uniflag Overlay.simhubdash')

# Audit 1: exactly the four expected top-level entries.
$expected = @('INSTALL.md', 'Uniflag Overlay.simhubdash', 'UniflagPlugin.dll', 'uniflag.uf2')
$top = @(Get-ChildItem $stage | Sort-Object Name | ForEach-Object Name)
if (Compare-Object $expected $top) {
    Write-Host "staged: $($top -join ', ')"
    throw "staged layout mismatch -- expected exactly: $($expected -join ', ')"
}

# Audit 2: never ship a DLL other than our own (SimHub's reference assemblies
# are proprietary and must not leak into the zip).
$badDlls = @(Get-ChildItem $stage -Recurse -File -Filter '*.dll' |
    Where-Object Name -ne 'UniflagPlugin.dll')
if ($badDlls) {
    $badDlls | ForEach-Object { Write-Host $_.FullName }
    throw 'unexpected DLLs staged -- refusing to package'
}

# Zip + listing
$zip = Join-Path $root "target\uniflag-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Write-Host "packaged: $zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $archive.Entries | Sort-Object FullName | ForEach-Object {
        '{0,10}  {1}' -f $_.Length, $_.FullName
    }
} finally {
    $archive.Dispose()
}
