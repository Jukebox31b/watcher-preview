[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$Configuration = "Release",
    [switch]$BoundedProcessSelfCheckOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts\WatcherPreview-win-x64"
}
$outputRoot = [IO.Path]::GetFullPath($OutputPath)
$mainProject = Join-Path $repoRoot "source\dcs-watcher-v2\DcsWatcherV2.csproj"
$intakeProject = Join-Path $repoRoot "source\dcs-watcher-v2-stage3-intake\DcsWatcherV2.Stage3Intake.csproj"
$regressionProject = Join-Path $repoRoot "source\dcs-watcher-v2-stage3-regression\DcsWatcherV2.Stage3Regression.csproj"
$sourceRoots = @(
    (Join-Path $repoRoot "source\dcs-watcher-v2"),
    (Join-Path $repoRoot "source\dcs-watcher-v2-stage3-intake"),
    (Join-Path $repoRoot "source\dcs-watcher-v2-stage3-regression")
)
$artifactRoot = Split-Path -Parent $outputRoot
$packageName = Split-Path -Leaf $outputRoot
$archivePath = Join-Path $artifactRoot "$packageName.zip"
$summaryPath = Join-Path $artifactRoot "$packageName.release-summary.json"
$releaseTestPath = Join-Path $artifactRoot "$packageName.release-test.json"
$regressionEvidencePath = Join-Path $artifactRoot "$packageName.stage3-regression.json"
$faultEvidencePath = Join-Path $artifactRoot "$packageName.stage3-fault.json"
$expectedPackagedReleaseTestTotal = 203
$expectedCommandCancellationTotal = 17
$expectedStage3RegressionTotal = 295
$evidenceStagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("WatcherReleaseEvidence-" + [Guid]::NewGuid().ToString("N"))
$regressionBuildRoot = Join-Path $evidenceStagingRoot "stage3-regression-build"
$stagedReleaseTestPath = Join-Path $evidenceStagingRoot "release-test.json"
$stagedRegressionEvidencePath = Join-Path $evidenceStagingRoot "stage3-regression.json"
$stagedFaultEvidencePath = Join-Path $evidenceStagingRoot "stage3-fault.json"
$stagedArchivePath = Join-Path $evidenceStagingRoot "$packageName.zip"
$stagedSummaryPath = Join-Path $evidenceStagingRoot "$packageName.release-summary.json"
$singleFileTargetsPath = Join-Path ([IO.Path]::GetTempPath()) ("WatcherSingleFile-" + [Guid]::NewGuid().ToString("N") + ".targets")
$externalArtifactsPrepared = $false

function Fail([string]$Message) {
    throw "Watcher Preview publish failed: $Message"
}

function Assert-SafeOutputPath([string]$Path) {
    $root = [IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrWhiteSpace($root) -or $Path.TrimEnd('\') -eq $root.TrimEnd('\')) {
        Fail "refusing to use a drive root as output: $Path"
    }
    if ($Path.TrimEnd('\') -eq $repoRoot.TrimEnd('\')) {
        Fail "the output directory cannot be the repository root"
    }
}

function Get-SourceSnapshotHash {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $records = foreach ($root in $sourceRoots) {
            Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @(".cs", ".csproj")
            } | ForEach-Object {
                $relative = $_.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
                $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$fileHash  $relative`n"
            }
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($records | Sort-Object) -join '')
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ReleaseInputSnapshotHash([bool]$HasGitCheckout) {
    if ($HasGitCheckout) {
        $listing = Invoke-GitText @("ls-files", "--cached", "--others", "--exclude-standard")
        $relativePaths = @($listing -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    else {
        $legacyStateDirectoryName = "." + (@("dcs", "watcher", "v2") -join "-")
        $excludedDirectoryNames = @(
            ".git", $legacyStateDirectoryName, ".watcher-package-staging", "app-v2", "artifacts",
            "bin", "obj", "package-staging", "TestResults", "test-output"
        )
        $outputPrefix = $outputRoot.TrimEnd('\') + '\'
        $relativePaths = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File | Where-Object {
            $fullName = $_.FullName
            $relative = $fullName.Substring($repoRoot.Length + 1)
            $segments = $relative -split '[\\/]'
            -not ($fullName -eq $outputRoot -or $fullName.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) -and
                $fullName -notin @($archivePath, $summaryPath, $releaseTestPath, $regressionEvidencePath, $faultEvidencePath) -and
                -not ($segments | Where-Object { $_ -in $excludedDirectoryNames })
        } | ForEach-Object {
            $_.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
        })
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $records = foreach ($relative in ($relativePaths | Sort-Object -Unique)) {
            $path = Join-Path $repoRoot $relative.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Fail "release input changed or disappeared while hashing: $relative"
            }
            $fileHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            "$fileHash  $($relative.Replace('\', '/'))`n"
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($records | Sort-Object) -join '')
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-GitText([string[]]$Arguments) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot ".git"))) { return $null }
    $result = & git -C $repoRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { Fail "git $($Arguments -join ' ') failed" }
    return (($result | Out-String).Trim())
}

