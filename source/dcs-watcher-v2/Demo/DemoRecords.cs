using DcsWatcherV2.Models;

namespace DcsWatcherV2.Demo;

public static class DemoDispositions
{
    public const string AcceptedOnce = "ACCEPTED_ONCE";
    public const string RejectedReplay = "REJECTED_REPLAY";
    public const string RejectedBranchDivergence = "REJECTED_BRANCH_DIVERGENCE";
    public const string RejectedInvalid = "REJECTED_INVALID";
}

public sealed record DemoAdapterSelection(string ReportAdapterId, string DirectorAdapterId, string DeliveryAdapterId)
{
    public static DemoAdapterSelection IsolatedDefault { get; } = new(
        WatcherAdapterIds.ReportDemoFixture,
        WatcherAdapterIds.DirectorDemoFixture,
        WatcherAdapterIds.DeliveryTestSink);
}

public sealed record DemoFixture(
    string FixtureId,
    DateTimeOffset TimestampUtc,
    string ReportName,
    string ReportSha256,
    string ReportSummary,
    WakeTransactionRecord Wake,
    ConversationLineageSnapshot Snapshot,
    AssistantResponseObservation Response);

public sealed record DemoActivityRecord(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string FixtureId,
    string Stage,
    string Status,
    string Message);

public sealed record DemoEvidenceRecord(
    string FixtureId,
    DateTimeOffset TimestampUtc,
    string TransactionId,
    string ConversationId,
    string TaskId,
    string WakeMessageId,
    string AssistantMessageId,
    string AssistantParentMessageId,
    string CurrentNode,
    bool? OnCurrentPath,
    string EnvelopeSha256,
    string ProvenanceSha256,
    string SignerFingerprintSha256,
    string Destination,
    bool SignatureCreated,
    bool DeliveryAttempted,
    string Disposition,
    string ReasonCode,
    DateTimeOffset? FirstSeenUtc = null);

public sealed record DemoRunResult(
    bool Accepted,
    string Disposition,
    string ReasonCode,
    string Message,
    DemoEvidenceRecord Evidence,
    IReadOnlyList<DemoActivityRecord> Activity,
    int SinkAcceptedCount);

public sealed record DemoSignedTransaction(
    byte[] PayloadBytes,
    Stage2InstructionProvenanceV1 Provenance,
    Stage2PublicKeyRecord PublicKey);

public sealed record DemoSinkReceipt(
    bool Accepted,
    string Disposition,
    string ReasonCode,
    string TransactionId,
    string EnvelopeSha256,
    DateTimeOffset TimestampUtc,
    bool NonActionable);
