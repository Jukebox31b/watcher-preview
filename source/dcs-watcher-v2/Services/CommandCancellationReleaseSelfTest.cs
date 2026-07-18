using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record CommandCancellationReleaseSelfTestResult(
    int Passed,
    int Failed,
    IReadOnlyList<string> Messages);

public static class CommandCancellationReleaseSelfTest
{
    public static async Task<CommandCancellationReleaseSelfTestResult> RunAsync()
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
                messages.Add("PASS: " + name);
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"FAIL: {name}: {ex.Message}");
            }
        }

        await Run("Stop cancels every in-flight command family", TestAllCommandFamiliesCancelAsync);
        await Run("Manual scan scope before Start is cancellable", TestManualCommandBeforeStartAsync);
        await Run("Explicit command after Stop receives a fresh scope", TestFreshScopeAfterStopAsync);
        await Run("Git subprocess success is bounded", TestGitSuccessAsync);
        await Run("Git subprocess nonzero exit is bounded", TestGitNonzeroAsync);
        await Run("Git subprocess timeout terminates the process tree", TestGitTimeoutAsync);
        await Run("Git subprocess cancellation terminates promptly", TestGitCancellationAsync);
        await Run("Child subprocess is killed with its parent tree", TestChildTreeKillAsync);
        await Run("Codex CLI cancellation kills its tree and unregisters", TestCodexCliCancellationCleanupAsync);
        await Run("Codex CLI prompt exception unregisters its process", TestCodexCliPromptExceptionCleanupAsync);
        await Run("Report publication post-kill output is bounded", TestReportPublishPostKillOutputAsync);
        await Run("Report polling observes cancellation", TestReportPollCancellationAsync);
        await Run("Absent confirmation is rejected", TestAbsentConfirmationAsync);
        await Run("Stale confirmation is rejected", TestStaleConfirmationAsync);
        await Run("Mismatched confirmation bindings are rejected", TestMismatchedConfirmationAsync);
        await Run("Valid confirmation is accepted once", TestValidConfirmationOneUseAsync);
        await Run("Replayed confirmation is rejected", TestConfirmationReplayAsync);

        return new CommandCancellationReleaseSelfTestResult(passed, failed, messages);
    }

    private static Task TestAllCommandFamiliesCancelAsync()
    {
        using var lifecycle = new WatcherLifecycleController();
        var families = new[]
        {
            "scan", "resend", "wake-newest", "baseline", "capture", "route-chatgpt",
            "route-codex", "git-pull", "report-poll"
        };
        var scopes = families.Select(_ => lifecycle.BeginCommand()).ToList();
        try
        {
            lifecycle.Stop();
            var uncancelled = scopes
                .Select((scope, index) => (scope, index))
                .Where(item => !item.scope.Token.IsCancellationRequested)
                .Select(item => families[item.index])
                .ToArray();
            if (uncancelled.Length != 0)
            {
                throw new InvalidOperationException("Uncancelled command families: " + string.Join(", ", uncancelled));
            }
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestManualCommandBeforeStartAsync()
    {
        using var lifecycle = new WatcherLifecycleController();
        using var command = lifecycle.BeginCommand();
        if (!command.Token.CanBeCanceled)
        {
            throw new InvalidOperationException("A manual command before Start received a non-cancellable token.");
        }

        lifecycle.Stop();
        if (!command.Token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Stop did not cancel the pre-Start manual command.");
        }

        return Task.CompletedTask;
    }

    private static Task TestFreshScopeAfterStopAsync()
    {
        using var lifecycle = new WatcherLifecycleController();
        using var first = lifecycle.BeginCommand();
        lifecycle.Stop();
        using var second = lifecycle.BeginCommand();
        if (!first.Token.IsCancellationRequested || second.Token.IsCancellationRequested || !second.Token.CanBeCanceled)
        {
            throw new InvalidOperationException("A fresh explicit command did not receive a new active scope after Stop.");
        }

        return Task.CompletedTask;
    }

    private static async Task TestGitSuccessAsync()
    {
        var result = await GitPullService.RunForSelfTestAsync(
            "git", ["--version"], TimeSpan.FromSeconds(5));
        if (result.ExitCode != 0 || result.TimedOut ||
            !result.Output.Contains("git version", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected Git success result: exit={result.ExitCode} error={result.Error}");
        }
    }

    private static async Task TestGitNonzeroAsync()
    {
        var result = await GitPullService.RunForSelfTestAsync(
            "git", ["--definitely-invalid-watcher-option"], TimeSpan.FromSeconds(5));
        if (result.ExitCode == 0 || result.TimedOut)
        {
            throw new InvalidOperationException("Invalid Git arguments did not return a bounded nonzero result.");
        }
    }

    private static async Task TestGitTimeoutAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await GitPullService.RunForSelfTestAsync(
            PowerShellPath(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();
        if (!result.TimedOut || !result.ProcessTreeTerminated || stopwatch.Elapsed > TimeSpan.FromSeconds(6))
        {
            throw new InvalidOperationException($"Timeout was not bounded. elapsed={stopwatch.Elapsed} kill={result.ProcessTreeTerminated}");
        }
    }

    private static async Task TestGitCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await GitPullService.RunForSelfTestAsync(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                TimeSpan.FromSeconds(20),
                cancellation.Token);
            throw new InvalidOperationException("Cancelled process returned normally.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(6))
            {
                throw new InvalidOperationException("Cancellation cleanup exceeded its bound.");
            }
        }
    }

    private static async Task TestChildTreeKillAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Watcher-Cancel-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "child.pid");
        try
        {
            var escaped = pidPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "$child = Start-Process -FilePath cmd.exe -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -PassThru; " +
                         $"[IO.File]::WriteAllText('{escaped}', [string]$child.Id); Wait-Process -Id $child.Id";
            var result = await GitPullService.RunForSelfTestAsync(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromSeconds(2));
            if (!result.TimedOut || !result.ProcessTreeTerminated || !File.Exists(pidPath))
            {
                throw new InvalidOperationException("Child process fixture did not reach the expected timeout state.");
            }

            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            await Task.Delay(250);
            if (IsProcessRunning(childPid))
            {
                throw new InvalidOperationException($"Child process {childPid} survived process-tree termination.");
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

    private static async Task TestCodexCliCancellationCleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Watcher-Codex-Cancel-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var promptPath = Path.Combine(root, "prompt.txt");
        var logPath = Path.Combine(root, "recovery.jsonl");
        var pidPath = Path.Combine(root, "child.pid");
        await File.WriteAllTextAsync(promptPath, "synthetic prompt");
        using var cancellation = new CancellationTokenSource();
        Task<bool>? operation = null;
        var baseline = CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var escaped = pidPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "[Console]::In.ReadToEnd() | Out-Null; " +
                         "$child = Start-Process -FilePath cmd.exe -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -PassThru; " +
                         $"[IO.File]::WriteAllText('{escaped}', [string]$child.Id); Wait-Process -Id $child.Id";
            operation = CodexDirectorBridge.RunCliRecoveryForSelfTestAsync(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-Command", script],
                promptPath,
                logPath,
                TimeSpan.FromSeconds(20),
                cancellation.Token);

            await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(5));
            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            cancellation.Cancel();
            try
            {
                _ = await operation;
                throw new InvalidOperationException("Cancelled Codex CLI fixture returned normally.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Expected.
            }

            stopwatch.Stop();
            await Task.Delay(250);
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(6) ||
                CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest != baseline ||
                IsProcessRunning(childPid))
            {
                throw new InvalidOperationException(
                    $"Codex cancellation cleanup failed. elapsed={stopwatch.Elapsed} active={CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest} childRunning={IsProcessRunning(childPid)}");
            }
        }
        finally
        {
            cancellation.Cancel();
            if (operation is not null)
            {
                try
                {
                    await operation.WaitAsync(TimeSpan.FromSeconds(6));
                }
                catch
                {
                    // The assertion path still requires bounded fixture cleanup.
                }
            }

            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task TestCodexCliPromptExceptionCleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Watcher-Codex-Exception-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var promptPath = Path.Combine(root, "prompt.txt");
        var logPath = Path.Combine(root, "recovery.jsonl");
        await File.WriteAllTextAsync(promptPath, "locked synthetic prompt");
        var baseline = CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var promptLock = new FileStream(
                promptPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                useAsync: true);
            var succeeded = await CodexDirectorBridge.RunCliRecoveryForSelfTestAsync(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                promptPath,
                logPath,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
            stopwatch.Stop();
            if (succeeded || stopwatch.Elapsed > TimeSpan.FromSeconds(6) ||
                CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest != baseline)
            {
                throw new InvalidOperationException(
                    $"Codex prompt exception cleanup failed. success={succeeded} elapsed={stopwatch.Elapsed} active={CodexDirectorBridge.ActiveCliRecoveryCountForSelfTest}");
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task TestReportPublishPostKillOutputAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var script = "$child = Start-Process -FilePath cmd.exe -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -PassThru; " +
                     "Write-Output 'fixture-ready'; Wait-Process -Id $child.Id";
        var result = await DirectorReportPublishService.RunForSelfTestAsync(
            PowerShellPath(),
            ["-NoProfile", "-NonInteractive", "-Command", script],
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1));
        stopwatch.Stop();
        if (!result.TimedOut || !result.ProcessTreeTerminated || stopwatch.Elapsed > TimeSpan.FromSeconds(4))
        {
            throw new InvalidOperationException(
                $"Report publication cleanup was not bounded. elapsed={stopwatch.Elapsed} timeout={result.TimedOut} kill={result.ProcessTreeTerminated}");
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Fixture did not create {path} within {timeout}.");
            }

            await Task.Delay(50);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary offline fixture cleanup is best effort.
        }
    }

    private static async Task TestReportPollCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var config = new AppConfig { ReportPollMode = "LocalFolder", LocalReportRoot = Path.GetTempPath() };
        try
        {
            _ = await new GitHubReportPoller().GetCandidatesAsync(config, cancellation.Token);
            throw new InvalidOperationException("Cancelled report polling returned normally.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected.
        }
    }

    private static Task TestAbsentConfirmationAsync()
    {
        var context = ConfirmationContext();
        if (Validate(null, context, reserve: false, out _))
        {
            throw new InvalidOperationException("Missing confirmation was accepted.");
        }
        return Task.CompletedTask;
    }

    private static Task TestStaleConfirmationAsync()
    {
        var context = ConfirmationContext();
        var stale = context.Confirmation with
        {
            IssuedAtUtc = context.Now.AddMinutes(-20),
            ExpiresAtUtc = context.Now.AddMinutes(-10)
        };
        if (Validate(stale, context, reserve: false, out _))
        {
            throw new InvalidOperationException("Expired confirmation was accepted.");
        }
        return Task.CompletedTask;
    }

    private static Task TestMismatchedConfirmationAsync()
    {
        var context = ConfirmationContext();
        var variants = new[]
        {
            context.Confirmation with { Action = "different-action" },
            context.Confirmation with { SourceReportFingerprint = new string('0', 64) },
            context.Confirmation with { SourceReportPath = "different/report.md" },
            context.Confirmation with { ProfileId = "different-profile" },
            context.Confirmation with { PromptSha256 = new string('0', 64) }
        };
        if (variants.Any(variant => Validate(variant, context, reserve: false, out _)))
        {
            throw new InvalidOperationException("A mismatched confirmation binding was accepted.");
        }
        return Task.CompletedTask;
    }

    private static Task TestValidConfirmationOneUseAsync()
    {
        var context = ConfirmationContext();
        if (!Validate(context.Confirmation, context, reserve: true, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        return Task.CompletedTask;
    }

    private static Task TestConfirmationReplayAsync()
    {
        var context = ConfirmationContext();
        if (!Validate(context.Confirmation, context, reserve: true, out var firstReason))
        {
            throw new InvalidOperationException(firstReason);
        }
        if (Validate(context.Confirmation, context, reserve: true, out _))
        {
            throw new InvalidOperationException("A reserved confirmation nonce was accepted twice.");
        }
        return Task.CompletedTask;
    }

    private static bool Validate(
        HumanConfirmationRecord? confirmation,
        ConfirmationTestContext context,
        bool reserve,
        out string reason)
    {
        return WatcherOrchestrator.ValidateAndReserveHumanConfirmation(
            confirmation,
            context.Action,
            context.Report,
            context.ProfileId,
            context.Prompt,
            context.Now,
            context.UsedNonces,
            context.Gate,
            reserve,
            out reason);
    }

    private static ConfirmationTestContext ConfirmationContext()
    {
        var now = DateTimeOffset.UtcNow;
        const string action = WatcherOrchestrator.WakeNewestReportAction;
        const string profileId = "profile-test";
        const string prompt = "Synthetic exact wake prompt.";
        var bytes = Encoding.UTF8.GetBytes("Result: PASS\nSynthetic report.\n");
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var report = new ReportCandidate(
            "reports/CGPT-REPORT-TEST.md",
            "fixture:CGPT-REPORT-TEST.md",
            "CGPT-REPORT-TEST.md",
            fingerprint,
            now.UtcDateTime,
            "https://example.invalid/report",
            now.UtcDateTime)
        {
            ContentBytes = bytes
        };
        var confirmation = HumanConfirmationRecord.Create(
            action,
            report,
            profileId,
            prompt,
            now,
            TimeSpan.FromMinutes(5));
        return new ConfirmationTestContext(
            now,
            action,
            profileId,
            prompt,
            report,
            confirmation,
            new HashSet<string>(StringComparer.Ordinal),
            new object());
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

    private static string PowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private sealed record ConfirmationTestContext(
        DateTimeOffset Now,
        string Action,
        string ProfileId,
        string Prompt,
        ReportCandidate Report,
        HumanConfirmationRecord Confirmation,
        ISet<string> UsedNonces,
        object Gate);
}
