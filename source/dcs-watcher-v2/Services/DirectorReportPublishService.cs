using System.Diagnostics;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record DirectorReportPublishResult(bool Attempted, bool Success, string Message)
{
    public static DirectorReportPublishResult Skipped(string message) => new(false, true, message);
    public static DirectorReportPublishResult Ok(string message) => new(true, true, message);
    public static DirectorReportPublishResult Fail(string message) => new(true, false, message);
}

public sealed class DirectorReportPublishService
{
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);

    public async Task<DirectorReportPublishResult> PublishPendingReportsAsync(AppConfig config)
    {
        if (!config.AutoPublishLocalReportCommits ||
            !config.ReportPollMode.Equals("GitRemote", StringComparison.OrdinalIgnoreCase))
        {
            return DirectorReportPublishResult.Skipped("Automatic local report publication is disabled.");
        }

        var gitRoot = config.ReportGitRoot.Trim();
        var remote = string.IsNullOrWhiteSpace(config.ReportRemote) ? "origin" : config.ReportRemote.Trim();
        var branch = config.ReportBranch.Trim();
        var folder = NormalizePath(config.ReportFolder);
        if (!Directory.Exists(gitRoot))
        {
            return DirectorReportPublishResult.Fail($"ReportGitRoot does not exist: {gitRoot}");
        }

        var remoteUrl = await RunGitAsync(gitRoot, $"remote get-url {Quote(remote)}", TimeSpan.FromSeconds(15));
        if (remoteUrl.ExitCode != 0)
        {
            return DirectorReportPublishResult.Fail($"Could not resolve report remote {remote}: {remoteUrl.Error.Trim()}");
        }

        if (!RemoteMatchesConfiguredRepo(remoteUrl.Output, config.ReportRepoFullName))
        {
            return DirectorReportPublishResult.Fail(
                $"Refusing report publication because remote URL does not match {config.ReportRepoFullName}: {remoteUrl.Output.Trim()}");
        }

        var fetch = await RunGitAsync(gitRoot, $"fetch {Quote(remote)} {Quote(branch)}", TimeSpan.FromSeconds(90));
        if (fetch.ExitCode != 0)
        {
            return DirectorReportPublishResult.Fail($"Report publication fetch failed: {fetch.Error.Trim()}");
        }

        var remoteRef = $"{remote}/{branch}";
        var counts = await RunGitAsync(gitRoot, $"rev-list --left-right --count {Quote(remoteRef + "...HEAD")}", TimeSpan.FromSeconds(15));
        if (counts.ExitCode != 0 || !TryParseAheadBehind(counts.Output, out var behind, out var ahead))
        {
            return DirectorReportPublishResult.Fail($"Could not determine report publication state: {counts.Error.Trim()} {counts.Output.Trim()}".Trim());
        }

        if (ahead == 0)
        {
            return DirectorReportPublishResult.Skipped("No local report commits are awaiting publication.");
        }

        var changed = await RunGitAsync(gitRoot, $"diff --name-only {Quote(remoteRef + "..HEAD")}", TimeSpan.FromSeconds(15));
        if (changed.ExitCode != 0)
        {
            return DirectorReportPublishResult.Fail($"Could not inspect unpublished report commits: {changed.Error.Trim()}");
        }

        var changedPaths = changed.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (!ShouldAutoPublish(behind, ahead, changedPaths, folder))
        {
            var reason = behind > 0
                ? $"local report repository is behind/diverged (behind={behind}, ahead={ahead})"
                : $"the {ahead} unpublished commit(s) contain no {folder}/CGPT-REPORT-*.md file";
            return DirectorReportPublishResult.Fail($"Refusing automatic report publication because {reason}.");
        }

        var push = await RunGitAsync(gitRoot, $"push {Quote(remote)} HEAD:{Quote(branch)}", TimeSpan.FromSeconds(90));
        if (push.ExitCode != 0)
        {
            return DirectorReportPublishResult.Fail($"Automatic report publication failed: {push.Error.Trim()}");
        }

        return DirectorReportPublishResult.Ok(
            $"Automatically published {ahead} local Director commit(s) to {remote}/{branch}. {OneLine(push.Output)} {OneLine(push.Error)}".Trim());
    }

    public static bool ShouldAutoPublish(
        int behind,
        int ahead,
        IEnumerable<string> changedPaths,
        string reportFolder)
    {
        var folder = NormalizePath(reportFolder) + "/";
        return behind == 0 &&
               ahead > 0 &&
               changedPaths.Select(NormalizePath).Any(path =>
                   path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(path).StartsWith("CGPT-REPORT-", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RemoteMatchesConfiguredRepo(string remoteUrl, string repoFullName)
    {
        var normalizedUrl = remoteUrl.Trim().Replace('\\', '/').TrimEnd('/');
        var normalizedRepo = repoFullName.Trim().Trim('/');
        return normalizedUrl.EndsWith(normalizedRepo, StringComparison.OrdinalIgnoreCase) ||
               normalizedUrl.EndsWith(normalizedRepo + ".git", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseAheadBehind(string output, out int behind, out int ahead)
    {
        behind = 0;
        ahead = 0;
        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               int.TryParse(parts[0], out behind) &&
               int.TryParse(parts[1], out ahead);
    }

    private static Task<DirectorReportProcessResult> RunGitAsync(string gitRoot, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C {Quote(gitRoot)} {arguments}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return RunProcessAsync(startInfo, timeout, ProcessCleanupTimeout);
    }

    private static async Task<DirectorReportProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        TimeSpan cleanupTimeout)
    {
        if (timeout <= TimeSpan.Zero || cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeouts must be positive.");
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            var terminated = TerminateProcessTree(process);
            await WaitForExitBoundedAsync(process, cleanupTimeout);
            var streams = await ReadStreamsBoundedAsync(outputTask, errorTask, cleanupTimeout);
            return new DirectorReportProcessResult(
                -1,
                streams.Output,
                $"git command timed out after {timeout.TotalSeconds:0} seconds.",
                TimedOut: true,
                ProcessTreeTerminated: terminated);
        }

        var completedStreams = await ReadStreamsBoundedAsync(outputTask, errorTask, cleanupTimeout);
        return new DirectorReportProcessResult(
            process.ExitCode,
            completedStreams.Output,
            completedStreams.Error,
            TimedOut: false,
            ProcessTreeTerminated: false);
    }

    private static bool TerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between inspection and termination.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or access was lost during cleanup.
        }

        return false;
    }

    private static async Task WaitForExitBoundedAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (InvalidOperationException)
        {
            // The process already reached its terminal state.
        }
        catch (TimeoutException)
        {
            // Post-kill completion must remain bounded.
        }
    }

    private static async Task<(string Output, string Error)> ReadStreamsBoundedAsync(
        Task<string> outputTask,
        Task<string> errorTask,
        TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // Descendants can retain pipe handles; only use streams that completed in time.
        }

        return (
            outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty,
            errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty);
    }

    internal static Task<DirectorReportProcessResult> RunForSelfTestAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        TimeSpan cleanupTimeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunProcessAsync(startInfo, timeout, cleanupTimeout);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string OneLine(string value) => string.Join(" ", value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)).Trim();
}

internal sealed record DirectorReportProcessResult(
    int ExitCode,
    string Output,
    string Error,
    bool TimedOut,
    bool ProcessTreeTerminated);
