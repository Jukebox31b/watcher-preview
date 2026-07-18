using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record CoreSafetyReleaseSelfTestResult(
    int Passed,
    int Failed,
    IReadOnlyList<string> Messages);

public static class CoreSafetyReleaseSelfTest
{
    public static async Task<CoreSafetyReleaseSelfTestResult> RunAsync()
    {
        var messages = new List<string>();
        var passed = 0;
        var failed = 0;

        async Task Run(string name, Func<Task> test)
        {
            try
            {
                await test();
                passed++;
                messages.Add($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"FAIL: {name}: {ex.Message}");
            }
        }

        await Run("Stop cancels an in-flight operation before post-stop work", TestStopCancellationAsync);
        await Run("Restart creates a fresh lifecycle token", TestFreshLifecycleTokenAsync);
        await Run("Stage 4 cannot fabricate human confirmation", TestAutomaticAuthorizationFailsClosedAsync);
        await Run("Default wake is notification-only and follow-on is explicit", TestWakePromptModesAsync);
        await Run("Git process success completes", TestGitSuccessAsync);
        await Run("Git nonzero exit completes", TestGitNonzeroAsync);
        await Run("Bounded process timeout completes and requests tree kill", TestProcessTimeoutAsync);
        await Run("Bounded process cancellation completes", TestProcessCancellationAsync);
        await Run("Timeout terminates a spawned child process", TestChildProcessTreeKillAsync);

        return new CoreSafetyReleaseSelfTestResult(passed, failed, messages);
    }

    private static async Task TestStopCancellationAsync()
    {
        using var lifecycle = new WatcherLifecycleController();
        var token = lifecycle.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postStopWorkRan = false;

        var operation = Task.Run(async () =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            postStopWorkRan = true;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.Stop();
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("The fake transaction completed without observing Stop cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected terminal state.
        }

        if (!token.IsCancellationRequested || postStopWorkRan)
        {
            throw new InvalidOperationException("Stop did not cancel before post-stop send/capture work.");
        }
    }

    private static Task TestFreshLifecycleTokenAsync()
    {
        using var lifecycle = new WatcherLifecycleController();
        var first = lifecycle.Start();
        lifecycle.Stop();
        var second = lifecycle.Start();
        if (!first.IsCancellationRequested || second.IsCancellationRequested || first == second)
        {
            throw new InvalidOperationException("Restart reused the cancelled lifecycle token.");
        }
        return Task.CompletedTask;
    }

    private static Task TestAutomaticAuthorizationFailsClosedAsync()
    {
        if (Stage4LimitedAutomaticService.HasVerifiedBoundedAuthorization(out var reason) ||
            !reason.Contains("signed bounded-autopilot grant", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Stage 4 claimed authorization without a verified bounded grant.");
        }
        return Task.CompletedTask;
    }

    private static Task TestWakePromptModesAsync()
    {
        var bytes = Encoding.UTF8.GetBytes("Result: PASS\nSynthetic report.\n");
        var report = new ReportCandidate(
            "fixtures/report.md",
            "fixture:report.md",
            "report.md",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            DateTime.UtcNow,
            "https://example.invalid/report",
            DateTime.UtcNow)
        {
            ContentBytes = bytes,
            Commit = new string('a', 40)
        };
        var builder = new ChatGptWakePromptBuilder();
        var notification = builder.Build(new AppConfig(), report, "wake-token");
        var followOn = builder.Build(new AppConfig(), report, "wake-token", requestFollowOnInstruction: true);
        if (notification.Contains("<<<DCS_CODEX_TASK_V1>>>", StringComparison.Ordinal) ||
            notification.Contains("issue the next", StringComparison.OrdinalIgnoreCase) ||
            !notification.Contains("Report notification only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Default wake still requests a follow-on instruction.");
        }
        if (!followOn.Contains("<<<DCS_CODEX_TASK_V1>>>", StringComparison.Ordinal) ||
            !followOn.Contains("explicitly requested one follow-on", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Explicit follow-on opt-in did not include the task-envelope request.");
        }
        return Task.CompletedTask;
    }

    private static async Task TestGitSuccessAsync()
    {
        var result = await BranchGuardService.RunProcessAsync(
            "git", ["--version"], Environment.CurrentDirectory, TimeSpan.FromSeconds(5));
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled ||
            !result.Output.Contains("git version", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected Git success; exit={result.ExitCode} error={result.Error.Trim()}");
        }
    }

    private static async Task TestGitNonzeroAsync()
    {
        var result = await BranchGuardService.RunProcessAsync(
            "git", ["--definitely-invalid-watcher-option"], Environment.CurrentDirectory, TimeSpan.FromSeconds(5));
        if (result.ExitCode == 0 || result.TimedOut || result.Cancelled)
        {
            throw new InvalidOperationException("Expected a bounded nonzero Git result.");
        }
    }

    private static async Task TestProcessTimeoutAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await BranchGuardService.RunProcessAsync(
            PowerShellPath(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory,
            TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();
        if (!result.TimedOut || result.Cancelled || !result.ProcessTreeKillRequested || stopwatch.Elapsed > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException($"Timeout was not bounded. elapsed={stopwatch.Elapsed} timedOut={result.TimedOut} kill={result.ProcessTreeKillRequested}");
        }
    }

    private static async Task TestProcessCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();
        var result = await BranchGuardService.RunProcessAsync(
            PowerShellPath(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20),
            cancellation.Token);
        stopwatch.Stop();
        if (!result.Cancelled || result.TimedOut || stopwatch.Elapsed > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException($"Cancellation was not bounded. elapsed={stopwatch.Elapsed} cancelled={result.Cancelled}");
        }
    }

    private static async Task TestChildProcessTreeKillAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Watcher-CoreSafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "child.pid");
        try
        {
            var escapedPath = pidPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "$child = Start-Process -FilePath cmd.exe -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -PassThru; " +
                         $"[IO.File]::WriteAllText('{escapedPath}', [string]$child.Id); Wait-Process -Id $child.Id";
            var result = await BranchGuardService.RunProcessAsync(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-Command", script],
                root,
                TimeSpan.FromSeconds(2));
            if (!result.TimedOut || !result.ProcessTreeKillRequested || !File.Exists(pidPath))
            {
                throw new InvalidOperationException("The child-process timeout fixture did not reach its expected state.");
            }

            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            await Task.Delay(250);
            if (IsProcessRunning(childPid))
            {
                throw new InvalidOperationException($"Child process {childPid} survived entire-process-tree termination.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Temporary offline fixture cleanup is best effort.
            }
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string PowerShellPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
