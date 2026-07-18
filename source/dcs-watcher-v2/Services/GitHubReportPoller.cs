using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ReportScanResult(
    bool FoundAnyReport,
    bool FoundNewReport,
    bool DuplicateSuppressed,
    ReportCandidate? Candidate,
    ReportCandidate? NewestCandidate,
    string Message);

public sealed class GitHubReportPoller
{
    private const string ReportPrefix = "CGPT-REPORT-";
    private const string ReportSuffix = ".md";
    private const string GitRemotePollMode = "GitRemote";
    private const string GitHubPollMode = "GitHub";
    private const string LocalFolderPollMode = "LocalFolder";
    private const string GitHubThenLocalFallbackPollMode = "GitHubThenLocalFallback";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<ReportScanResult> ScanAsync(
        AppConfig config,
        AppState state,
        bool includeCompleted = false,
        CancellationToken cancellationToken = default)
    {
        var candidatesResult = await GetCandidatesAsync(config, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ScanCandidates(candidatesResult, state, includeCompleted);
    }

    public ReportScanResult ScanCandidatesForSelfTest(
        IReadOnlyList<ReportCandidate> candidates,
        AppState state,
        bool includeCompleted = false)
    {
        return ScanCandidates(CandidateListResult.Ok(candidates), state, includeCompleted);
    }

    public async Task<CandidateListResult> GetCandidatesAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = ResolveReportPollMode(config);
        if (mode.Equals(LocalFolderPollMode, StringComparison.OrdinalIgnoreCase))
        {
            return GetCandidatesFromLocalFolder(config, cancellationToken);
        }

        if (mode.Equals(GitRemotePollMode, StringComparison.OrdinalIgnoreCase))
        {
            return await GetCandidatesFromConfiguredGitRemoteAsync(config, cancellationToken);
        }

        var githubResult = await GetCandidatesFromGitHubAsync(config, cancellationToken);
        if (githubResult.Success ||
            mode.Equals(GitHubPollMode, StringComparison.OrdinalIgnoreCase))
        {
            return githubResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var localResult = GetCandidatesFromLocalFolder(config, cancellationToken);
        if (localResult.Success)
        {
            return localResult;
        }

        return CandidateListResult.Fail($"GitHub report polling failed: {githubResult.Message}; local fallback failed: {localResult.Message}");
    }

    private static async Task<CandidateListResult> GetCandidatesFromGitHubAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildGitHubReportsLocation(config, out var location, out var locationError))
        {
            return CandidateListResult.Fail(locationError);
        }

        var apiUrl = BuildContentsApiUrl(location);
        string json;
        try
        {
            using var request = CreateGitHubRequest(apiUrl);
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var apiFailure = $"GitHub contents API request failed: {(int)response.StatusCode} {response.ReasonPhrase}. URL: {apiUrl}";
                var gitRemoteResult = await GetCandidatesFromGitHubRemoteCacheAsync(config, location, cancellationToken);
                return gitRemoteResult.Success
                    ? gitRemoteResult
                    : CandidateListResult.Fail($"{apiFailure}; Git remote cache failed: {gitRemoteResult.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var apiFailure = $"GitHub contents API request failed: {ex.Message}. URL: {apiUrl}";
            var gitRemoteResult = await GetCandidatesFromGitHubRemoteCacheAsync(config, location, cancellationToken);
            return gitRemoteResult.Success
                ? gitRemoteResult
                : CandidateListResult.Fail($"{apiFailure}; Git remote cache failed: {gitRemoteResult.Message}");
        }

        var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json, JsonOptions) ?? [];
        var reportItems = items
            .Where(item => item.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
            .Where(item => IsReportFileName(item.Name))
            .ToList();

        var candidates = new List<ReportCandidate>();
        foreach (var item in reportItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                return CandidateListResult.Fail($"GitHub report has no download_url: {item.Path}");
            }

            var download = await DownloadReportAsync(item.DownloadUrl, cancellationToken);
            if (!download.Success || download.Bytes is null)
            {
                return CandidateListResult.Fail(download.Message);
            }

            candidates.Add(BuildRemoteCandidate(
                config,
                item.Path,
                item.Name,
                string.IsNullOrWhiteSpace(item.HtmlUrl) ? BuildReportBlobUrl(config, item.Path) : item.HtmlUrl,
                item.DownloadUrl,
                download.Bytes,
                blobIdentity: item.Sha));
        }

