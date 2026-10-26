$ErrorActionPreference = 'Stop'
$cleanupRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$allowed = @('src\bin', 'src\obj', 'native\build', 'app-update', 'out')

function Get-CheckedFiles([string]$Folder) {
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Folder)
    while ($stack.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to traverse link in cleanup target: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) { $stack.Push($entry.FullName) } else { $entry }
        }
    }
}

function Get-ProtectedState {
    $state = @{}
    $stack = [Collections.Generic.Stack[string]]::new()
    foreach ($area in @('app', 'runtime', 'data')) {
        $folder = Join-Path $cleanupRoot $area
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "Missing protected directory: $folder" }
        $stack.Push($folder)
    }
    while ($stack.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $state[$entry.FullName] = 'link:' + ($entry.Target -join '|')
            } elseif ($entry.PSIsContainer) {
                $state[$entry.FullName] = 'directory'
                $stack.Push($entry.FullName)
            } else {
                $state[$entry.FullName] = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
            }
        }
    }
    return ,$state
}

if (Get-Process -Name DosboxEnhanced -ErrorAction SilentlyContinue) {
    throw 'Close DOSBox Enhanced before cleaning its generated folders.'
}
$targets = @(foreach ($relative in $allowed) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $cleanupRoot $relative))
    if (-not $candidate.StartsWith($cleanupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup target escaped the workspace.'
    }
    if (Test-Path -LiteralPath $candidate) {
        $entry = Get-Item -LiteralPath $candidate -Force
        if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unsafe cleanup target: $candidate"
        }
        $files = @(Get-CheckedFiles $candidate)
        [pscustomobject]@{ relative = $relative; path = $candidate; files = $files.Count; bytes = ($files | Measure-Object Length -Sum).Sum }
    }
})
$reportPath = Join-Path $cleanupRoot 'data\maintenance\CLEANUP_COMPARISON.json'
New-Item -ItemType Directory -Path (Split-Path $reportPath -Parent) -Force | Out-Null
$before = Get-ProtectedState
$receipt = if (Test-Path -LiteralPath $reportPath) {
    Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
foreach ($target in $targets) {
    Remove-Item -LiteralPath $target.path -Recurse -Force
    if (Test-Path -LiteralPath $target.path) { throw "Cleanup target remains: $($target.path)" }
}
$after = Get-ProtectedState
if ($before.Count -ne $after.Count) { throw 'Protected app/runtime/data entry count changed.' }
foreach ($path in $before.Keys) {
    if (-not $after.ContainsKey($path) -or $before[$path] -ne $after[$path]) {
        throw "Protected file or link changed: $path"
    }
}
$receipt | Add-Member -NotePropertyName cleanup -NotePropertyValue ([ordered]@{
    status = 'completed'; completed_at = (Get-Date -Format o); removed = $targets
    removed_bytes = ($targets | Measure-Object bytes -Sum).Sum
    protected_entries_verified_unchanged = $before.Count
}) -Force
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host ('Removed {0:N1} MiB. Active app, runtime and personal data verified unchanged.' -f (($targets | Measure-Object bytes -Sum).Sum / 1MB))