function Get-EvidenceCount([object]$Value, [string]$Name) {
    if ($Value -is [bool] -or
        ($Value -isnot [byte] -and $Value -isnot [int16] -and $Value -isnot [int32] -and
         $Value -isnot [int64] -and $Value -isnot [uint16] -and $Value -isnot [uint32] -and
         $Value -isnot [uint64])) {
        Fail "evidence JSON property '$Name' must be an integer"
    }
    $count = [int64]$Value
    if ($count -lt 0) { Fail "evidence JSON property '$Name' cannot be negative" }
    return $count
}

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value.Contains('"')) { Fail "process arguments cannot contain a quote character" }
    if (-not [string]::IsNullOrEmpty($Value) -and $Value -notmatch '\s') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Stop-ProcessTree([Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return }
    if (Get-Command taskkill.exe -ErrorAction SilentlyContinue) {
        & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
    }
    else {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-BoundedProcess(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [int]$TimeoutMilliseconds,
    [string]$Label,
    [string]$StandardOutputPath,
    [string]$StandardErrorPath
) {
    foreach ($logPath in @($StandardOutputPath, $StandardErrorPath)) {
        if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
    }
    $quotedArguments = @($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $quotedArguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { Fail "$Label could not be started" }
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) {
            Stop-ProcessTree $process
            if (-not $process.WaitForExit(10000)) {
                Fail "$Label timed out and its process tree did not terminate"
            }
        }
        $process.WaitForExit()
        $outputTasks = [Threading.Tasks.Task[]]@($standardOutputTask, $standardErrorTask)
        if (-not [Threading.Tasks.Task]::WaitAll($outputTasks, 10000)) {
            Fail "$Label redirected output did not drain within 10 seconds"
        }
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($StandardOutputPath, $standardOutput, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($StandardErrorPath, $standardError, [Text.UTF8Encoding]::new($false))
        if ($timedOut) { Fail "$Label timed out after $($TimeoutMilliseconds / 1000) seconds" }
        return [int]$process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Assert-BoundedProcessExitCodes {
    $zeroExitCode = Invoke-BoundedProcess `
        $env:ComSpec `
        @("/d", "/c", "exit 0") `
        $repoRoot `
        10000 `
        "bounded-process zero-exit self-check" `
        (Join-Path $evidenceStagingRoot "process-self-check-zero.stdout.log") `
        (Join-Path $evidenceStagingRoot "process-self-check-zero.stderr.log")
    $nonzeroExitCode = Invoke-BoundedProcess `
        $env:ComSpec `
        @("/d", "/c", "exit 7") `
        $repoRoot `
        10000 `
        "bounded-process nonzero-exit self-check" `
        (Join-Path $evidenceStagingRoot "process-self-check-seven.stdout.log") `
        (Join-Path $evidenceStagingRoot "process-self-check-seven.stderr.log")
    if ($zeroExitCode -ne 0 -or $nonzeroExitCode -ne 7) {
        Fail "bounded-process self-check expected exit codes 0 and 7 but received '$zeroExitCode' and '$nonzeroExitCode'"
    }
}

function Invoke-PackagedReleaseTest([string]$Executable, [string]$EvidencePath) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        Fail "packaged main executable is missing: $Executable"
    }

    Write-Host "Running packaged offline release test..."
    $exitCode = Invoke-BoundedProcess `
        $Executable `
        @("--release-test", $EvidencePath) `
        (Split-Path -Parent $Executable) `
        900000 `
        "packaged offline release test" `
        (Join-Path $evidenceStagingRoot "release-test.stdout.log") `
        (Join-Path $evidenceStagingRoot "release-test.stderr.log")

    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        Fail "packaged offline release test did not create JSON evidence"
    }
    try {
        $result = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
    }
    catch {
        Fail "packaged offline release-test evidence is malformed JSON: $($_.Exception.Message)"
    }
    if ($null -eq $result) { Fail "packaged offline release-test evidence is empty" }
    foreach ($propertyName in @("Passed", "Total", "PassedCount", "Failed")) {
        if ($null -eq $result.PSObject.Properties[$propertyName]) {
            Fail "release-test JSON is missing property '$propertyName'"
        }
    }
    if ($exitCode -ne 0) { Fail "packaged offline release test exited with code $exitCode" }
    if ($result.Passed -isnot [bool] -or $result.Passed -ne $true) {
        Fail "packaged offline release-test JSON does not report Passed=true"
    }
    $total = Get-EvidenceCount $result.Total "Total"
    $passed = Get-EvidenceCount $result.PassedCount "PassedCount"
    $failed = Get-EvidenceCount $result.Failed "Failed"
    if ($failed -ne 0) { Fail "packaged offline release-test JSON reports Failed=$failed" }
    if ($total -ne ($passed + $failed)) {
        Fail "packaged offline release-test counts are inconsistent"
    }
    if ($total -ne $expectedPackagedReleaseTestTotal) {
        Fail "packaged offline release-test total is $total; expected the reviewed $expectedPackagedReleaseTestTotal-test candidate baseline"
    }

    if ($null -eq $result.PSObject.Properties["Suites"]) {
        Fail "release-test JSON is missing property 'Suites'"
    }
    $suiteSummaries = @($result.Suites | ForEach-Object {
        if ($_.Name -isnot [string] -or [string]::IsNullOrWhiteSpace($_.Name)) {
            Fail "release-test JSON contains a suite without a valid name"
        }
        $suitePassed = Get-EvidenceCount $_.Passed "$($_.Name).Passed"
        $suiteFailed = Get-EvidenceCount $_.Failed "$($_.Name).Failed"
        [ordered]@{
            name = $_.Name
            total = $suitePassed + $suiteFailed
            passed = $suitePassed
            failed = $suiteFailed
        }
    })
    if (($suiteSummaries | ForEach-Object { $_["total"] } | Measure-Object -Sum).Sum -ne $total) {
        Fail "release-test suite totals do not equal the top-level total"
    }
    $commandCancellation = @($suiteSummaries | Where-Object { $_["name"] -eq "command-cancellation" })
    if ($commandCancellation.Count -ne 1 -or
        $commandCancellation[0]["total"] -ne $expectedCommandCancellationTotal -or
        $commandCancellation[0]["passed"] -ne $expectedCommandCancellationTotal -or
        $commandCancellation[0]["failed"] -ne 0) {
        Fail "packaged release-test must include the reviewed $expectedCommandCancellationTotal/$expectedCommandCancellationTotal command-cancellation subset"
    }

    return [pscustomobject]@{
        Total = $total
        Passed = $passed
        Failed = $failed
        Suites = $suiteSummaries
        CommandCancellation = $commandCancellation[0]
    }
}

function Invoke-PackagedStage3Regression(
    [string]$PackagedIntakeExecutable,
    [string]$RegressionEvidence,
    [string]$FaultEvidence
) {
    if (-not (Test-Path -LiteralPath $PackagedIntakeExecutable -PathType Leaf)) {
        Fail "packaged intake executable is missing: $PackagedIntakeExecutable"
    }

    Write-Host "Building Stage3 regression runner..."
    New-Item -ItemType Directory -Path $regressionBuildRoot -Force | Out-Null
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    $buildExitCode = Invoke-BoundedProcess `
        $dotnetPath `
        @("build", $regressionProject, "--configuration", $Configuration, "--output", $regressionBuildRoot, "--nologo") `
        $repoRoot `
        900000 `
        "Stage3 regression build" `
        (Join-Path $evidenceStagingRoot "stage3-build.stdout.log") `
        (Join-Path $evidenceStagingRoot "stage3-build.stderr.log")
    if ($buildExitCode -ne 0) { Fail "Stage3 regression build exited with code $buildExitCode" }

    $regressionExecutable = Join-Path $regressionBuildRoot "DcsWatcherV2.Stage3Regression.exe"
    if (-not (Test-Path -LiteralPath $regressionExecutable -PathType Leaf)) {
        Fail "Stage3 regression build did not create its executable"
    }

    Write-Host "Running Stage3 regression against the packaged intake executable..."
    $regressionExitCode = Invoke-BoundedProcess `
        $regressionExecutable `
        @($RegressionEvidence, $FaultEvidence, $PackagedIntakeExecutable) `
        $regressionBuildRoot `
        1800000 `
        "Stage3 regression suite" `
        (Join-Path $evidenceStagingRoot "stage3-regression.stdout.log") `
        (Join-Path $evidenceStagingRoot "stage3-regression.stderr.log")

    foreach ($evidence in @($RegressionEvidence, $FaultEvidence)) {
        if (-not (Test-Path -LiteralPath $evidence -PathType Leaf)) {
            Fail "Stage3 regression did not create required JSON evidence: $evidence"
        }
    }
    try {
        $regression = Get-Content -LiteralPath $RegressionEvidence -Raw | ConvertFrom-Json
    }
    catch {
        Fail "Stage3 regression evidence is malformed JSON: $($_.Exception.Message)"
    }
    try {
        $fault = Get-Content -LiteralPath $FaultEvidence -Raw | ConvertFrom-Json
    }
    catch {
        Fail "Stage3 fault evidence is malformed JSON: $($_.Exception.Message)"
    }
    if ($null -eq $regression -or $null -eq $fault) { Fail "Stage3 evidence is empty" }
    foreach ($propertyName in @("Schema", "Stage2Passed", "Stage2Failed", "Stage3Passed", "Stage3Failed", "TotalPassed", "TotalFailed")) {
        if ($null -eq $regression.PSObject.Properties[$propertyName]) {
            Fail "Stage3 regression JSON is missing property '$propertyName'"
        }
    }
    if ($regression.Schema -isnot [string] -or $regression.Schema -ne "watcher-stage3-readiness-test-results-v1") {
        Fail "Stage3 regression JSON has an unexpected schema"
    }
    $stage2Passed = Get-EvidenceCount $regression.Stage2Passed "Stage2Passed"
    $stage2Failed = Get-EvidenceCount $regression.Stage2Failed "Stage2Failed"
    $stage3Passed = Get-EvidenceCount $regression.Stage3Passed "Stage3Passed"
    $stage3Failed = Get-EvidenceCount $regression.Stage3Failed "Stage3Failed"
    $totalPassed = Get-EvidenceCount $regression.TotalPassed "TotalPassed"
    $totalFailed = Get-EvidenceCount $regression.TotalFailed "TotalFailed"
    if ($totalPassed -ne ($stage2Passed + $stage3Passed) -or $totalFailed -ne ($stage2Failed + $stage3Failed)) {
        Fail "Stage3 regression counts are inconsistent"
    }
    if (($totalPassed + $totalFailed) -ne $expectedStage3RegressionTotal) {
        Fail "Stage3 regression total is $($totalPassed + $totalFailed); expected the authoritative $expectedStage3RegressionTotal-test baseline"
    }
    if ($totalFailed -ne 0 -or $totalPassed -ne $expectedStage3RegressionTotal) {
        Fail "Stage3 regression must pass $expectedStage3RegressionTotal/$expectedStage3RegressionTotal with zero failures"
    }

    foreach ($propertyName in @("Schema", "DuplicateAcceptances", "UnauthorizedDeliveries", "SilentRecoveries", "LiveOutputs")) {
        if ($null -eq $fault.PSObject.Properties[$propertyName]) {
            Fail "Stage3 fault JSON is missing property '$propertyName'"
        }
    }
    if ($fault.Schema -isnot [string] -or $fault.Schema -ne "watcher-stage3-fault-injection-results-v1") {
        Fail "Stage3 fault JSON has an unexpected schema"
    }
    $faultCounts = [ordered]@{
        DuplicateAcceptances = Get-EvidenceCount $fault.DuplicateAcceptances "DuplicateAcceptances"
        UnauthorizedDeliveries = Get-EvidenceCount $fault.UnauthorizedDeliveries "UnauthorizedDeliveries"
        SilentRecoveries = Get-EvidenceCount $fault.SilentRecoveries "SilentRecoveries"
        LiveOutputs = Get-EvidenceCount $fault.LiveOutputs "LiveOutputs"
    }
    foreach ($entry in $faultCounts.GetEnumerator()) {
        if ($entry.Value -ne 0) { Fail "Stage3 fault JSON reports $($entry.Key)=$($entry.Value)" }
    }
    if ($regressionExitCode -ne 0) { Fail "Stage3 regression suite exited with code $regressionExitCode" }

    return [pscustomobject]@{
        Total = $expectedStage3RegressionTotal
        Passed = $totalPassed
        Failed = $totalFailed
        Stage2Passed = $stage2Passed
        Stage2Failed = $stage2Failed
        Stage3Passed = $stage3Passed
        Stage3Failed = $stage3Failed
        DuplicateAcceptances = $faultCounts.DuplicateAcceptances
        UnauthorizedDeliveries = $faultCounts.UnauthorizedDeliveries
        SilentRecoveries = $faultCounts.SilentRecoveries
        LiveOutputs = $faultCounts.LiveOutputs
    }
}

function Invoke-DotNetPublish([string]$Project, [string]$Destination, [string]$Label) {
    Write-Host "Cleaning $Label build output..."
    & dotnet clean $Project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { Fail "dotnet clean failed for $Label" }

    Write-Host "Publishing $Label..."
    $arguments = @(
        "publish", $Project,
        "--configuration", $Configuration,
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $Destination,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=false",
        "-p:EnableCompressionInSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:PublishAot=false",
        "-p:EnableSingleFileAnalyzer=false",
        "-p:CustomAfterMicrosoftCommonTargets=$singleFileTargetsPath",
        "-p:DebugSymbols=false",
        "-p:DebugType=None"
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for $Label" }
}

try {
    if ($BoundedProcessSelfCheckOnly) {
        New-Item -ItemType Directory -Path $evidenceStagingRoot -Force | Out-Null
        Assert-BoundedProcessExitCodes
        Write-Host "Bounded process self-check passed: exit codes 0 and 7 were distinguished."
        return
    }
    Assert-SafeOutputPath $outputRoot
    foreach ($required in @($mainProject, $intakeProject, $regressionProject)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { Fail "missing project: $required" }
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail ".NET SDK was not found on PATH" }

    $hasGitCheckout = Test-Path -LiteralPath (Join-Path $repoRoot ".git")
    if ($hasGitCheckout -and -not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "Git is required to verify checkout cleanliness"
    }
    $sourceDirty = $null
    $provenance = $null
    $provenanceHashBefore = $null
    if ($hasGitCheckout) {
        $sourceCommit = Invoke-GitText @("rev-parse", "HEAD")
        $gitStatus = Invoke-GitText @("status", "--porcelain=v1", "--untracked-files=all")
        $sourceDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
        if ($sourceDirty) { Fail "Git worktree is dirty; final distribution packages require source.dirty=false" }
    }
    else {
        $provenancePath = Join-Path $repoRoot "SOURCE_PROVENANCE.json"
        if (-not (Test-Path -LiteralPath $provenancePath)) {
            Fail "no Git commit or SOURCE_PROVENANCE.json is available"
        }
        try {
            $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
        }
        catch {
            Fail "SOURCE_PROVENANCE.json is malformed: $($_.Exception.Message)"
        }
        $provenanceCommit = $provenance.PSObject.Properties["sourceCommit"]
        if ($null -eq $provenanceCommit) { Fail "SOURCE_PROVENANCE.json is missing sourceCommit" }
        $sourceCommit = $provenanceCommit.Value
        $provenanceDirty = $provenance.PSObject.Properties["sourceDirty"]
        if ($null -ne $provenanceDirty) {
            if ($provenanceDirty.Value -isnot [bool]) { Fail "SOURCE_PROVENANCE.json sourceDirty must be boolean" }
            $sourceDirty = $provenanceDirty.Value
            if ($sourceDirty) { Fail "source provenance reports a dirty tree; final distribution is blocked" }
        }
        $provenanceHashBefore = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ([string]::IsNullOrWhiteSpace($sourceCommit)) { Fail "source commit provenance is empty" }
    $sourceHashBefore = Get-SourceSnapshotHash
    $releaseInputHashBefore = Get-ReleaseInputSnapshotHash $hasGitCheckout
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "dotnet --version failed" }

    $singleFileTargets = @'
<Project>
  <Target Name="WatcherKeepApplicationAssembliesOutsideBundle" BeforeTargets="_ComputeFilesToBundle">
    <ItemGroup>
      <_WatcherApplicationAssembly Include="@(ResolvedFileToPublish)"
        Condition="'%(ResolvedFileToPublish.Filename)%(ResolvedFileToPublish.Extension)' == 'DcsWatcherV2.dll' Or '%(ResolvedFileToPublish.Filename)%(ResolvedFileToPublish.Extension)' == 'DcsWatcherV2.Stage3Intake.dll'" />
      <ResolvedFileToPublish Remove="@(_WatcherApplicationAssembly)" />
      <ResolvedFileToPublish Include="@(_WatcherApplicationAssembly)" ExcludeFromSingleFile="true" />
    </ItemGroup>
  </Target>
</Project>
'@
    [IO.File]::WriteAllText($singleFileTargetsPath, $singleFileTargets, [Text.UTF8Encoding]::new($false))
    $singleFileTargetsHash = (Get-FileHash -LiteralPath $singleFileTargetsPath -Algorithm SHA256).Hash.ToLowerInvariant()

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $evidenceStagingRoot -Force | Out-Null
    Assert-BoundedProcessExitCodes
    if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
    foreach ($staleArtifact in @($archivePath, $summaryPath, $releaseTestPath, $regressionEvidencePath, $faultEvidencePath)) {
        if (Test-Path -LiteralPath $staleArtifact) { Remove-Item -LiteralPath $staleArtifact -Force }
    }
    $externalArtifactsPrepared = $true
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Invoke-DotNetPublish $mainProject $outputRoot "Watcher Preview"
    $intakeOutput = Join-Path $outputRoot "tools\intake"
    Invoke-DotNetPublish $intakeProject $intakeOutput "intake verifier"

    $staleIntakeRuntimeConfig = Join-Path $intakeOutput "DcsWatcherV2.runtimeconfig.json"
    if (Test-Path -LiteralPath $staleIntakeRuntimeConfig) { Remove-Item -LiteralPath $staleIntakeRuntimeConfig -Force }
    Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter *.pdb | Remove-Item -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $outputRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "RUN-WATCHER-PREVIEW.cmd") -Destination $outputRoot
    $docsOutput = Join-Path $outputRoot "docs"
    New-Item -ItemType Directory -Path $docsOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination (Join-Path $outputRoot "README.md")
    foreach ($document in @("RELEASE_PACKAGING.md", "PRIVACY.md", "SECURITY.md", "SUPPORT_MATRIX.md", "UNSIGNED_PREVIEW_NOTICE.md")) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\watcher-release\$document") -Destination $docsOutput
    }

    $releaseTestTotals = Invoke-PackagedReleaseTest `
        (Join-Path $outputRoot "DcsWatcherV2.exe") `
        $stagedReleaseTestPath
    $intakeExecutable = Join-Path $intakeOutput "DcsWatcherV2.Stage3Intake.exe"
    $stage3Totals = Invoke-PackagedStage3Regression `
        $intakeExecutable `
        $stagedRegressionEvidencePath `
        $stagedFaultEvidencePath

    $sourceHashAfter = Get-SourceSnapshotHash
    $releaseInputHashAfter = Get-ReleaseInputSnapshotHash $hasGitCheckout
    if ($sourceHashBefore -ne $sourceHashAfter) { Fail "Watcher source changed while the package was building; retry from a stable tree" }
    if ($releaseInputHashBefore -ne $releaseInputHashAfter) { Fail "release inputs changed while the package was building; retry from a stable tree" }
    if ($hasGitCheckout) {
        $commitAfter = Invoke-GitText @("rev-parse", "HEAD")
        if ($sourceCommit -ne $commitAfter) { Fail "Git HEAD changed while the package was building" }
    }
    else {
        try {
            $provenanceAfter = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
        }
        catch {
            Fail "SOURCE_PROVENANCE.json became unreadable or malformed: $($_.Exception.Message)"
        }
        $provenanceCommitAfter = $provenanceAfter.PSObject.Properties["sourceCommit"]
        if ($null -eq $provenanceCommitAfter -or $provenanceCommitAfter.Value -ne $sourceCommit) {
            Fail "SOURCE_PROVENANCE.json sourceCommit changed while the package was building"
        }
        $provenanceDirtyAfter = $provenanceAfter.PSObject.Properties["sourceDirty"]
        if (($null -eq $provenanceDirty) -ne ($null -eq $provenanceDirtyAfter)) {
            Fail "SOURCE_PROVENANCE.json sourceDirty presence changed while the package was building"
        }
        if ($null -ne $provenanceDirtyAfter) {
            if ($provenanceDirtyAfter.Value -isnot [bool] -or $provenanceDirtyAfter.Value -ne $sourceDirty) {
                Fail "SOURCE_PROVENANCE.json sourceDirty changed while the package was building"
            }
        }
        $provenanceHashAfter = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($provenanceHashBefore -ne $provenanceHashAfter) {
            Fail "SOURCE_PROVENANCE.json changed while the package was building"
        }
    }

    $depsPath = Join-Path (Split-Path -Parent $mainProject) "obj\$Configuration\net8.0-windows\win-x64\DcsWatcherV2.deps.json"
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { Fail "generated dependency inventory is missing: $depsPath" }
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $bundledRuntimes = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "runtimepack.*" } | Sort-Object)
    $files = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object { $_.Name -ne "manifest.json" } | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } | Sort-Object path)
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "Watcher Preview"
        version = "0.1.0-preview.3"
        channel = "Preview"
        createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
        source = [ordered]@{
            commit = $sourceCommit
            dirty = $sourceDirty
            treeHash = $sourceHashBefore
            treeHashAlgorithm = "sha256(sorted sha256-plus-repo-relative-path records for selected source files)"
            releaseInputTreeHash = $releaseInputHashBefore
            releaseInputTreeHashAlgorithm = "sha256(sorted sha256-plus-repo-relative-path records; Git includes all tracked and non-ignored untracked files)"
        }
        toolchain = [ordered]@{
            dotnetSdk = $sdkVersion
            targetFramework = "net8.0-windows"
            runtimeIdentifier = "win-x64"
            selfContained = $true
            publishSingleFile = $true
            includeNativeLibrariesForSelfExtract = $true
            includeAllContentForSelfExtract = $false
            compressionEnabled = $true
            trimmed = $false
            nativeAot = $false
            applicationAssemblySidecars = @("DcsWatcherV2.dll", "tools/intake/DcsWatcherV2.dll", "tools/intake/DcsWatcherV2.Stage3Intake.dll")
            bundledRuntimePacks = $bundledRuntimes
        }
        build = [ordered]@{
            configuration = $Configuration
            authoritativeEntrypoint = [ordered]@{
                path = "PUBLISH-WATCHER-PREVIEW.ps1"
                role = "Authoritative publisher; performs clean publish, sidecar customization, packaged offline release test, bounded Stage3 regression and fault gates against packaged intake, manifest, ZIP, and evidence-bound summary generation."
                sha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot "PUBLISH-WATCHER-PREVIEW.ps1") -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            invocation = ".\PUBLISH-WATCHER-PREVIEW.ps1 -Configuration $Configuration"
            generatedSingleFileTargets = [ordered]@{
                role = "CustomAfterMicrosoftCommonTargets input that keeps Watcher application assemblies outside the single-file bundles for runtime attestation."
                sha256 = $singleFileTargetsHash
                appliedTo = @(
                    "source/dcs-watcher-v2/DcsWatcherV2.csproj",
                    "source/dcs-watcher-v2-stage3-intake/DcsWatcherV2.Stage3Intake.csproj"
                )
            }
        }
        inventoryNote = "File inventory excludes manifest.json because a manifest cannot contain its own stable hash."
        files = $files
    }
    $manifestPath = Join-Path $outputRoot "manifest.json"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $packageFiles = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $outputRoot,
        $stagedArchivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)

    $archive = Get-Item -LiteralPath $stagedArchivePath
    $releaseTestEvidence = Get-Item -LiteralPath $stagedReleaseTestPath
    $regressionEvidence = Get-Item -LiteralPath $stagedRegressionEvidencePath
    $faultEvidence = Get-Item -LiteralPath $stagedFaultEvidencePath
    $summary = [ordered]@{
        schemaVersion = 2
        product = "Watcher Preview"
        version = "0.1.0-preview.3"
        createdUtc = $manifest.createdUtc
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        packageDirectory = $packageName
        packageFileCount = $packageFiles.Count
        packageSizeBytes = ($packageFiles | Measure-Object Length -Sum).Sum
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        mainExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $outputRoot "DcsWatcherV2.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
        mainApplicationDllSha256 = (Get-FileHash -LiteralPath (Join-Path $outputRoot "DcsWatcherV2.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
        intakeExecutableSha256 = (Get-FileHash -LiteralPath $intakeExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        releaseTest = [ordered]@{
            name = "Packaged Preview offline application suite"
            executionTarget = "Packaged DcsWatcherV2.exe"
            command = "DcsWatcherV2.exe --release-test <json-path>"
            scope = "Runs the Watcher application release suites from the packaged executable. Subsuite counts are included in, not additional to, the top-level total."
            path = [IO.Path]::GetFileName($releaseTestPath)
            fileName = [IO.Path]::GetFileName($releaseTestPath)
            sizeBytes = $releaseTestEvidence.Length
            sha256 = (Get-FileHash -LiteralPath $stagedReleaseTestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            total = $releaseTestTotals.Total
            passed = $releaseTestTotals.Passed
            failed = $releaseTestTotals.Failed
            includedSuites = $releaseTestTotals.Suites
            commandCancellationSubset = $releaseTestTotals.CommandCancellation
        }
        stage3Regression = [ordered]@{
            name = "Stage 3 provenance and intake regression suite"
            executionTarget = "Regression runner exercising the packaged DcsWatcherV2.Stage3Intake.exe"
            command = "DcsWatcherV2.Stage3Regression.exe <regression-json> <fault-json> <packaged-intake-executable>"
            scope = "Runs Stage 2 and Stage 3 provenance, verifier, replay, lineage, and fault fixtures against the packaged intake executable. It is separate from the packaged Preview offline application suite."
            path = [IO.Path]::GetFileName($regressionEvidencePath)
            fileName = [IO.Path]::GetFileName($regressionEvidencePath)
            sizeBytes = $regressionEvidence.Length
            sha256 = (Get-FileHash -LiteralPath $stagedRegressionEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
            expectedTotal = $expectedStage3RegressionTotal
            total = $stage3Totals.Total
            passed = $stage3Totals.Passed
            failed = $stage3Totals.Failed
            stage2Passed = $stage3Totals.Stage2Passed
            stage2Failed = $stage3Totals.Stage2Failed
            stage3Passed = $stage3Totals.Stage3Passed
            stage3Failed = $stage3Totals.Stage3Failed
            packagedIntakeExecutableSha256 = (Get-FileHash -LiteralPath $intakeExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        stage3Fault = [ordered]@{
            path = [IO.Path]::GetFileName($faultEvidencePath)
            fileName = [IO.Path]::GetFileName($faultEvidencePath)
            sizeBytes = $faultEvidence.Length
            sha256 = (Get-FileHash -LiteralPath $stagedFaultEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
            duplicateAcceptances = $stage3Totals.DuplicateAcceptances
            unauthorizedDeliveries = $stage3Totals.UnauthorizedDeliveries
            silentRecoveries = $stage3Totals.SilentRecoveries
            liveOutputs = $stage3Totals.LiveOutputs
        }
        archive = [ordered]@{
            fileName = [IO.Path]::GetFileName($archivePath)
            sizeBytes = $archive.Length
            sha256 = (Get-FileHash -LiteralPath $stagedArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        unsignedPreviewWarning = "This Preview is unsigned. Windows may show Microsoft Defender SmartScreen messages including 'Windows protected your PC' and 'Unknown publisher'. SHA-256 verifies file equality only; it does not prove publisher identity or safety."
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stagedSummaryPath -Encoding UTF8

    Move-Item -LiteralPath $stagedReleaseTestPath -Destination $releaseTestPath
    Move-Item -LiteralPath $stagedRegressionEvidencePath -Destination $regressionEvidencePath
    Move-Item -LiteralPath $stagedFaultEvidencePath -Destination $faultEvidencePath
    Move-Item -LiteralPath $stagedArchivePath -Destination $archivePath
    Move-Item -LiteralPath $stagedSummaryPath -Destination $summaryPath

    Write-Host "Watcher Preview package created: $outputRoot"
    Write-Host "Files: $($files.Count + 1); source commit: $sourceCommit; source tree: $sourceHashBefore"
    Write-Host "Archive: $archivePath"
    Write-Host "Archive SHA-256: $($summary.archive.sha256); bytes: $($summary.archive.sizeBytes)"
    Write-Host "Release test: $releaseTestPath"
    Write-Host "Release test SHA-256: $($summary.releaseTest.sha256); bytes: $($summary.releaseTest.sizeBytes); total/passed/failed: $($summary.releaseTest.total)/$($summary.releaseTest.passed)/$($summary.releaseTest.failed)"
    Write-Host "Stage3 regression: $regressionEvidencePath"
    Write-Host "Stage3 regression SHA-256: $($summary.stage3Regression.sha256); bytes: $($summary.stage3Regression.sizeBytes); total/passed/failed: $($summary.stage3Regression.total)/$($summary.stage3Regression.passed)/$($summary.stage3Regression.failed)"
    Write-Host "Stage3 fault evidence: $faultEvidencePath"
    Write-Host "Stage3 fault SHA-256: $($summary.stage3Fault.sha256); bytes: $($summary.stage3Fault.sizeBytes); duplicate/unauthorized/silent/live: $($summary.stage3Fault.duplicateAcceptances)/$($summary.stage3Fault.unauthorizedDeliveries)/$($summary.stage3Fault.silentRecoveries)/$($summary.stage3Fault.liveOutputs)"
    Write-Host "Release summary: $summaryPath"
}
catch {
    if ($externalArtifactsPrepared) {
        foreach ($artifact in @($archivePath, $summaryPath, $releaseTestPath, $regressionEvidencePath, $faultEvidencePath)) {
            if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue }
        }
    }
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    if (Test-Path -LiteralPath $singleFileTargetsPath) {
        Remove-Item -LiteralPath $singleFileTargetsPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $evidenceStagingRoot) {
        Remove-Item -LiteralPath $evidenceStagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