        var ordered = OrderCandidatesByPublication(candidates);

        return CandidateListResult.Ok(ordered);
    }

    public static ReportCandidate BuildCandidateForSelfTest(
        AppConfig config,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalizedPath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        return BuildRemoteCandidate(
            config,
            normalizedPath,
            fileName,
            BuildReportBlobUrl(config, normalizedPath),
            BuildReportBlobUrl(config, normalizedPath),
            bytes,
            lastWriteTimeUtc);
    }

    public static IReadOnlyList<ReportCandidate> OrderCandidatesForSelfTest(IEnumerable<ReportCandidate> candidates)
    {
        return OrderCandidatesByPublication(candidates);
    }

    private static ReportScanResult ScanCandidates(
        CandidateListResult candidatesResult,
        AppState state,
        bool includeCompleted)
    {
        if (!candidatesResult.Success)
        {
            return new ReportScanResult(
                FoundAnyReport: false,
                FoundNewReport: false,
                DuplicateSuppressed: false,
                Candidate: null,
                NewestCandidate: null,
                Message: candidatesResult.Message);
        }

        var candidates = candidatesResult.Candidates;
        if (candidates.Count == 0)
        {
            return new ReportScanResult(
                FoundAnyReport: false,
                FoundNewReport: false,
                DuplicateSuppressed: false,
                Candidate: null,
                NewestCandidate: null,
                Message: "No CGPT-REPORT-*.md files found in GitHub reports folder.");
        }

        var newest = candidates[0];
        if (includeCompleted)
        {
            return new ReportScanResult(
                FoundAnyReport: true,
                FoundNewReport: true,
                DuplicateSuppressed: false,
                Candidate: newest,
                NewestCandidate: newest,
                Message: $"Newest report selected for resend: {newest.RelativePath}");
        }

        var newestIdentity = ReportIdentity.FromCandidate(newest);
        if (string.IsNullOrWhiteSpace(state.HighestReportSortKeySeen) && candidates.Count > 1)
        {
            return new ReportScanResult(
                FoundAnyReport: true,
                FoundNewReport: false,
                DuplicateSuppressed: false,
                Candidate: null,
                NewestCandidate: newest,
                Message: "Existing reports found but no baseline exists. Click Baseline Existing Reports or Wake Newest Report Once.");
        }

        if (state.WasReportWoken(newest.FileName))
        {
            if (state.ShouldRetryWokenReport(newest, TimeSpan.FromMinutes(5), out var retryReason))
            {
                return new ReportScanResult(
                    FoundAnyReport: true,
                    FoundNewReport: true,
                    DuplicateSuppressed: false,
                    Candidate: newest,
                    NewestCandidate: newest,
                    Message: $"Newest report wake will be retried because prior capture did not consume it: {retryReason}");
            }

            if (!string.IsNullOrWhiteSpace(retryReason))
            {
                return new ReportScanResult(
                    FoundAnyReport: true,
                    FoundNewReport: false,
                    DuplicateSuppressed: true,
                    Candidate: null,
                    NewestCandidate: newest,
                    Message: $"Newest report already woken; {retryReason}");
            }

            return new ReportScanResult(
                FoundAnyReport: true,
                FoundNewReport: false,
                DuplicateSuppressed: true,
                Candidate: null,
                NewestCandidate: newest,
                Message: $"Newest report already woken: {newest.RelativePath}");
        }

        if (state.BusyReportsFirstSeenAtUtc.TryGetValue(newest.Fingerprint, out var busySince))
        {
            var retryAfter = busySince.AddSeconds(Math.Max(5, state.LastReportWakeCount == 0 ? 60 : 300));
            if (DateTimeOffset.UtcNow < retryAfter)
            {
                return new ReportScanResult(
                    FoundAnyReport: true,
                    FoundNewReport: false,
                    DuplicateSuppressed: true,
                    Candidate: null,
                    NewestCandidate: newest,
                    Message: $"Newest report wake is deferred because ChatGPT was busy. Retry after {retryAfter:O}");
            }
        }

        if (!string.IsNullOrWhiteSpace(state.HighestReportSortKeySeen) &&
            string.CompareOrdinal(newestIdentity.SortKey, state.HighestReportSortKeySeen) <= 0 &&
            (string.IsNullOrWhiteSpace(state.LastReportWokenSortKey) ||
             string.CompareOrdinal(newestIdentity.SortKey, state.LastReportWokenSortKey) <= 0))
        {
            state.MarkReportSuppressed(newest, $"sortKey {newestIdentity.SortKey} <= highWater {state.HighestReportSortKeySeen}");
            return new ReportScanResult(
                FoundAnyReport: true,
                FoundNewReport: false,
                DuplicateSuppressed: true,
                Candidate: null,
                NewestCandidate: newest,
                Message: $"Newest report suppressed as stale: {newest.RelativePath}");
        }

        if (IsOutOfSequenceWorkItem(newestIdentity, state.HighestReportWorkItemSeen))
        {
            state.MarkReportQuarantined(newest, $"work item {newestIdentity.WorkItemFamily}{newestIdentity.WorkItemNumber} is lower than high-water work item {state.HighestReportWorkItemSeen}");
            return new ReportScanResult(
                FoundAnyReport: true,
                FoundNewReport: false,
                DuplicateSuppressed: true,
                Candidate: null,
                NewestCandidate: newest,
                Message: $"Newest report quarantined as out-of-sequence: {newest.RelativePath}");
        }

        return new ReportScanResult(
            FoundAnyReport: true,
            FoundNewReport: true,
            DuplicateSuppressed: false,
            Candidate: newest,
            NewestCandidate: newest,
            Message: $"New newest report selected: {newest.RelativePath}");
    }

    private static async Task<ReportDownloadResult> DownloadReportAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateGitHubRequest(downloadUrl);
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ReportDownloadResult.Fail(
                    $"GitHub report download failed: {(int)response.StatusCode} {response.ReasonPhrase}. URL: {downloadUrl}");
            }

            return ReportDownloadResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ReportDownloadResult.Fail($"GitHub report download failed: {ex.Message}. URL: {downloadUrl}");
        }
    }

    private static async Task<CandidateListResult> GetCandidatesFromGitHubRemoteCacheAsync(
        AppConfig config,
        GitHubReportsLocation location,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = GetReportGitCachePath(config, location);
            var repoUrl = $"https://github.com/{location.Owner}/{location.Repo}.git";

            if (!Directory.Exists(cachePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var clone = await RunProcessCaptureAsync(
                    "git",
                    $"clone --bare {Quote(repoUrl)} {Quote(cachePath)}",
                    TimeSpan.FromSeconds(90),
                    cancellationToken);
                if (clone.ExitCode != 0)
                {
                    return CandidateListResult.Fail($"git clone failed for {repoUrl}: {clone.Error.Trim()}");
                }
            }

            var setUrl = await RunProcessCaptureAsync(
                "git",
                $"--git-dir {Quote(cachePath)} remote set-url origin {Quote(repoUrl)}",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (setUrl.ExitCode != 0)
            {
                return CandidateListResult.Fail($"git remote set-url failed for {repoUrl}: {setUrl.Error.Trim()}");
            }

            var remoteRef = $"refs/remotes/origin/{location.Branch}";
            var fetch = await RunProcessCaptureAsync(
                "git",
                $"--git-dir {Quote(cachePath)} fetch origin {Quote(location.Branch + ":" + remoteRef)}",
                TimeSpan.FromSeconds(45),
                cancellationToken);

            if (fetch.ExitCode != 0)
            {
                return CandidateListResult.Fail($"git fetch failed for {repoUrl} {location.Branch}: {fetch.Error.Trim()}");
            }

            var tree = await RunProcessCaptureAsync(
                "git",
                $"--git-dir {Quote(cachePath)} ls-tree -r {Quote(remoteRef)} -- {Quote(location.ReportsFolder)}",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (tree.ExitCode != 0)
            {
                return CandidateListResult.Fail($"git ls-tree failed for {location.RepoFullName}:{location.Branch}:{location.ReportsFolder}: {tree.Error.Trim()}");
            }

            var entries = tree.Output
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseGitTreeLine)
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .Where(entry => IsReportFileName(Path.GetFileName(entry.Path)))
                .ToList();

            var candidates = new List<ReportCandidate>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await RunProcessCaptureAsync(
                    "git",
                    $"--git-dir {Quote(cachePath)} show {Quote(remoteRef + ":" + entry.Path)}",
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
                if (content.ExitCode != 0)
                {
                    return CandidateListResult.Fail($"git show failed for {location.RepoFullName}:{location.Branch}:{entry.Path}: {content.Error.Trim()}");
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(content.Output);
                candidates.Add(BuildRemoteCandidate(
                    config,
                    entry.Path,
                    Path.GetFileName(entry.Path),
                    BuildReportBlobUrl(config, entry.Path),
                    $"git:{location.RepoFullName}:{location.Branch}:{entry.Path}",
                    bytes));
            }

            var ordered = OrderCandidatesByPublication(candidates);

            return CandidateListResult.Ok(ordered);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CandidateListResult.Fail($"GitHub remote cache scan failed: {ex.Message}");
        }
    }

    private static CandidateListResult GetCandidatesFromLocalFolder(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(config.LocalReportRoot))
        {
            return CandidateListResult.Fail("LocalReportRoot is empty; local report fallback is not configured.");
        }

        var reportsFolder = Path.Combine(
            config.LocalReportRoot,
            NormalizeRelativePath(config.ReportFolder).Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(reportsFolder))
        {
            return CandidateListResult.Fail($"Local report folder not found: {reportsFolder}");
        }

        var candidates = new List<ReportCandidate>();
        foreach (var path in Directory.EnumerateFiles(
                     reportsFolder,
                     $"{ReportPrefix}*{ReportSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(BuildLocalCandidate(config, path));
        }

        return CandidateListResult.Ok(OrderCandidatesByPublication(candidates));
    }

    private static async Task<CandidateListResult> GetCandidatesFromConfiguredGitRemoteAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gitRoot = string.IsNullOrWhiteSpace(config.ReportGitRoot)
            ? config.LocalRepoPath
            : config.ReportGitRoot.Trim();
        var remote = string.IsNullOrWhiteSpace(config.ReportRemote)
            ? "origin"
            : config.ReportRemote.Trim();
        var branch = config.ReportBranch.Trim();
        var folder = NormalizeRelativePath(config.ReportFolder);

        if (string.IsNullOrWhiteSpace(gitRoot))
        {
            return CandidateListResult.Fail("ReportGitRoot is required for GitRemote report polling.");
        }

        if (!Directory.Exists(gitRoot))
        {
            return CandidateListResult.Fail($"ReportGitRoot does not exist: {gitRoot}");
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            return CandidateListResult.Fail("ReportRemote is required for GitRemote report polling.");
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return CandidateListResult.Fail("ReportBranch is required for GitRemote report polling.");
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return CandidateListResult.Fail("ReportFolder is required for GitRemote report polling.");
        }

        var fetch = await RunProcessCaptureAsync(
            "git",
            $"-C {Quote(gitRoot)} fetch {Quote(remote)} {Quote(branch)}",
            TimeSpan.FromSeconds(90),
            cancellationToken);
        if (fetch.ExitCode != 0)
        {
            return CandidateListResult.Fail($"git fetch failed for {remote} {branch} in {gitRoot}: {fetch.Error.Trim()}");
        }

        var remoteRef = $"{remote}/{branch}";
        var tree = await RunProcessCaptureAsync(
            "git",
            $"-C {Quote(gitRoot)} ls-tree -r {Quote(remoteRef)} -- {Quote(folder)}",
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (tree.ExitCode != 0)
        {
            return CandidateListResult.Fail($"git ls-tree failed for {remoteRef}:{folder} in {gitRoot}: {tree.Error.Trim()}");
        }

        var reportEntries = tree.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseGitTreeLine)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .Where(entry => IsReportFileName(Path.GetFileName(entry.Path)))
            .ToList();

        var commitMetadataResult = await GetGitRemoteReportCommitMetadataAsync(
            gitRoot,
            remoteRef,
            folder,
            cancellationToken);
        if (!commitMetadataResult.Success)
        {
            return CandidateListResult.Fail(commitMetadataResult.Message);
        }

        var candidates = new List<ReportCandidate>();
        foreach (var entry in reportEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blob = await RunProcessCaptureBytesAsync(
                "git",
                $"-C {Quote(gitRoot)} cat-file blob {Quote(entry.Sha)}",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (blob.ExitCode != 0)
            {
                return CandidateListResult.Fail($"git cat-file failed for {remoteRef}:{entry.Path} blob={entry.Sha} in {gitRoot}: {blob.Error.Trim()}");
            }

            commitMetadataResult.Metadata.TryGetValue(entry.Path, out var metadata);
            candidates.Add(BuildRemoteCandidate(
                config,
                entry.Path,
                Path.GetFileName(entry.Path),
                BuildReportBlobUrl(config, entry.Path),
                $"git:{remoteRef}:{entry.Path}",
                blob.Bytes,
                metadata?.CommitTimeUtc,
                metadata?.Commit ?? string.Empty,
                entry.Sha));
        }

        var ordered = OrderCandidatesByPublication(candidates);

        return CandidateListResult.Ok(ordered);
    }

    private static async Task<GitReportCommitMetadataResult> GetGitRemoteReportCommitMetadataAsync(
        string gitRoot,
        string remoteRef,
        string folder,
        CancellationToken cancellationToken)
    {
        const string marker = "DCS_COMMIT_META=";
        var log = await RunProcessCaptureAsync(
            "git",
            $"-C {Quote(gitRoot)} log --format={Quote(marker + "%H|%cI")} --name-only {Quote(remoteRef)} -- {Quote(folder)}",
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (log.ExitCode != 0)
        {
            return GitReportCommitMetadataResult.Fail($"git log failed for {remoteRef}:{folder} in {gitRoot}: {log.Error.Trim()}");
        }

        var metadata = new Dictionary<string, GitReportCommitMetadata>(StringComparer.OrdinalIgnoreCase);
        string currentCommit = string.Empty;
        DateTime currentCommitTimeUtc = default;
        foreach (var rawLine in log.Output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                var parts = line[marker.Length..].Split('|', 2);
                currentCommit = parts.Length > 0 ? parts[0] : string.Empty;
                currentCommitTimeUtc = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var parsed)
                    ? parsed.UtcDateTime
                    : default;
                continue;
            }

            var path = NormalizeRelativePath(line);
            if (currentCommitTimeUtc != default &&
                IsReportFileName(Path.GetFileName(path)) &&
                !metadata.ContainsKey(path))
            {
                metadata[path] = new GitReportCommitMetadata(currentCommit, currentCommitTimeUtc);
            }
        }

        return GitReportCommitMetadataResult.Ok(metadata);
    }

    private static List<ReportCandidate> OrderCandidatesByPublication(IEnumerable<ReportCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenByDescending(candidate => candidate.SortTimestampUtc)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ReportCandidate BuildLocalCandidate(AppConfig config, string fullPath)
    {
        var info = new FileInfo(fullPath);
        var bytes = File.ReadAllBytes(fullPath);
        var root = Path.GetFullPath(config.LocalReportRoot);
        var relative = NormalizeRelativePath(Path.GetRelativePath(root, info.FullName));
        var folder = NormalizeRelativePath(config.ReportFolder);
        if (!relative.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
        {
            relative = $"{folder}/{info.Name}";
        }

        return BuildRemoteCandidate(
            config,
            relative,
            info.Name,
            BuildReportBlobUrl(config, relative),
            info.FullName,
            bytes,
            info.LastWriteTimeUtc) with
        {
            Repository = "local",
            Branch = "local",
            Commit = string.Empty,
            BlobIdentity = string.Empty
        };
    }

    private static ReportCandidate BuildRemoteCandidate(
        AppConfig config,
        string relativePath,
        string fileName,
        string? htmlUrl,
        string downloadUrl,
        byte[] contentBytes,
        DateTime? lastWriteTimeUtc = null,
        string commit = "",
        string blobIdentity = "")
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fingerprint = ComputeSha256(contentBytes);
        var blobUrl = string.IsNullOrWhiteSpace(htmlUrl)
            ? BuildReportBlobUrl(config, normalizedPath)
            : htmlUrl;
        var sortTimestamp = TryParseTimestampUtc(fileName);
        var lastWrite = DateTime.SpecifyKind(lastWriteTimeUtc ?? sortTimestamp ?? DateTime.MinValue, DateTimeKind.Utc);

        var decoded = DecodeReport(contentBytes);
        var workItem = WorkItemIdParser.Parse(fileName)?.ToString() ?? string.Empty;
        var sourceReport = ReadReportField(decoded, "source_report");
        var terminal = IsTerminalReportText(decoded);

        return new ReportCandidate(
            RelativePath: normalizedPath,
            FullPath: downloadUrl,
            FileName: fileName,
            Fingerprint: fingerprint,
            LastWriteTimeUtc: lastWrite,
            GitHubBlobUrl: blobUrl,
            SortTimestampUtc: sortTimestamp ?? lastWrite)
        {
            Repository = config.ReportRepoFullName,
            Branch = config.ReportBranch,
            Commit = commit,
            BlobIdentity = blobIdentity,
            ContentBytes = contentBytes,
            ReportTaskId = workItem,
            SourceReport = sourceReport,
            IsTerminal = terminal
        };
    }

    public static string ComputeSha256(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool IsTerminalReportText(string text) => Regex.IsMatch(
        text,
        @"(?im)^\s*(?:[-*+]\s*)?(?:\*\*|__)?(?:result|status)(?:\*\*|__)?\s*:\s*`{0,3}(?:PASS|FAIL_CLOSED|COMPLETE|COMPLETED|TERMINAL)\b");

    private static bool TryBuildGitHubReportsLocation(
        AppConfig config,
        out GitHubReportsLocation location,
        out string error)
    {
        location = new GitHubReportsLocation("", "", "", "");
        error = string.Empty;

        var repoFullName = config.ReportRepoFullName.Trim().Trim('/');
        var repoParts = repoFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2)
        {
            error = $"ReportRepoFullName must look like owner/repo. Current value: {config.ReportRepoFullName}";
            return false;
        }

        var branch = config.ReportBranch.Trim();
        if (string.IsNullOrWhiteSpace(branch))
        {
            error = "ReportBranch is required.";
            return false;
        }

        var folder = NormalizeRelativePath(config.ReportFolder);
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "ReportFolder is required.";
            return false;
        }

        location = new GitHubReportsLocation(
            Owner: repoParts[0],
            Repo: repoParts[1],
            Branch: branch,
            ReportsFolder: folder);
        return true;
    }

    private static string BuildContentsApiUrl(GitHubReportsLocation location)
    {
        return $"https://api.github.com/repos/{Uri.EscapeDataString(location.Owner)}/{Uri.EscapeDataString(location.Repo)}/contents/{EncodePath(location.ReportsFolder)}?ref={Uri.EscapeDataString(location.Branch)}";
    }

    private static string BuildReportBlobUrl(AppConfig config, string relativePath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.ReportGitHubBlobBase)
            ? config.GitHubBlobBase
            : config.ReportGitHubBlobBase;
        return $"{baseUrl.TrimEnd('/')}/{NormalizeRelativePath(relativePath)}";
    }

    private static HttpRequestMessage CreateGitHubRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DcsWatcherV2", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static GitTreeEntry? ParseGitTreeLine(string line)
    {
        var tabIndex = line.IndexOf('\t');
        if (tabIndex < 0)
        {
            return null;
        }

        var metadata = line[..tabIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length < 3)
        {
            return null;
        }

        return new GitTreeEntry(metadata[2], NormalizeRelativePath(line[(tabIndex + 1)..]));
    }

    private static async Task<ProcessCaptureResult> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
            var streams = await ReadTextStreamsAsync(outputTask, errorTask);
            return new ProcessCaptureResult(process.ExitCode, streams.Output, streams.Error);
        }
        catch (OperationCanceledException)
        {
            TerminateProcessTree(process);
            await WaitForTerminationAsync(process);
            var streams = await ReadTextStreamsAsync(outputTask, errorTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Report polling subprocess was cancelled and its process tree was terminated.",
                    cancellationToken);
            }

            return new ProcessCaptureResult(
                -1,
                streams.Output,
                $"Timed out after {timeout.TotalSeconds:N0}s. {streams.Error}".Trim());
        }
    }

    private static async Task<ProcessBytesResult> RunProcessCaptureBytesAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
            var error = await ReadByteStreamsAsync(copyTask, errorTask);
            return new ProcessBytesResult(process.ExitCode, output.ToArray(), error);
        }
        catch (OperationCanceledException)
        {
            TerminateProcessTree(process);
            await WaitForTerminationAsync(process);
            var error = await ReadByteStreamsAsync(copyTask, errorTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Report polling subprocess was cancelled and its process tree was terminated.",
                    cancellationToken);
            }

            return new ProcessBytesResult(
                -1,
                output.ToArray(),
                $"Timed out after {timeout.TotalSeconds:N0}s. {error}".Trim());
        }
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited during cleanup.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access or process state changed during cleanup.
        }
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Cleanup is bounded so the poll loop cannot hang indefinitely.
        }
    }

    private static async Task<(string Output, string Error)> ReadTextStreamsAsync(
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Cancellation can cancel the stream reads as well.
        }
        catch (TimeoutException)
        {
            // Stream cleanup must not hold the poll loop.
        }

        return (
            outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty,
            errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty);
    }

    private static async Task<string> ReadByteStreamsAsync(
        Task copyTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(copyTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Cancellation can cancel the stream reads as well.
        }
        catch (TimeoutException)
        {
            // Stream cleanup must not hold the poll loop.
        }

        return errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string ResolveReportPollMode(AppConfig config)
    {
        return (config.ReportPollMode ?? string.Empty).Trim() switch
        {
            GitRemotePollMode => GitRemotePollMode,
            GitHubPollMode => GitHubPollMode,
            LocalFolderPollMode => LocalFolderPollMode,
            GitHubThenLocalFallbackPollMode => GitHubThenLocalFallbackPollMode,
            _ => GitRemotePollMode
        };
    }

    private static string GetReportGitCachePath(AppConfig config, GitHubReportsLocation location)
    {
        var ledgerRoot = Path.IsPathRooted(config.LedgerRoot)
            ? config.LedgerRoot
            : Path.Combine(config.LocalRepoPath, config.LedgerRoot);
        var safeRepo = string.Join("_", location.RepoFullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(ledgerRoot, "report_git_cache", safeRepo + ".git");
    }

    private static bool IsOutOfSequenceWorkItem(ReportIdentity report, string highestWorkItemSeen)
    {
        if (string.IsNullOrWhiteSpace(report.WorkItemFamily) ||
            report.WorkItemNumber is null ||
            string.IsNullOrWhiteSpace(highestWorkItemSeen))
        {
            return false;
        }

        var highest = WorkItemIdParser.Parse(highestWorkItemSeen);
        return highest is not null &&
               highest.Family.Equals(report.WorkItemFamily, StringComparison.OrdinalIgnoreCase) &&
               report.WorkItemNumber.Value < highest.Number;
    }

    private static bool IsReportFileName(string fileName)
    {
        return fileName.StartsWith(ReportPrefix, StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(ReportSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string EncodePath(string path)
    {
        return string.Join("/", NormalizeRelativePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private static DateTime? TryParseTimestampUtc(string fileName)
    {
        var match = Regex.Match(fileName, @"CGPT-REPORT-(?<date>\d{8})-(?<time>\d{6})", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var timestamp = match.Groups["date"].Value + match.Groups["time"].Value;
        if (!DateTime.TryParseExact(
                timestamp,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static string DecodeReport(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private static string ReadReportField(string text, string fieldName)
    {
        var match = Regex.Match(text, $@"(?im)^\s*{Regex.Escape(fieldName)}\s*:\s*(?<value>[^\r\n]+)$");
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private sealed record GitHubReportsLocation(
        string Owner,
        string Repo,
        string Branch,
        string ReportsFolder)
    {
        public string RepoFullName => $"{Owner}/{Repo}";
    }

    private sealed record GitHubContentItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("download_url")] string DownloadUrl,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("sha")] string Sha);

    private sealed record GitTreeEntry(string Sha, string Path);

    private sealed record ProcessCaptureResult(int ExitCode, string Output, string Error);

    private sealed record ProcessBytesResult(int ExitCode, byte[] Bytes, string Error);

    private sealed record GitReportCommitMetadata(string Commit, DateTime CommitTimeUtc);

    private sealed record GitReportCommitMetadataResult(
        bool Success,
        IReadOnlyDictionary<string, GitReportCommitMetadata> Metadata,
        string Message)
    {
        public static GitReportCommitMetadataResult Ok(IReadOnlyDictionary<string, GitReportCommitMetadata> metadata)
        {
            return new GitReportCommitMetadataResult(true, metadata, "Git report commit metadata loaded.");
        }

        public static GitReportCommitMetadataResult Fail(string message)
        {
            return new GitReportCommitMetadataResult(false, new Dictionary<string, GitReportCommitMetadata>(), message);
        }
    }

    private sealed record ReportDownloadResult(bool Success, byte[]? Bytes, string Message)
    {
        public static ReportDownloadResult Ok(byte[] bytes)
        {
            return new ReportDownloadResult(true, bytes, "Downloaded.");
        }

        public static ReportDownloadResult Fail(string message)
        {
            return new ReportDownloadResult(false, null, message);
        }
    }
}

public sealed record CandidateListResult(bool Success, IReadOnlyList<ReportCandidate> Candidates, string Message)
{
    public static CandidateListResult Ok(IReadOnlyList<ReportCandidate> candidates)
    {
        return new CandidateListResult(true, candidates, $"{candidates.Count} GitHub report candidate(s) found.");
    }

    public static CandidateListResult Fail(string message)
    {
        return new CandidateListResult(false, Array.Empty<ReportCandidate>(), message);
    }
}
