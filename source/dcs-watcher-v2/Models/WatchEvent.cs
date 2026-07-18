namespace DcsWatcherV2.Models;

public sealed class WatchEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Level { get; set; } = "INFO";
    public string Source { get; set; } = "Watcher";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = [];

    public override string ToString()
    {
        return $"{Timestamp:yyyy-MM-dd HH:mm:ss zzz} [{Level}] {Source}: {Message}";
    }
}
