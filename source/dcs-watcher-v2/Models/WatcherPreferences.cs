using System.Text.Json.Serialization;

namespace DcsWatcherV2.Models;

public sealed class WatcherPreferences
{
    [JsonPropertyOrder(0)]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(1)]
    public string LastSelectedProfileId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public bool FirstRunCompleted { get; set; }

    [JsonPropertyOrder(3)]
    public int OverviewSplitterDistance { get; set; } = 320;

    [JsonPropertyOrder(4)]
    public string LastSelectedPage { get; set; } = "Overview";
}
