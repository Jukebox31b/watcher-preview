using System.Diagnostics;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record BranchGuardResult(
    string Branch,
    bool IsAllowed,
    bool IsMain,
    bool IsBlocked,
    string Message);

internal sealed record BoundedProcessResult(
    int ExitCode,
    string Output,
    string Error,
    bool TimedOut,
    bool Cancelled,
    int ProcessId,
    bool ProcessTreeKillRequested);

public sealed class BranchGuardService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    public BranchGuardResult Check(AppConfig config) =>
        CheckAsync(config).GetAwaiter().GetResult();

    public async Task<BranchGuardResult> CheckAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(config.LocalRepoPath))
        {
            return new BranchGuardResult(
                "(missing repo)",
                IsAllowed: false,
                IsMain: false,
                IsBlocked: config.BranchLockEnabled,
                Message: $"Local repo path does not exist: {config.LocalRepoPath}");
        }

        var result = await RunProcessAsync(
            "git",
            ["branch", "--show-current"],
            config.LocalRepoPath,
            GitTimeout,
            cancellationToken);

        if (result.Cancelled)
        {
            return new BranchGuardResult(
                "(cancelled)",
                IsAllowed: false,
                IsMain: false,
                IsBlocked: true,
                Message: "Branch inspection cancelled; Git process tree was terminated.");
        }

        if (result.TimedOut)
        {
            return new BranchGuardResult(
                "(git timeout)",
                IsAllowed: false,
                IsMain: false,
                IsBlocked: true,
                Message: $"Unable to inspect current branch: git timed out after {GitTimeout.TotalSeconds:N0} seconds and was terminated.");
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error)
                ? $"git exited with code {result.ExitCode}."
                : result.Error.Trim();
            return new BranchGuardResult(
                "(git error)",
                IsAllowed: false,
                IsMain: false,
                IsBlocked: config.BranchLockEnabled,
                Message: $"Unable to inspect current branch: {detail}");
        }

        var branch = string.IsNullOrWhiteSpace(result.Output) ? "(detached HEAD)" : result.Output.Trim();
        var isMain = branch.Equals("main", StringComparison.OrdinalIgnoreCase);
        var isAllowed = branch.Equals(config.AllowedBranch, StringComparison.OrdinalIgnoreCase);
        var isBlocked = config.BranchLockEnabled && !isAllowed;

        if (isMain)
        {
            return new BranchGuardResult(
                branch,
                isAllowed,
                IsMain: true,
                IsBlocked: true,
                Message: "Hard block: current branch is main.");
        }

        if (isBlocked)
        {
            return new BranchGuardResult(
                branch,
                isAllowed,
                IsMain: false,
                IsBlocked: true,
                Message: $"Branch lock requires {config.AllowedBranch}; current branch is {branch}.");
        }

        return new BranchGuardResult(
            branch,
            isAllowed,
            IsMain: false,
            IsBlocked: false,
            Message: $"Branch OK: {branch}");
    }

    internal static async Task<BoundedProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var processId = process.Id;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var cancelled = false;
        var timedOut = false;
        var killRequested = false;
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled && timeoutSource.IsCancellationRequested;
            if (!process.HasExited)
            {
                killRequested = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the state check and Kill.
                }
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // The bounded result still reports the requested tree termination.
            }
        }

        var output = await outputTask.WaitAsync(TimeSpan.FromSeconds(2));
        var error = await errorTask.WaitAsync(TimeSpan.FromSeconds(2));
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new BoundedProcessResult(exitCode, output, error, timedOut, cancelled, processId, killRequested);
    }
}
