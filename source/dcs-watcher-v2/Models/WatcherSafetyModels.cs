namespace DcsWatcherV2.Models;

public enum WatcherOperatingStage
{
    Stage1DetectOnly = 1,
    Stage2SignedDryRun = 2,
    Stage3ManualPilotReady = 3,
    Stage3ManualPilot = 4,
    Stage3ManualPilotPassed = 5,
    Stage4LimitedAutomatic = 6,
    Stage5Production = 7
}

public enum AuthorizedInstructionDeliveryMode
{
    ManualEnvelopePaste,
    HashBoundFile
}

public sealed class ReportIngestionRecord
{
    public string Repository { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public string ReportTaskId { get; set; } = string.Empty;
    public string ActiveTaskId { get; set; } = string.Empty;
    public string SourceReport { get; set; } = string.Empty;
    public string ReportCommit { get; set; } = string.Empty;
    public string ReportBlobIdentity { get; set; } = string.Empty;
    public string ReportSha256 { get; set; } = string.Empty;
    public DateTimeOffset DiscoveryTimeUtc { get; set; }
    public DateTimeOffset VerificationTimeUtc { get; set; }
    public bool Duplicate { get; set; }
    public bool Eligible { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
}

public sealed class ActiveTaskLockRecord
{
    public bool IsActive { get; set; }
    public string ActiveTaskId { get; set; } = string.Empty;
    public string ActiveInstructionSha256 { get; set; } = string.Empty;
    public string SourceReport { get; set; } = string.Empty;
    public DateTimeOffset? DeliveryTimestampUtc { get; set; }
    public string DirectorThreadId { get; set; } = string.Empty;
    public string TerminalReportExpectedPath { get; set; } = string.Empty;
    public string TaskStatus { get; set; } = "inactive";
    public string CompletionReportCommit { get; set; } = string.Empty;
    public string CompletionReportSha256 { get; set; } = string.Empty;
}

public sealed class WakeTransactionRecord
{
    public string SchemaVersion { get; set; } = "watcher-wake-transaction-v1";
    public string TransactionId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string CurrentNodeBeforeWake { get; set; } = string.Empty;
    public List<string> VisibleBranchAncestry { get; set; } = [];
    public string VisibleParentMessageId { get; set; } = string.Empty;
    public string BrowserTabIdentity { get; set; } = string.Empty;
    public string WakeToken { get; set; } = string.Empty;
    public string IntendedSourceReport { get; set; } = string.Empty;
    public string IntendedActiveTask { get; set; } = string.Empty;
    public string WakeMessageId { get; set; } = string.Empty;
    public string WakeParentMessageId { get; set; } = string.Empty;
    public DateTimeOffset? WakeCreatedAtUtc { get; set; }
    public string Status { get; set; } = "prepared";
    public bool HumanConfirmed { get; set; }
}

public sealed class ConversationNodeRecord
{
    public string MessageId { get; set; } = string.Empty;
    public string ParentMessageId { get; set; } = string.Empty;
    public List<string> ChildMessageIds { get; set; } = [];
    public string Role { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Complete { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public bool IsVisuallyHidden { get; set; }
    public bool IsTemporalTurn { get; set; }
    public bool RebaseSystemMessage { get; set; }
    public bool RebaseDeveloperMessage { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string TurnExchangeId { get; set; } = string.Empty;
}

public sealed class ConversationLineageSnapshot
{
    public string ConversationId { get; set; } = string.Empty;
    public string CurrentNode { get; set; } = string.Empty;
    public string BrowserTabIdentity { get; set; } = string.Empty;
    public string BrowserFrameIdentity { get; set; } = string.Empty;
    public string BrowserUrlBeforeAcquisition { get; set; } = string.Empty;
    public string BrowserUrlAfterAcquisition { get; set; } = string.Empty;
    public string AuthenticatedAcquisitionMethod { get; set; } = string.Empty;
    public string AuthenticatedRequestMethod { get; set; } = string.Empty;
    public string AuthenticatedEndpointPath { get; set; } = string.Empty;
    public string AuthenticatedCredentialMode { get; set; } = string.Empty;
    public List<string> AuthenticatedHeaderNames { get; set; } = [];
    public int AuthenticationSessionStatusCode { get; set; }
    public string ResponseContentType { get; set; } = string.Empty;
    public bool ResponseBodyAvailable { get; set; }
    public bool ResponseMalformed { get; set; }
    public string RequestCacheMode { get; set; } = string.Empty;
    public bool CachedOnly { get; set; }
    public DateTimeOffset AcquisitionStartedAtUtc { get; set; }
    public DateTimeOffset AcquisitionCompletedAtUtc { get; set; }
    public DateTimeOffset AcquisitionDeadlineUtc { get; set; }
    public DateTimeOffset? BackendResponseTimestampUtc { get; set; }
    public DateTimeOffset SnapshotTimestampUtc { get; set; }
    public string DocumentVisibilityState { get; set; } = string.Empty;
    public bool ApiVerified { get; set; }
    public int ApiStatusCode { get; set; }
    public bool BrowserBackendAgree { get; set; }
    public List<string> BrowserVisibleMessageIds { get; set; } = [];
    public List<string> CurrentPathMessageIds { get; set; } = [];
    public List<string> VisibleActiveBranchMessageIds { get; set; } = [];
    public Dictionary<string, ConversationNodeRecord> Nodes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AssistantResponseObservation
{
    public string MessageId { get; set; } = string.Empty;
    public string ParentMessageId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Complete { get; set; }
    public bool? OnCurrentPath { get; set; }
    public string WakeToken { get; set; } = string.Empty;
    public string SourceReport { get; set; } = string.Empty;
    public string CaptureMethod { get; set; } = string.Empty;
    public bool FallbackBody { get; set; }
    public bool ApiVerified { get; set; }
    public int? SelectedAssistantIndex { get; set; }
    public bool AssistantSelectionAmbiguous { get; set; }
    public bool WholePageCaptureUsed { get; set; }
    public string CurrentNodeAtCapture { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAtUtc { get; set; }
}

public sealed record ChatGptLineageCaptureResult(
    bool Success,
    string Message,
    ConversationLineageSnapshot? Snapshot = null,
    AssistantResponseObservation? Response = null,
    string ReasonCode = "",
    long DurationMilliseconds = 0);

public sealed record SafetyValidationResult(
    bool Eligible,
    bool BranchDivergence,
    string Reason,
    string EnvelopeText = "",
    string EnvelopeTaskId = "",
    string EnvelopeSha256 = "");

public sealed class InstructionProvenanceRecord
{
    public string SchemaVersion { get; set; } = "watcher-instruction-provenance-v1";
    public string ConversationId { get; set; } = string.Empty;
    public string WakeToken { get; set; } = string.Empty;
    public string WakeMessageId { get; set; } = string.Empty;
    public string AssistantMessageId { get; set; } = string.Empty;
    public string AssistantParentMessageId { get; set; } = string.Empty;
    public string CurrentNodeBeforeWake { get; set; } = string.Empty;
    public string CurrentNodeAfterResponse { get; set; } = string.Empty;
    public string FullBranchLineageDigest { get; set; } = string.Empty;
    public bool? OnCurrentPath { get; set; }
    public string ResponseRole { get; set; } = string.Empty;
    public DateTimeOffset? ResponseCreationTimestampUtc { get; set; }
    public string CaptureMethod { get; set; } = string.Empty;
    public bool? FallbackBody { get; set; }
    public bool? ApiVerificationResult { get; set; }
    public string EnvelopeSha256 { get; set; } = string.Empty;
    public int? EnvelopeByteCount { get; set; }
    public string SourceReport { get; set; } = string.Empty;
    public string ActiveTask { get; set; } = string.Empty;
    public string DeliveryMode { get; set; } = string.Empty;
    public bool? HumanConfirmationState { get; set; }
    public string DeliveryDestination { get; set; } = string.Empty;
    public string IpcDeliveryId { get; set; } = string.Empty;
    public string RejectionReason { get; set; } = string.Empty;
}

public sealed class TransactionAuditState
{
    public string ConversationId { get; set; } = string.Empty;
    public string CurrentNode { get; set; } = string.Empty;
    public string WakeMessageId { get; set; } = string.Empty;
    public string ResponseMessageId { get; set; } = string.Empty;
    public string ResponseParentId { get; set; } = string.Empty;
    public bool? OnCurrentPath { get; set; }
    public string CaptureMethod { get; set; } = string.Empty;
    public bool? FallbackBody { get; set; }
    public bool? ApiVerification { get; set; }
    public string EnvelopeTaskId { get; set; } = string.Empty;
    public string EnvelopeSha256 { get; set; } = string.Empty;
    public string DeliveryMode { get; set; } = string.Empty;
    public bool HumanConfirmation { get; set; }
    public string ActiveTask { get; set; } = string.Empty;
    public string TerminalReportStatus { get; set; } = string.Empty;
    public string EligibilityResult { get; set; } = "Not evaluated";
    public string VisibleWarning { get; set; } = string.Empty;
}

public sealed class HashBoundInstructionAuthorization
{
    public string AbsolutePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long ExpectedSizeBytes { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string ExpectedTaskId { get; set; } = string.Empty;
    public string ReceivingDirectorThreadId { get; set; } = string.Empty;
    public string AuthorizedDeliveryMethod { get; set; } = "HashBoundFile";
    public DateTimeOffset AuthorizedAtUtc { get; set; }
    public DateTimeOffset? FileLastWriteTimeUtcAtAuthorization { get; set; }
}

public sealed record HashBoundFileValidationResult(
    bool Eligible,
    string Reason,
    string TaskId = "",
    string SourceReport = "",
    long ActualSizeBytes = 0,
    string ActualSha256 = "",
    string EnvelopeText = "");

public sealed class SupersessionRecord
{
    public string RevokedPath { get; set; } = string.Empty;
    public string RevokedSha256 { get; set; } = string.Empty;
    public string ReplacementPath { get; set; } = string.Empty;
    public string ReplacementSha256 { get; set; } = string.Empty;
    public DateTimeOffset SupersededAtUtc { get; set; }
}
