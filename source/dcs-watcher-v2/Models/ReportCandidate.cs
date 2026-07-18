namespace DcsWatcherV2.Models;

public sealed record ReportCandidate(
    string RelativePath,
    string FullPath,
    string FileName,
    string Fingerprint,
    DateTime LastWriteTimeUtc,
    string GitHubBlobUrl,
    DateTime SortTimestampUtc)
{
    public string Repository { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string BlobIdentity { get; init; } = string.Empty;
    public byte[] ContentBytes { get; init; } = [];
    public string ReportTaskId { get; init; } = string.Empty;
    public string SourceReport { get; init; } = string.Empty;
    public bool IsTerminal { get; init; }
}
