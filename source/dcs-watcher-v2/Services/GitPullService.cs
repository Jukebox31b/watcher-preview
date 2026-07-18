using System.Diagnostics;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record GitPullResult(
    bool Success,
    int ExitCode,
    string Output,
    string Error,
    string Message);

internal sealed record GitPullProcessResult(
    int ExitCode,
    string Output,
    string Error,
    bool TimedOut,
    bool ProcessTreeTerminated);

public sealed class GitPullService
{
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(30);

    public async Task<GitPullResult> PullFastForwardOnlyAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = CreateStartInfo("git");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(config.LocalRepoPath);
        startInfo.ArgumentList.Add("pull");
        startInfo.ArgumentList.Add("--ff-only");
        startInfo.ArgumentList.Add("origin");
        startInfo.ArgumentList.Add(config.AllowedBranch);

        var result = await RunProcessAsync(startInfo, PullTimeout, cancellationToken);
        var success = !result.TimedOut && result.ExitCode == 0;
        var summary = result.TimedOut
            ? "git pull timed out after 30 seconds; the process tree was terminated."
            : success
                ? "git pull --ff-only succeeded."
                : $"git pull --ff-only failed with exit code {result.ExitCode}.";

        return new GitPullResult(
            success,
            result.ExitCode,
            result.Output.Trim(),
            result.Error.Trim(),
            summary);
    }

    internal static async Task<GitPullProcessResult> RunForSelfTestAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunProcessAsync(startInfo, timeout, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName) => new(fileName)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static async Task<GitPullProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var terminated = false;
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
            var streams = await ReadStreamsAsync(outputTask, errorTask);
            return new GitPullProcessResult(process.ExitCode, streams.Output, streams.Error, false, false);
        }
        catch (OperationCanceledException)
        {
            terminated = TerminateProcessTree(process);
            await WaitForTerminationAsync(process);
            var streams = await ReadStreamsAsync(outputTask, errorTask);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Process execution was cancelled and its process tree was terminated.",
                    cancellationToken);
            }

            return new GitPullProcessResult(
                -1,
                streams.Output,
                string.IsNullOrWhiteSpace(streams.Error)
                    ? $"Timed out after {timeout.TotalSeconds:N1}s."
                    : streams.Error,
                true,
                terminated);
        }
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
            // The process exited between the state check and termination.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or access was lost during cleanup.
        }

        return false;
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Do not let cleanup hold the polling loop indefinitely.
        }
    }

    private static async Task<(string Output, string Error)> ReadStreamsAsync(
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Cancellation may also cancel stream reads; return what completed.
        }
        catch (TimeoutException)
        {
            // Stream cleanup is bounded independently from process cleanup.
        }

        var output = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
        var error = errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
        return (output, error);
    }
}
