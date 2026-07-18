using System.Text.Json;

namespace DcsWatcherV2.Services;

public sealed record WatcherReleaseSuiteResult(
    string Name,
    int Passed,
    int Failed,
    IReadOnlyList<string> Messages);

public sealed record WatcherReleaseTestResult(
    bool Passed,
    int Total,
    int PassedCount,
    int Failed,
    IReadOnlyList<WatcherReleaseSuiteResult> Suites)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public static class WatcherReleaseTestSuite
{
    public static async Task<WatcherReleaseTestResult> RunStartupBoundedAsync()
    {
        var suites = new List<WatcherReleaseSuiteResult>();
        suites.Add(Capture("watcher-safety", RunWatcherSafety));
        suites.Add(Capture("profile-foundation", () => From(ProfileFoundationSelfTest.Run())));
        suites.Add(await CaptureAsync("core-safety", async () => From(await CoreSafetyReleaseSelfTest.RunAsync())));
        suites.Add(Capture("safe-demo", () => From(AdapterDemoReleaseSelfTest.Run())));
        suites.Add(Capture("runtime-composition", () => From(RuntimeCompositionReleaseSelfTest.Run())));
        suites.Add(Capture("human-confirmation-ui", RunHumanConfirmationUi));
        suites.Add(Capture("ui-release", RunUi));
        return Aggregate(suites);
    }

    public static async Task<WatcherReleaseTestResult> RunFullAsync()
    {
        var startup = await RunStartupBoundedAsync();
        var suites = startup.Suites.ToList();
        suites.Add(await CaptureAsync("command-cancellation", async () => From(await CommandCancellationReleaseSelfTest.RunAsync())));
        suites.Add(Capture("installation-trust", RunInstallationTrust));
        return Aggregate(suites);
    }

    internal static WatcherReleaseTestResult AggregateForSelfTest(params WatcherReleaseSuiteResult[] suites) => Aggregate(suites);

    private static WatcherReleaseTestResult Aggregate(IEnumerable<WatcherReleaseSuiteResult> suites)
    {
        var materialized = suites.ToArray();
        var passed = materialized.Sum(item => item.Passed);
        var failed = materialized.Sum(item => item.Failed);
        return new WatcherReleaseTestResult(failed == 0, passed + failed, passed, failed, materialized);
    }

    private static WatcherReleaseSuiteResult Capture(string name, Func<WatcherReleaseSuiteResult> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            return new WatcherReleaseSuiteResult(name, 0, 1, [$"FAIL: {ex.GetType().Name}: {ex.Message}"]);
        }
    }

    private static async Task<WatcherReleaseSuiteResult> CaptureAsync(string name, Func<Task<WatcherReleaseSuiteResult>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception ex)
        {
            return new WatcherReleaseSuiteResult(name, 0, 1, [$"FAIL: {ex.GetType().Name}: {ex.Message}"]);
        }
    }

    private static WatcherReleaseSuiteResult RunWatcherSafety()
    {
        var messages = new WatcherSelfTestService().Run();
        var passed = messages.Count(message => message.Contains("PASS", StringComparison.OrdinalIgnoreCase));
        return new WatcherReleaseSuiteResult("watcher-safety", Math.Max(1, passed), 0, messages);
    }

    private static WatcherReleaseSuiteResult RunUi()
    {
        var result = UiReleaseSelfTest.Run();
        var passed = result.Messages.Count(message => message.Contains("PASS", StringComparison.OrdinalIgnoreCase));
        var failed = result.Messages.Count - passed;
        return new WatcherReleaseSuiteResult("ui-release", passed, failed, result.Messages);
    }

    private static WatcherReleaseSuiteResult RunHumanConfirmationUi()
    {
        var result = HumanConfirmationUiSelfTest.Run();
        return new WatcherReleaseSuiteResult("human-confirmation-ui", result.Passed, result.Failed, result.Messages);
    }

    private static WatcherReleaseSuiteResult RunInstallationTrust()
    {
        var messages = new List<string>();
        var passed = 0;
        var failed = 0;
        for (var testNumber = 1; testNumber <= 10; testNumber++)
        {
            try
            {
                InstallationTrustReleaseSelfTest.Run(testNumber);
                passed++;
                messages.Add($"PASS: installation trust test {testNumber}");
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"FAIL: installation trust test {testNumber}: {ex.Message}");
            }
        }
        return new WatcherReleaseSuiteResult("installation-trust", passed, failed, messages);
    }

    private static WatcherReleaseSuiteResult From(ProfileFoundationSelfTestResult result) =>
        new("profile-foundation", result.Passed, result.Failed, result.Messages);

    private static WatcherReleaseSuiteResult From(CoreSafetyReleaseSelfTestResult result) =>
        new("core-safety", result.Passed, result.Failed, result.Messages);

    private static WatcherReleaseSuiteResult From(AdapterDemoReleaseSelfTestResult result) =>
        new("safe-demo", result.Passed, result.Failed, result.Messages);

    private static WatcherReleaseSuiteResult From(RuntimeCompositionReleaseSelfTestResult result) =>
        new("runtime-composition", result.Passed, result.Failed, result.Messages);

    private static WatcherReleaseSuiteResult From(CommandCancellationReleaseSelfTestResult result) =>
        new("command-cancellation", result.Passed, result.Failed, result.Messages);
}
