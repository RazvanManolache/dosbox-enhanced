param([string]$Model = '', [string]$NvidiaSdkArchive = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'runtime/phoenix_native.dll')) -or -not (Test-Path -LiteralPath (Join-Path $root 'runtime/dosbox_nvngx.dll'))) {
    throw 'The native graphics libraries are missing. Use the portable release, or build with -Graphics first.'
}
if (-not $Model) {
    Add-Type -AssemblyName PresentationFramework
    $picker = New-Object Microsoft.Win32.OpenFileDialog
    $picker.Title = 'Choose your compatible NVIDIA neural-rendering model DLL'
    $picker.Filter = 'NVIDIA neural rendering (nvngx_dlssnr.dll)|nvngx_dlssnr.dll|DLL files (*.dll)|*.dll'
    if (-not $picker.ShowDialog()) { return }
    $Model = $picker.FileName
}
$Model = (Resolve-Path -LiteralPath $Model).Path
if ((Get-AuthenticodeSignature -LiteralPath $Model).Status -ne 'Valid') { throw 'The selected model DLL does not have a valid signature.' }
$lock = (Get-Content -LiteralPath (Join-Path $root 'runtime.lock.json') -Raw | ConvertFrom-Json).nvidia
if (-not $NvidiaSdkArchive) {
    $downloads = Join-Path $root 'data/downloads'; New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $NvidiaSdkArchive = Join-Path $downloads ('streamline-sdk-' + $lock.version + '.zip')
    if (-not (Test-Path -LiteralPath $NvidiaSdkArchive)) { Write-Host 'Downloading the pinned NVIDIA Streamline SDK from NVIDIA GitHub...'; Invoke-WebRequest -Uri $lock.url -OutFile $NvidiaSdkArchive }
}
if ((Get-FileHash -LiteralPath $NvidiaSdkArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.archiveSha256) { throw 'NVIDIA SDK archive differs from runtime.lock.json.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$vendor = Join-Path $root 'runtime/nvidia'; New-Item -ItemType Directory -Path $vendor -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $NvidiaSdkArchive).Path)
try {
    foreach ($file in $lock.files) {
        $target = Join-Path $vendor $file.name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('bin/x64/' + $file.name), $target, $true)
        if ((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $file.sha256 -or (Get-AuthenticodeSignature -LiteralPath $target).Status -ne 'Valid') { throw ('Runtime verification failed: ' + $file.name) }
    }
    foreach ($license in @('license.txt','3rd-party-licenses.md','bin/x64/nvngx_dlss.license.txt','bin/x64/reflex.license.txt')) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($license),(Join-Path $vendor ([IO.Path]::GetFileName($license))),$true)
    }
} finally { $zip.Dispose() }
$config = [ordered]@{nr_dll=$Model;sr_dll='runtime/nvidia/nvngx_dlss.dll';streamline='runtime/nvidia/sl.interposer.dll';native_bundle='runtime/native';weight_source=@{kind='nvidia_dll';path=$Model;sha256=(Get-FileHash -LiteralPath $Model).Hash.ToLowerInvariant()};cache='data/graphics-cache'}
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'graphics.json') -Encoding utf8
Write-Host 'Graphics configured. Start or restart DOSBox Enhanced. Keep your selected model DLL in place.'
Write-Host 'NVIDIA runtime terms are retained in runtime/nvidia. No model weights have been copied into this app.'
