using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

if (args.Length > 0 && args[0].Equals("ledger-worker", StringComparison.OrdinalIgnoreCase))
{
    return Stage3RegressionSuite.RunLedgerWorker(args.Skip(1).ToArray());
}
if (args.Length > 0 && args[0].Equals("monotonic-counter-worker", StringComparison.OrdinalIgnoreCase))
{
    return Stage3RegressionSuite.RunMonotonicCounterWorker(args.Skip(1).ToArray());
}

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : string.Empty;
var faultOutputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : string.Empty;
var intakeExecutablePath = args.Length > 2 ? Path.GetFullPath(args[2]) : string.Empty;
var skipFaultInjection = args.Length > 3 && args[3].Equals("--skip-fault", StringComparison.OrdinalIgnoreCase);
var selfExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Regression executable path is unavailable.");

var started = DateTimeOffset.UtcNow;
var stage1 = new WatcherSafetyRegressionSuite().Run();
var stage2 = new Stage2RegressionSuite().Run();
var suite = new Stage3RegressionSuite(selfExecutablePath, intakeExecutablePath);
var stage3 = suite.Run();
var report = new Stage3ReadinessTestReport
{
    StartedAtUtc = started,
    CompletedAtUtc = DateTimeOffset.UtcNow,
    Stage2Passed = stage1.Passed + stage2.Count(test => test.Passed),
    Stage2Failed = stage1.Failed + stage2.Count(test => !test.Passed),
    Stage3Passed = stage3.Count(test => test.Passed),
    Stage3Failed = stage3.Count(test => !test.Passed),
    Stage2Tests = [.. stage2],
    Stage3Tests = [.. stage3]
};
report.TotalPassed = report.Stage2Passed + report.Stage3Passed;
report.TotalFailed = report.Stage2Failed + report.Stage3Failed;

if (!string.IsNullOrWhiteSpace(outputPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

var fault = skipFaultInjection ? new Stage3FaultInjectionReport() : suite.RunFaultInjection();
if (!string.IsNullOrWhiteSpace(faultOutputPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(faultOutputPath)!);
    File.WriteAllText(faultOutputPath, JsonSerializer.Serialize(fault, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    report.TotalPassed,
    report.TotalFailed,
    fault.DuplicateAcceptances,
    fault.UnauthorizedDeliveries,
    fault.SilentRecoveries,
    fault.LiveOutputs
}));
return report.TotalFailed == 0 &&
       fault.DuplicateAcceptances == 0 &&
       fault.UnauthorizedDeliveries == 0 &&
       fault.SilentRecoveries == 0 &&
       fault.LiveOutputs == 0 ? 0 : 1;
