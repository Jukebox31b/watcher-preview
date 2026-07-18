namespace DcsWatcherV2.Models;

public sealed class RegressionTestCaseResult
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class RegressionTestReport
{
    public string SchemaVersion { get; set; } = "watcher-regression-results-v1";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<RegressionTestCaseResult> Tests { get; set; } = [];
}
