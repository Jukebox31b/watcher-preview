[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$destinationPrefix = $destinationRoot.TrimEnd('\') + '\'
$sourceRelativeDirectories = @(
    "source\dcs-watcher-v2",
    "source\dcs-watcher-v2-stage3-intake",
    "source\dcs-watcher-v2-stage3-regression"
)
$rootFiles = @("PUBLISH-WATCHER-PREVIEW.ps1", "PUBLISH-WATCHER-PREVIEW.cmd", "RUN-WATCHER-PREVIEW.cmd", "LICENSE")
$publicDocuments = @(
    "PRIVACY.md",
    "RELEASE_PACKAGING.md",
    "SECURITY.md",
    "SUPPORT_MATRIX.md",
    "UNSIGNED_PREVIEW_NOTICE.md"
)
$toolFiles = @("New-PublicWatcherStage.ps1", "Test-PublicSanitization.ps1")

function Fail([string]$Message) {
    throw "Public Watcher staging failed: $Message"
}

function Get-SourceFiles {
    foreach ($relativeDirectory in $sourceRelativeDirectories) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $relativeDirectory) -Recurse -File | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @(".cs", ".csproj")
        }
    }
}

function Get-HashForFiles([IO.FileInfo[]]$Files) {
    $records = foreach ($file in ($Files | Sort-Object FullName -Unique)) {
        if (-not (Test-Path -LiteralPath $file.FullName -PathType Leaf)) {
            Fail "missing public-stage input: $($file.FullName)"
        }
        $relative = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative`n"
    }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($records | Sort-Object) -join '')
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $algorithm.Dispose() }
}

function Get-SourceSnapshotHash {
    return (Get-HashForFiles @(Get-SourceFiles))
}

function Get-PublicStageInputHash {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-SourceFiles)) { $paths.Add($file.FullName) }
    foreach ($file in $rootFiles) { $paths.Add((Join-Path $repoRoot $file)) }
    $paths.Add((Join-Path $repoRoot "docs\watcher-release\PUBLIC_README.md"))
    foreach ($file in $publicDocuments) { $paths.Add((Join-Path $repoRoot "docs\watcher-release\$file")) }
    foreach ($file in $toolFiles) { $paths.Add((Join-Path $PSScriptRoot $file)) }
    return (Get-HashForFiles @($paths | ForEach-Object { Get-Item -LiteralPath $_ }))
}

function Copy-SourceTree([string]$Source, [string]$DestinationPath) {
    Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @(".cs", ".csproj")
    } | ForEach-Object {
        $relative = $_.FullName.Substring($Source.Length).TrimStart('\', '/')
        $target = Join-Path $DestinationPath $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}

try {
    if ($destinationRoot -eq $repoRoot -or $destinationRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "destination must be outside the source repository"
    }
    if ($repoRoot.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "destination cannot contain the source repository"
    }
    if ($destinationRoot.TrimEnd('\') -eq [IO.Path]::GetPathRoot($destinationRoot).TrimEnd('\')) {
        Fail "destination cannot be a drive root"
    }

    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { Fail "unable to read the source commit" }
    $gitStatus = (& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "unable to read source worktree status" }
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
    $sourceHash = Get-SourceSnapshotHash
    $stageInputHash = Get-PublicStageInputHash

    if (Test-Path -LiteralPath $destinationRoot) { Remove-Item -LiteralPath $destinationRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

    foreach ($relativeDirectory in $sourceRelativeDirectories) {
        Copy-SourceTree (Join-Path $repoRoot $relativeDirectory) (Join-Path $destinationRoot $relativeDirectory)
    }
    foreach ($file in $rootFiles) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $destinationRoot
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\watcher-release\PUBLIC_README.md") -Destination (Join-Path $destinationRoot "README.md")

    $docsTarget = Join-Path $destinationRoot "docs\watcher-release"
    New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
    foreach ($file in $publicDocuments) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\watcher-release\$file") -Destination $docsTarget
    }

    $toolsTarget = Join-Path $destinationRoot "tools\watcher-release"
    New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
    foreach ($file in $toolFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $toolsTarget
    }

    $sourceHashAfter = Get-SourceSnapshotHash
    $stageInputHashAfter = Get-PublicStageInputHash
    $commitAfter = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim()
    if ($sourceHash -ne $sourceHashAfter) { Fail "Watcher source changed while the public stage was being copied; retry from a stable tree" }
    if ($stageInputHash -ne $stageInputHashAfter) { Fail "A public-stage input changed while it was being copied; retry from a stable tree" }
    if ($commit -ne $commitAfter) { Fail "Git HEAD changed while the public stage was being copied" }

    [ordered]@{
        schemaVersion = 1
        sourceCommit = $commit
        sourceDirty = $sourceDirty
        sourceTreeHash = $sourceHash
        sourceTreeHashAlgorithm = "sha256(sorted sha256-plus-repo-relative-path records for selected source files)"
        publicStageInputHash = $stageInputHash
        publicStageInputHashAlgorithm = "sha256(sorted sha256-plus-repo-relative-path records for every selected stage input)"
        stagedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        gitHistoryIncluded = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationRoot "SOURCE_PROVENANCE.json") -Encoding UTF8

    & (Join-Path $toolsTarget "Test-PublicSanitization.ps1") -Path $destinationRoot
    if ($LASTEXITCODE -ne 0) {
        Fail "sanitization blockers remain; the stage was retained for local remediation at $destinationRoot"
    }
    Write-Host "Sanitized public Watcher source staged at: $destinationRoot"
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
