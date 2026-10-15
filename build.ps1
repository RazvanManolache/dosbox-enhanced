param(
    [switch]$Graphics,
    [string]$PhoenixSdk = '',
    [switch]$BuildPhoenix,
    [string]$CudaCompiler = '',
    [string]$CoreLibrary = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'runtime/dosbox_pure_libretro.dll'))) {
    & (Join-Path $root 'setup-runtime.ps1') -CoreLibrary $CoreLibrary
}
if ($Graphics) {
    & (Join-Path $root 'tools/Initialize-Dependencies.ps1')
    if ($BuildPhoenix) {
        $PhoenixSdk = Join-Path $root 'out/phoenix-sdk'
        $phoenixSource = Join-Path $root 'out/phoenix-source'
        if (Test-Path -LiteralPath $phoenixSource) { throw 'out/phoenix-source already exists. Choose a fresh build folder or remove this app-owned generated folder before rebuilding Phoenix.' }
        New-Item -ItemType Directory -Path $phoenixSource -Force | Out-Null
        $archive = Join-Path $root 'out/phoenix-source.zip'
        git -C (Join-Path $root 'external/phoenix') archive --format=zip "--output=$archive" HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not export the pinned Phoenix source.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $phoenixSource
        git -C $phoenixSource init --quiet
        git -C $phoenixSource apply (Join-Path $root 'patches/phoenix-native-api.patch')
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix native API patch did not apply cleanly.' }
        cmake -S (Join-Path $phoenixSource 'native_runtime') -B (Join-Path $root 'out/phoenix-build') -G 'Visual Studio 17 2022' -A x64 -DPHOENIX_ENABLE_NATIVE_RTX=ON -DBUILD_TESTING=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded "-DCMAKE_SHARED_LINKER_FLAGS=/PDBALTPATH:phoenix_native.pdb" "-DPHOENIX_NGX_INCLUDE=$(Join-Path $root 'external/streamline/external/ngx-sdk/include')"
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix configuration failed.' }
        cmake --build (Join-Path $root 'out/phoenix-build') --config Release --target phoenix_native --parallel 4
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix build failed.' }
        New-Item -ItemType Directory -Path (Join-Path $PhoenixSdk 'bin'),(Join-Path $PhoenixSdk 'lib'),(Join-Path $PhoenixSdk 'include') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'out/phoenix-build/Release/phoenix_native.dll') -Destination (Join-Path $PhoenixSdk 'bin')
        Copy-Item -LiteralPath (Join-Path $root 'out/phoenix-build/Release/phoenix_native.lib') -Destination (Join-Path $PhoenixSdk 'lib')
        Copy-Item -LiteralPath (Join-Path $phoenixSource 'native_runtime/phoenix_native.h') -Destination (Join-Path $PhoenixSdk 'include')
        if ($CudaCompiler) {
            python (Join-Path $phoenixSource 'tools/production_build_native_kernels.py') (Join-Path $PhoenixSdk 'native') --compiler $CudaCompiler
            if ($LASTEXITCODE -ne 0) { throw 'Phoenix kernel compilation failed.' }
        }
    }
    if (-not $PhoenixSdk) { throw 'Graphics builds need -PhoenixSdk <SDK folder> or -BuildPhoenix. See BUILDING.md.' }
    $PhoenixSdk = (Resolve-Path -LiteralPath $PhoenixSdk).Path
    cmake -S (Join-Path $root 'native') -B (Join-Path $root 'out/native') -G 'Visual Studio 17 2022' -A x64 "-DNATIVE_SDK=$PhoenixSdk"
    if ($LASTEXITCODE -ne 0) { throw 'Graphics bridge configuration failed.' }
    cmake --build (Join-Path $root 'out/native') --config Release --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'Graphics bridge build failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'out/native/Release/dosbox_nvngx.dll'),(Join-Path $PhoenixSdk 'bin/phoenix_native.dll') -Destination (Join-Path $root 'runtime') -Force
    if (Test-Path -LiteralPath (Join-Path $PhoenixSdk 'native')) { Copy-Item -LiteralPath (Join-Path $PhoenixSdk 'native') -Destination (Join-Path $root 'runtime') -Recurse -Force }
}
dotnet publish (Join-Path $root 'src/DosboxEnhanced.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $root 'app')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if (-not $SkipTests) {
    $test = Start-Process -FilePath (Join-Path $root 'app/DosboxEnhanced.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw 'Self-tests failed; see tests/results/self-test.json.' }
}
Write-Host 'Ready: app/DosboxEnhanced.exe. Graphics are optional; see BUILDING.md to enable them.'
