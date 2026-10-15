param([string]$CoreLibrary = '')
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
if ($CoreLibrary) {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $CoreLibrary).Path -Destination (Join-Path $runtime 'dosbox_pure_libretro.dll') -Force
} else {
    $lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime.lock.json') -Raw | ConvertFrom-Json
    $archive = Join-Path $runtime 'core-download.zip'
    Invoke-WebRequest -Uri $lock.core.url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.core.archiveSha256) { throw 'DOS core download differs from the reviewed runtime. No unverified core has been installed.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
    Remove-Item -LiteralPath $archive
    $core = Join-Path $runtime 'dosbox_pure_libretro.dll'
    if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.core.sha256) { throw 'DOS core DLL hash mismatch.' }
}
Write-Host 'DOSBox Pure is ready.'
