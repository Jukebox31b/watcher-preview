namespace DcsWatcherV2.Models;

public sealed class WorkItemId
{
    public string Family { get; init; } = string.Empty;
    public int Number { get; init; }
    public int Revision { get; init; }
    public string RevisionSuffix { get; init; } = string.Empty;
    public long RevisionSuffixRank { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string SortKey => $"{Family}:{Number:D8}:R{Revision:D4}:S{RevisionSuffixRank:D19}:{RevisionSuffix.ToUpperInvariant()}";

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Family)
            ? OriginalText
            : $"{Family}{Number}{(Revision > 0 ? $"R{Revision}{RevisionSuffix.ToUpperInvariant()}" : string.Empty)}";
    }
}
