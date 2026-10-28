param([string]$OutputDirectory = '', [string]$Version = '0.6.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'release' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$folder = Join-Path $OutputDirectory ("DOSBox-Enhanced-$Version-win-x64")
if (Test-Path -LiteralPath $folder) { throw 'Package folder already exists. Clean the reviewed staging folder before packaging again.' }
New-Item -ItemType Directory -Path (Join-Path $folder 'app'),(Join-Path $folder 'runtime') -Force | Out-Null
$app = Join-Path $root 'app/DosboxEnhanced.exe'; if (-not (Test-Path -LiteralPath $app)) { throw 'Build the app first.' }
Copy-Item -LiteralPath $app -Destination (Join-Path $folder 'app')
foreach ($file in @('dosbox_pure_libretro.dll','dosbox_nvngx.dll','phoenix_native.dll')) {
    $inputFile = Join-Path $root ('runtime/' + $file)
    if (-not (Test-Path -LiteralPath $inputFile)) { throw ('Missing portable runtime: ' + $file) }
    Copy-Item -LiteralPath $inputFile -Destination (Join-Path $folder 'runtime')
}
$kernels = Join-Path $root 'runtime/native'
if (-not (Test-Path -LiteralPath (Join-Path $kernels 'native_manifest.json'))) { throw 'Build/stage the model-free Phoenix kernel bundle first.' }
Copy-Item -LiteralPath $kernels -Destination (Join-Path $folder 'runtime') -Recurse
foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md','DOSBox Enhanced.cmd','Setup graphics.cmd','setup-graphics.ps1','runtime.lock.json')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $folder }
New-Item -ItemType Directory -Path (Join-Path $folder 'docs') -Force | Out-Null
foreach ($name in @('USER_GUIDE.md','GRAPHICS.md','MCP.md','CAPTURES.md','CAPTURE_EVIDENCE.json','PIPELINE_EVIDENCE.json','GAME_GALLERY.md','GAME_PRESETS.json','GALLERY_EVIDENCE.json','VALIDATION.md','RELEASE_0.6_VALIDATION.json','RELEASE_0.6.1_VALIDATION.json','PHOENIX_0.17_UPDATE.md','DOSBOX_LICENSE')) { Copy-Item -LiteralPath (Join-Path $root ('docs/' + $name)) -Destination (Join-Path $folder 'docs') }
Copy-Item -LiteralPath (Join-Path $root 'docs/images') -Destination (Join-Path $folder 'docs') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $folder -Recurse
if (Test-Path -LiteralPath (Join-Path $folder 'data')) { throw 'A package must not contain user data.' }
if (Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object Name -Match '^nvngx_|\.pdb$|^graphics\.json$|^environments\.json$|^preferences\.json$') { throw 'Package contains forbidden local/model/debug data.' }
$manifest = @(Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object { [ordered]@{path=($_.FullName.Substring([IO.Path]::GetFullPath($folder).TrimEnd('\').Length + 1)).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'FILES.json') -Encoding utf8
$zip = $folder + '.zip'
Compress-Archive -LiteralPath $folder -DestinationPath $zip -CompressionLevel Optimal
Write-Host ('Portable archive: ' + $zip)
