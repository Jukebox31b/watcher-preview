using System.Text.Json.Serialization;

namespace DcsWatcherV2.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2InstructionProvenanceV1
{
    public const string SchemaName = "DCS_WATCHER_INSTRUCTION_PROVENANCE_V1";
    public const string AlgorithmName = "ECDSA_P256_SHA256_RFC3279_DER";

    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaName;

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("wake_token")]
    public string WakeToken { get; set; } = string.Empty;

    [JsonPropertyOrder(5), JsonPropertyName("wake_message_id")]
    public string WakeMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("wake_message_created_at")]
    public string WakeMessageCreatedAt { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("assistant_message_id")]
    public string AssistantMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("assistant_message_created_at")]
    public string AssistantMessageCreatedAt { get; set; } = string.Empty;

    [JsonPropertyOrder(9), JsonPropertyName("assistant_parent_message_id")]
    public string AssistantParentMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(10), JsonPropertyName("expected_parent_message_id")]
    public string ExpectedParentMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(11), JsonPropertyName("current_node_before_wake")]
    public string CurrentNodeBeforeWake { get; set; } = string.Empty;

    [JsonPropertyOrder(12), JsonPropertyName("current_node_after_response")]
    public string CurrentNodeAfterResponse { get; set; } = string.Empty;

    [JsonPropertyOrder(13), JsonPropertyName("current_node_at_capture")]
    public string CurrentNodeAtCapture { get; set; } = string.Empty;

    [JsonPropertyOrder(14), JsonPropertyName("current_path_root")]
    public string CurrentPathRoot { get; set; } = string.Empty;

    [JsonPropertyOrder(15), JsonPropertyName("on_current_path")]
    public bool OnCurrentPath { get; set; }

    [JsonPropertyOrder(16), JsonPropertyName("ancestry_message_ids")]
    public List<string> AncestryMessageIds { get; set; } = [];

    [JsonPropertyOrder(17), JsonPropertyName("ancestry_digest_sha256")]
    public string AncestryDigestSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(18), JsonPropertyName("ancestry_verified")]
    public bool AncestryVerified { get; set; }

    [JsonPropertyOrder(19), JsonPropertyName("direct_parent_verified")]
    public bool DirectParentVerified { get; set; }

    [JsonPropertyOrder(20), JsonPropertyName("current_node_verified")]
    public bool CurrentNodeVerified { get; set; }

    [JsonPropertyOrder(21), JsonPropertyName("selected_assistant_index")]
    public int SelectedAssistantIndex { get; set; }

    [JsonPropertyOrder(22), JsonPropertyName("capture_method")]
    public string CaptureMethod { get; set; } = string.Empty;

    [JsonPropertyOrder(23), JsonPropertyName("fallback_body_used")]
    public bool FallbackBodyUsed { get; set; }

    [JsonPropertyOrder(24), JsonPropertyName("whole_page_capture_used")]
    public bool WholePageCaptureUsed { get; set; }

    [JsonPropertyOrder(25), JsonPropertyName("backend_message_object_verified")]
    public bool BackendMessageObjectVerified { get; set; }

    [JsonPropertyOrder(26), JsonPropertyName("backend_verification_timestamp")]
    public string BackendVerificationTimestamp { get; set; } = string.Empty;

    [JsonPropertyOrder(27), JsonPropertyName("envelope_schema")]
    public string EnvelopeSchema { get; set; } = "DCS_CODEX_TASK_V1";

    [JsonPropertyOrder(28), JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyOrder(29), JsonPropertyName("source_report")]
    public string SourceReport { get; set; } = string.Empty;

    [JsonPropertyOrder(30), JsonPropertyName("envelope_size_bytes")]
    public long EnvelopeSizeBytes { get; set; }

    [JsonPropertyOrder(31), JsonPropertyName("envelope_sha256")]
    public string EnvelopeSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(32), JsonPropertyName("response_message_content_sha256")]
    public string ResponseMessageContentSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(33), JsonPropertyName("destination_codex_thread_id")]
    public string DestinationCodexThreadId { get; set; } = string.Empty;

    [JsonPropertyOrder(34), JsonPropertyName("watcher_source_commit")]
    public string WatcherSourceCommit { get; set; } = string.Empty;

    [JsonPropertyOrder(35), JsonPropertyName("watcher_source_tree_sha256")]
    public string WatcherSourceTreeSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(36), JsonPropertyName("watcher_executable_sha256")]
    public string WatcherExecutableSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(37), JsonPropertyName("watcher_configuration_sha256")]
    public string WatcherConfigurationSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(38), JsonPropertyName("issue_time_utc")]
    public string IssueTimeUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(39), JsonPropertyName("expiry_time_utc")]
    public string ExpiryTimeUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(40), JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyOrder(41), JsonPropertyName("replay_ledger_key")]
    public string ReplayLedgerKey { get; set; } = string.Empty;

    [JsonPropertyOrder(42), JsonPropertyName("signer_key_id")]
    public string SignerKeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(43), JsonPropertyName("signature_or_mac_algorithm")]
    public string SignatureOrMacAlgorithm { get; set; } = AlgorithmName;

    [JsonPropertyOrder(44), JsonPropertyName("signature_or_mac")]
    public string SignatureOrMac { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2BoundInstructionTransactionV1
{
    public const string SchemaName = "DCS_WATCHER_SIGNED_INSTRUCTION_TRANSACTION_V1";
    public const string DryRunClassification = "watcher_signed_dry_run";

    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaName;

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("delivery_classification")]
    public string DeliveryClassification { get; set; } = DryRunClassification;

    [JsonPropertyOrder(3), JsonPropertyName("provenance")]
    public Stage2InstructionProvenanceV1 Provenance { get; set; } = new();

    [JsonPropertyOrder(4), JsonPropertyName("envelope_base64")]
    public string EnvelopeBase64 { get; set; } = string.Empty;

    [JsonPropertyOrder(5), JsonPropertyName("response_message_content_base64")]
    public string ResponseMessageContentBase64 { get; set; } = string.Empty;
}

