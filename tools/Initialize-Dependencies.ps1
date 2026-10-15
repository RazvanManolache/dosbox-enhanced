param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content -LiteralPath (Join-Path $root 'dependencies.lock.json') -Raw | ConvertFrom-Json
foreach ($dependency in $lock.dependencies) {
    $folder = Join-Path $root $dependency.path
    if (-not (Test-Path -LiteralPath (Join-Path $folder '.git'))) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        git -C $folder init --quiet
        git -C $folder remote add origin $dependency.url
        git -C $folder fetch --quiet --depth 1 origin $dependency.revision
        if ($LASTEXITCODE -ne 0) { throw "Cannot fetch $($dependency.name) at its locked revision." }
        # Unneeded Nsight PDFs use LFS. They are not inputs to this build.
        git -C $folder -c filter.lfs.required=false -c filter.lfs.process= -c filter.lfs.smudge=cat -c filter.lfs.clean=cat checkout --quiet --detach FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw "Cannot check out $($dependency.name)." }
    }
    $actual = (git -C $folder rev-parse HEAD).Trim()
    if ($actual -ne $dependency.revision) { throw "$($dependency.name) differs from dependencies.lock.json. Restore the pinned submodule revision first." }
}
Write-Host 'External dependencies match the pinned revisions.'
