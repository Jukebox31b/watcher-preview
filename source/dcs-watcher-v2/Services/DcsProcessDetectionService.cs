using System.Diagnostics;

namespace DcsWatcherV2.Services;

public sealed record RunningProcessIdentity(int ProcessId, string ProcessName, string ExecutablePath);

public static class DcsProcessDetectionService
{
    private static readonly HashSet<string> ApprovedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DCS.exe",
        "DCS_server.exe"
    };

    public static int CountRunningDcsProcesses()
    {
        var processes = new List<RunningProcessIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes.Add(new RunningProcessIdentity(
                        process.Id,
                        process.ProcessName,
                        ReadExecutablePath(process)));
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the snapshot was being collected.
                }
            }
        }

        return CountApprovedDcsProcesses(processes, Environment.ProcessId, Environment.ProcessPath ?? string.Empty);
    }

    public static bool IsPilotPreflightClear(
        IEnumerable<RunningProcessIdentity> processes,
        int currentWatcherProcessId,
        string currentWatcherExecutablePath,
        out int dcsProcessCount)
    {
        dcsProcessCount = CountApprovedDcsProcesses(processes, currentWatcherProcessId, currentWatcherExecutablePath);
        return dcsProcessCount == 0;
    }

    public static int CountApprovedDcsProcesses(
        IEnumerable<RunningProcessIdentity> processes,
        int currentWatcherProcessId,
        string currentWatcherExecutablePath) => processes.Count(process =>
            IsApprovedDcsProcess(process, currentWatcherProcessId, currentWatcherExecutablePath));

    public static bool IsApprovedDcsProcess(
        RunningProcessIdentity process,
        int currentWatcherProcessId,
        string currentWatcherExecutablePath)
    {
        if (process.ProcessId == currentWatcherProcessId)
            return false;

        if (!string.IsNullOrWhiteSpace(currentWatcherExecutablePath) &&
            !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
            Path.GetFullPath(process.ExecutablePath).Equals(
                Path.GetFullPath(currentWatcherExecutablePath),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var processExecutableName = NormalizeExecutableName(process.ProcessName);
        if (processExecutableName.Equals("DcsWatcherV2.exe", StringComparison.OrdinalIgnoreCase) ||
            !ApprovedExecutableNames.Contains(processExecutableName))
            return false;

        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
            return true;

        var pathExecutableName = Path.GetFileName(process.ExecutablePath);
        return ApprovedExecutableNames.Contains(pathExecutableName) &&
               pathExecutableName.Equals(processExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutableName(string processName)
    {
        var name = Path.GetFileName(processName.Trim());
        return Path.HasExtension(name) ? name : name + ".exe";
    }

    private static string ReadExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }
}
