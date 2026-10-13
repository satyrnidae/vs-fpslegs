# Downloads build time mod dependencies listed in vendor_deps.json from the Vintage Story mod db.
# Zips land in libs/mods, top-level DLLs from each zip are extracted to libs.
# "game", "survival", "creative" and "essentials" are skipped, those ship with the install.
#
# Usage: ./tools/get-deps.ps1 [-DepsFile <path>] [-OutDir <path>] [-NoExtract]
param(
    [string]$DepsFile = "$PSScriptRoot/../vendor_deps.json",
    [string]$OutDir = "$PSScriptRoot/../libs",
    [switch]$NoExtract
)

$ErrorActionPreference = 'Stop'
$apiBase = 'https://mods.vintagestory.at/api/mod'
$builtIn = @('game', 'survival', 'creative', 'essentials')

$deps = Get-Content $DepsFile -Raw | ConvertFrom-Json
if (-not $deps) { Write-Host 'No dependencies in vendor_deps.json'; return }

$modsDir = Join-Path $OutDir 'mods'
New-Item -ItemType Directory -Force $modsDir | Out-Null

foreach ($dep in $deps.PSObject.Properties) {
    $modid = $dep.Name
    $wanted = $dep.Value
    if ($builtIn -contains $modid) { continue }

    Write-Host "[$modid] wanted $wanted"
    try {
        $resp = Invoke-RestMethod "$apiBase/$modid"
    } catch {
        Write-Warning "[$modid] mod db lookup failed: $($_.Exception.Message)"
        continue
    }

    # Releases come back newest first
    $release = $resp.mod.releases | Select-Object -First 1
    if (-not $release) { Write-Warning "[$modid] no releases found"; continue }

    if ($wanted -and $wanted -ne '*') {
        try {
            if ([version]$release.modversion -lt [version]$wanted) {
                Write-Warning "[$modid] latest $($release.modversion) is older than required $wanted"
            }
        } catch { } # non-numeric version strings, skip the check
    }

    $zip = Join-Path $modsDir "$modid-$($release.modversion).zip"
    if (Test-Path $zip) {
        Write-Host "[$modid] $($release.modversion) already downloaded"
    } else {
        Write-Host "[$modid] downloading $($release.modversion)"
        Invoke-WebRequest $release.mainfile -OutFile $zip
    }

    if ($NoExtract) { continue }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) "vsdep-$modid-$([guid]::NewGuid().ToString('N'))"
    try {
        Expand-Archive $zip -DestinationPath $tmp
        Get-ChildItem $tmp -Filter *.dll | ForEach-Object {
            Copy-Item $_.FullName $OutDir -Force
            Write-Host "[$modid] extracted $($_.Name) -> $OutDir"
        }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
