namespace DcsWatcherV2.Models;

public sealed class ReportIdentity
{
    public string FileName { get; init; } = string.Empty;
    public string GitHubPath { get; init; } = string.Empty;
    public string GitHubUrl { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public DateTime? EmbeddedTimestamp { get; init; }
    public string WorkItemFamily { get; init; } = string.Empty;
    public int? WorkItemNumber { get; init; }
    public int WorkItemRevision { get; init; }
    public string WorkItemRevisionSuffix { get; init; } = string.Empty;
    public long WorkItemRevisionSuffixRank { get; init; }
    public string SortKey { get; init; } = string.Empty;

    public static ReportIdentity FromCandidate(ReportCandidate candidate)
    {
        var workItem = Services.WorkItemIdParser.Parse(candidate.FileName);
        var timestamp = candidate.LastWriteTimeUtc != DateTime.MinValue
            ? candidate.LastWriteTimeUtc
            : candidate.SortTimestampUtc == DateTime.MinValue
                ? (DateTime?)null
                : candidate.SortTimestampUtc;
        var timestampKey = timestamp?.ToUniversalTime().ToString("yyyyMMddHHmmss") ?? "00000000000000";
        var family = workItem?.Family ?? "ZZ";
        var number = workItem?.Number ?? 0;
        var revision = workItem?.Revision ?? 0;
        var revisionSuffix = workItem?.RevisionSuffix ?? string.Empty;
        var revisionSuffixRank = workItem?.RevisionSuffixRank ?? 0;

        return new ReportIdentity
        {
            FileName = candidate.FileName,
            GitHubPath = candidate.RelativePath,
            GitHubUrl = candidate.GitHubBlobUrl,
            CommitSha = candidate.Fingerprint,
            EmbeddedTimestamp = timestamp,
            WorkItemFamily = workItem?.Family ?? string.Empty,
            WorkItemNumber = workItem?.Number,
            WorkItemRevision = revision,
            WorkItemRevisionSuffix = revisionSuffix,
            WorkItemRevisionSuffixRank = revisionSuffixRank,
            SortKey = $"{timestampKey}|{family}|{number:D8}|R{revision:D4}|S{revisionSuffixRank:D19}|{revisionSuffix}|{candidate.FileName}"
        };
    }
}
