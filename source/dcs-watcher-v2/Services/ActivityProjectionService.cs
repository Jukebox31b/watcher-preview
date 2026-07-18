using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class ActivityProjectionService
{
    public ActivityTimelineItem Project(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        return new ActivityTimelineItem
        {
            Timestamp = watchEvent.Timestamp,
            Stage = Classify(watchEvent.Source, watchEvent.Message),
            Title = watchEvent.Source,
            Detail = watchEvent.Message,
            Result = watchEvent.Level
        };
    }

    private static string Classify(string source, string message)
    {
        var text = source + " " + message;
        if (text.Contains("lineage", StringComparison.OrdinalIgnoreCase) || text.Contains("branch", StringComparison.OrdinalIgnoreCase)) return "Lineage";
        if (text.Contains("authoriz", StringComparison.OrdinalIgnoreCase) || text.Contains("policy", StringComparison.OrdinalIgnoreCase)) return "Authorization";
        if (text.Contains("codex", StringComparison.OrdinalIgnoreCase) || text.Contains("delivery", StringComparison.OrdinalIgnoreCase) || text.Contains("ipc", StringComparison.OrdinalIgnoreCase)) return "Destination";
        if (text.Contains("report", StringComparison.OrdinalIgnoreCase) || text.Contains("github", StringComparison.OrdinalIgnoreCase) || text.Contains("git", StringComparison.OrdinalIgnoreCase)) return "Source";
        return "System";
    }
}
