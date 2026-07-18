namespace DcsWatcherV2.Services;

public sealed class WatcherSelfTestService
{
    public IReadOnlyList<string> Run()
    {
        var report = new WatcherSafetyRegressionSuite().Run();
        if (report.Failed > 0)
        {
            var failures = report.Tests
                .Where(test => !test.Passed)
                .Select(test => $"#{test.Number} {test.Name}: {test.Details}");
            throw new InvalidOperationException(
                $"Watcher safety regression failed ({report.Failed}/{report.Tests.Count}): {string.Join(" | ", failures)}");
        }

        var messages = report.Tests
            .Select(test => $"Safety regression #{test.Number}: PASS - {test.Name}")
            .ToList();
        messages.Add($"Watcher safety regression summary: PASS ({report.Passed} passed, {report.Failed} failed).");
        var composition = RuntimeCompositionReleaseSelfTest.Run();
        messages.AddRange(composition.Messages.Select(message => "Runtime composition: " + message));
        if (composition.Failed > 0)
        {
            throw new InvalidOperationException(
                $"Runtime composition release checks failed ({composition.Failed}/{composition.Passed + composition.Failed}).");
        }
        messages.Add($"Runtime composition summary: PASS ({composition.Passed} passed, {composition.Failed} failed).");
        messages.Add("Operating boundary: Stage 1 detect-only; no wake, capture, IPC, or automatic delivery was exercised.");
        return messages;
    }
}
