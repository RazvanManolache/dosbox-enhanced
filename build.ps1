param(
    [switch]$Graphics,
    [string]$PhoenixSdk = '',
    [switch]$BuildPhoenix,
    [string]$CudaCompiler = '',
    [string]$CoreLibrary = '',
    [switch]$SkipTests,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'app')
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
        $phoenixSource = Join-Path $root 'external/phoenix'
        # Phoenix 0.17 embeds a shader literal larger than MSVC accepts.
        # This patch only splits adjacent literals; shader bytes stay identical.
        $shaderPatch = Join-Path $root 'tools/phoenix-msvc-shader.patch'
        git -C $phoenixSource apply --reverse --check $shaderPatch 2>$null
        if ($LASTEXITCODE -ne 0) {
            git -C $phoenixSource apply --check $shaderPatch
            if ($LASTEXITCODE -ne 0) { throw 'Phoenix shader compatibility patch does not match the pinned source.' }
            git -C $phoenixSource apply $shaderPatch
            if ($LASTEXITCODE -ne 0) { throw 'Phoenix shader compatibility patch failed.' }
        }
        try {
        $phoenixRevision = (git -C $phoenixSource rev-parse HEAD).Trim()
        $phoenixBuild = Join-Path $root ('out/phoenix-build-' + $phoenixRevision.Substring(0, 12))
        cmake -S (Join-Path $phoenixSource 'native_runtime') -B $phoenixBuild -G 'Visual Studio 17 2022' -A x64 -DPHOENIX_ENABLE_NATIVE_RTX=ON -DBUILD_TESTING=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded "-DCMAKE_SHARED_LINKER_FLAGS=/PDBALTPATH:phoenix_native.pdb" "-DPHOENIX_NGX_INCLUDE=$(Join-Path $root 'external/streamline/external/ngx-sdk/include')"
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix configuration failed.' }
        cmake --build $phoenixBuild --config Release --target phoenix_native --parallel 4
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix build failed.' }
        New-Item -ItemType Directory -Path (Join-Path $PhoenixSdk 'bin'),(Join-Path $PhoenixSdk 'lib'),(Join-Path $PhoenixSdk 'include') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $phoenixBuild 'Release/phoenix_native.dll') -Destination (Join-Path $PhoenixSdk 'bin')
        Copy-Item -LiteralPath (Join-Path $phoenixBuild 'Release/phoenix_native.lib') -Destination (Join-Path $PhoenixSdk 'lib')
        Copy-Item -LiteralPath (Join-Path $phoenixSource 'native_runtime/phoenix_native.h') -Destination (Join-Path $PhoenixSdk 'include')
        Copy-Item -LiteralPath (Join-Path $phoenixSource 'native_runtime/phoenix.h') -Destination (Join-Path $PhoenixSdk 'include')
        if ($CudaCompiler) {
            python (Join-Path $phoenixSource 'tools/production_build_native_kernels.py') (Join-Path $PhoenixSdk 'native') --compiler $CudaCompiler
            if ($LASTEXITCODE -ne 0) { throw 'Phoenix kernel compilation failed.' }
        }
    }
        finally {
            git -C $phoenixSource apply --reverse --check $shaderPatch 2>$null
            if ($LASTEXITCODE -eq 0) { git -C $phoenixSource apply --reverse $shaderPatch; if($LASTEXITCODE -ne 0){throw 'Could not restore the Phoenix shader source.'} }
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
dotnet publish (Join-Path $root 'src/DosboxEnhanced.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if (-not $SkipTests) {
    $test = Start-Process -FilePath (Join-Path $OutputDirectory 'DosboxEnhanced.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw 'Self-tests failed; see tests/results/self-test.json.' }
}
Write-Host 'Ready: app/DosboxEnhanced.exe. Graphics are optional; see BUILDING.md to enable them.'