public sealed class Stage2BuildIdentity
{
    public string SourceCommit { get; set; } = string.Empty;
    public string SourceTreeSha256 { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string ConfigurationSha256 { get; set; } = string.Empty;
}

public sealed record Stage2EnvelopeValidationResult(
    bool Valid,
    string ReasonCode,
    string Reason,
    string EnvelopeText = "",
    string TaskId = "",
    string SourceReport = "",
    byte[]? EnvelopeBytes = null);

public sealed record Stage2PipelineResult(
    bool Success,
    string ReasonCode,
    string Message,
    byte[]? PayloadBytes = null,
    Stage2InstructionProvenanceV1? Provenance = null,
    string TransactionRecordPath = "");

public sealed record CodexTestVerificationResult(
    bool Accepted,
    string Disposition,
    string ReasonCode,
    string Message,
    string TransactionId = "",
    string TaskId = "");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2ReplayLedgerDocument
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_REPLAY_LEDGER_V1";

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("records")]
    public List<Stage2ReplayLedgerRecord> Records { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2ReplayLedgerRecord
{
    [JsonPropertyOrder(0), JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyOrder(1), JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyOrder(2), JsonPropertyName("envelope_sha256")]
    public string EnvelopeSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("wake_message_id")]
    public string WakeMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("assistant_message_id")]
    public string AssistantMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(5), JsonPropertyName("destination_codex_thread_id")]
    public string DestinationCodexThreadId { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("first_seen_timestamp_utc")]
    public string FirstSeenTimestampUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("disposition")]
    public string Disposition { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("delivery_attempt_count")]
    public int DeliveryAttemptCount { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2PublicKeyRegistryV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_PUBLIC_KEY_REGISTRY_V1";

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("keys")]
    public List<Stage2PublicKeyRecord> Keys { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage2PublicKeyRecord
{
    [JsonPropertyOrder(0), JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(1), JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;

    [JsonPropertyOrder(2), JsonPropertyName("public_key_spki_base64")]
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("public_key_fingerprint_sha256")]
    public string PublicKeyFingerprintSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyOrder(5), JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("revoked_at_utc")]
    public string RevokedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("cng_key_name")]
    public string CngKeyName { get; set; } = string.Empty;
}

public sealed class ManualInstructionAuthorizationV1
{
    public string Schema { get; set; } = "manual_user_visible_file_authorization";
    public string AbsoluteFilePath { get; set; } = string.Empty;
    public long ExpectedSizeBytes { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string ReceivingCodexThreadId { get; set; } = string.Empty;
    public string DirectManuallyPastedAuthorizationText { get; set; } = string.Empty;
    public string AuthorizationTextSha256 { get; set; } = string.Empty;
    public DateTimeOffset ReceiptTimestampUtc { get; set; }
}

public sealed class Stage2TestCaseResult
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class Stage2CombinedTestReport
{
    public string SchemaVersion { get; set; } = "watcher-stage2-test-results-v1";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int Stage1Passed { get; set; }
    public int Stage1Failed { get; set; }
    public int Stage2Passed { get; set; }
    public int Stage2Failed { get; set; }
    public int TotalPassed { get; set; }
    public int TotalFailed { get; set; }
    public List<RegressionTestCaseResult> Stage1Tests { get; set; } = [];
    public List<Stage2TestCaseResult> Stage2Tests { get; set; } = [];
}
