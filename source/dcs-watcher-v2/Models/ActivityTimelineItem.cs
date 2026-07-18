namespace DcsWatcherV2.Models;

public sealed class ActivityTimelineItem
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Stage { get; init; } = "System";
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Result { get; init; } = "INFO";
}
