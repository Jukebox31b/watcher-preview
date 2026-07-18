using System.Text.Json.Serialization;

namespace DcsWatcherV2.Models;

public static class Stage3TransactionStates
{
    public const string Reserved = "RESERVED";
    public const string Validated = "VALIDATED";
    public const string Signed = "SIGNED";
    public const string Serialized = "SERIALIZED";
    public const string TestSinkSent = "TEST_SINK_SENT";
    public const string TestSinkAccepted = "TEST_SINK_ACCEPTED";
    public const string LiveDeliveryPending = "LIVE_DELIVERY_PENDING";
    public const string LiveDelivered = "LIVE_DELIVERED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";
    public const string RecoveryRequired = "RECOVERY_REQUIRED";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3ReplayLedgerV2
{
    public const string SchemaName = "DCS_WATCHER_REPLAY_LEDGER_V2";

    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaName;

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyOrder(2), JsonPropertyName("ledger_role")]
    public string LedgerRole { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("ledger_instance_id")]
    public string LedgerInstanceId { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("ledger_generation")]
    public long LedgerGeneration { get; set; }

    [JsonPropertyOrder(5), JsonPropertyName("entries")]
    public List<Stage3ReplayLedgerEntryV2> Entries { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3ReplayLedgerEntryV2
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_REPLAY_LEDGER_ENTRY_V2";

    [JsonPropertyOrder(1), JsonPropertyName("ledger_version")]
    public int LedgerVersion { get; set; } = 2;

    [JsonPropertyOrder(2), JsonPropertyName("ledger_instance_id")]
    public string LedgerInstanceId { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("ledger_generation")]
    public long LedgerGeneration { get; set; }

    [JsonPropertyOrder(4), JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyOrder(5), JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("envelope_sha256")]
    public string EnvelopeSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("wake_message_id")]
    public string WakeMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(9), JsonPropertyName("assistant_message_id")]
    public string AssistantMessageId { get; set; } = string.Empty;

    [JsonPropertyOrder(10), JsonPropertyName("destination_codex_thread_id")]
    public string DestinationCodexThreadId { get; set; } = string.Empty;

    [JsonPropertyOrder(11), JsonPropertyName("first_seen_utc")]
    public string FirstSeenUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(12), JsonPropertyName("last_transition_utc")]
    public string LastTransitionUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(13), JsonPropertyName("disposition")]
    public string Disposition { get; set; } = Stage3TransactionStates.Reserved;

    [JsonPropertyOrder(14), JsonPropertyName("delivery_attempt_count")]
    public int DeliveryAttemptCount { get; set; }

    [JsonPropertyOrder(15), JsonPropertyName("previous_entry_digest")]
    public string PreviousEntryDigest { get; set; } = new('0', 64);

    [JsonPropertyOrder(16), JsonPropertyName("entry_digest")]
    public string EntryDigest { get; set; } = string.Empty;

    [JsonPropertyOrder(17), JsonPropertyName("checkpoint_signer_key_id")]
    public string CheckpointSignerKeyId { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3LedgerCheckpointV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_LEDGER_CHECKPOINT_V1";

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("ledger_role")]
    public string LedgerRole { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("ledger_instance_id")]
    public string LedgerInstanceId { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("ledger_generation")]
    public long LedgerGeneration { get; set; }

    [JsonPropertyOrder(5), JsonPropertyName("entry_count")]
    public int EntryCount { get; set; }

    [JsonPropertyOrder(6), JsonPropertyName("head_entry_digest")]
    public string HeadEntryDigest { get; set; } = new('0', 64);

    [JsonPropertyOrder(7), JsonPropertyName("ledger_sha256")]
    public string LedgerSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("issued_at_utc")]
    public string IssuedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(9), JsonPropertyName("signer_key_id")]
    public string SignerKeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(10), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;

    [JsonPropertyOrder(11), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3MonotonicAnchorV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_MONOTONIC_ANCHOR_V1";

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("anchor_purpose")]
    public string AnchorPurpose { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("object_instance_id")]
    public string ObjectInstanceId { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("maximum_generation")]
    public long MaximumGeneration { get; set; }

    [JsonPropertyOrder(5), JsonPropertyName("object_digest")]
    public string ObjectDigest { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("issued_at_utc")]
    public string IssuedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("signer_key_id")]
    public string SignerKeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;

    [JsonPropertyOrder(9), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

public sealed class Stage3LedgerIdentity
{
    public string LedgerRole { get; set; } = string.Empty;
    public string LedgerInstanceId { get; set; } = string.Empty;
    public string MutexName { get; set; } = string.Empty;
    public string LedgerDirectory { get; set; } = string.Empty;
    public string AnchorDirectory { get; set; } = string.Empty;
}

public sealed record Stage3LedgerResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    long Generation = 0,
    long Sequence = 0,
    string Disposition = "",
    bool AbandonedMutexRecovered = false);

public sealed class Stage3LockOwnerMetadata
{
    public string Schema { get; set; } = "DCS_WATCHER_LOCK_OWNER_V1";
    public string MutexName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string ProcessStartTimeUtc { get; set; } = string.Empty;
    public string AcquiredAtUtc { get; set; } = string.Empty;
    public bool AbandonedMutexRecovery { get; set; }
}

public sealed class Stage3LedgerFaultOptions
{
    public string StopAfterStep { get; set; } = string.Empty;
    public bool LeaveTemporaryFile { get; set; }
    public bool TerminateProcess { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3BuildAttestationV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_BUILD_ATTESTATION_V1";
    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyOrder(2), JsonPropertyName("build_generation")]
    public long BuildGeneration { get; set; }
    [JsonPropertyOrder(3), JsonPropertyName("repository_path")]
    public string RepositoryPath { get; set; } = string.Empty;
    [JsonPropertyOrder(4), JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;
    [JsonPropertyOrder(5), JsonPropertyName("source_commit")]
    public string SourceCommit { get; set; } = string.Empty;
    [JsonPropertyOrder(6), JsonPropertyName("source_tree_sha256")]
    public string SourceTreeSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(7), JsonPropertyName("source_status")]
    public string SourceStatus { get; set; } = string.Empty;
    [JsonPropertyOrder(8), JsonPropertyName("tracked_manifest_sha256")]
    public string TrackedManifestSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(9), JsonPropertyName("untracked_file_disposition")]
    public string UntrackedFileDisposition { get; set; } = string.Empty;
    [JsonPropertyOrder(10), JsonPropertyName("build_timestamp_utc")]
    public string BuildTimestampUtc { get; set; } = string.Empty;
    [JsonPropertyOrder(11), JsonPropertyName("windows_version")]
    public string WindowsVersion { get; set; } = string.Empty;
    [JsonPropertyOrder(12), JsonPropertyName("dotnet_sdk_version")]
    public string DotnetSdkVersion { get; set; } = string.Empty;
    [JsonPropertyOrder(13), JsonPropertyName("dotnet_runtime_version")]
    public string DotnetRuntimeVersion { get; set; } = string.Empty;
    [JsonPropertyOrder(14), JsonPropertyName("compiler_identity")]
    public string CompilerIdentity { get; set; } = string.Empty;
    [JsonPropertyOrder(15), JsonPropertyName("build_configuration")]
    public string BuildConfiguration { get; set; } = string.Empty;
    [JsonPropertyOrder(16), JsonPropertyName("target_runtime")]
    public string TargetRuntime { get; set; } = string.Empty;
    [JsonPropertyOrder(17), JsonPropertyName("publish_mode")]
    public string PublishMode { get; set; } = string.Empty;
    [JsonPropertyOrder(18), JsonPropertyName("exact_build_command")]
    public string ExactBuildCommand { get; set; } = string.Empty;
    [JsonPropertyOrder(19), JsonPropertyName("exact_test_command")]
    public string ExactTestCommand { get; set; } = string.Empty;
    [JsonPropertyOrder(20), JsonPropertyName("warnings_count")]
    public int WarningsCount { get; set; }
    [JsonPropertyOrder(21), JsonPropertyName("errors_count")]
    public int ErrorsCount { get; set; }
    [JsonPropertyOrder(22), JsonPropertyName("test_count")]
    public int TestCount { get; set; }
    [JsonPropertyOrder(23), JsonPropertyName("executable_path")]
    public string ExecutablePath { get; set; } = string.Empty;
    [JsonPropertyOrder(24), JsonPropertyName("executable_sha256")]
    public string ExecutableSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(25), JsonPropertyName("application_dll_path")]
    public string ApplicationDllPath { get; set; } = string.Empty;
    [JsonPropertyOrder(26), JsonPropertyName("application_dll_sha256")]
    public string ApplicationDllSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(27), JsonPropertyName("supporting_dlls")]
    public List<Stage3FileHashRecord> SupportingDlls { get; set; } = [];
    [JsonPropertyOrder(28), JsonPropertyName("configuration_template_sha256")]
    public string ConfigurationTemplateSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(29), JsonPropertyName("provenance_schema_sha256")]
    public string ProvenanceSchemaSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(30), JsonPropertyName("verifier_contract_sha256")]
    public string VerifierContractSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(31), JsonPropertyName("replay_ledger_contract_sha256")]
    public string ReplayLedgerContractSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(32), JsonPropertyName("provenance_signer_public_key_fingerprint")]
    public string ProvenanceSignerPublicKeyFingerprint { get; set; } = string.Empty;
    [JsonPropertyOrder(33), JsonPropertyName("attestation_signer_key_id")]
    public string AttestationSignerKeyId { get; set; } = string.Empty;
    [JsonPropertyOrder(34), JsonPropertyName("intake_executable_path")]
    public string IntakeExecutablePath { get; set; } = string.Empty;
    [JsonPropertyOrder(35), JsonPropertyName("intake_executable_sha256")]
    public string IntakeExecutableSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(36), JsonPropertyName("intake_application_dll_path")]
    public string IntakeApplicationDllPath { get; set; } = string.Empty;
    [JsonPropertyOrder(37), JsonPropertyName("intake_application_dll_sha256")]
    public string IntakeApplicationDllSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(38), JsonPropertyName("intake_policy_sha256")]
    public string IntakePolicySha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(39), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    [JsonPropertyOrder(40), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

public sealed class Stage3FileHashRecord
{
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3TrustStoreV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_CODEX_TRUST_STORE_V1";
    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyOrder(2), JsonPropertyName("trust_store_instance_id")]
    public string TrustStoreInstanceId { get; set; } = string.Empty;
    [JsonPropertyOrder(3), JsonPropertyName("trust_generation")]
    public long TrustGeneration { get; set; }
    [JsonPropertyOrder(4), JsonPropertyName("keys")]
    public List<Stage3TrustedKeyRecord> Keys { get; set; } = [];
    [JsonPropertyOrder(5), JsonPropertyName("previous_trust_store_digest")]
    public string PreviousTrustStoreDigest { get; set; } = new('0', 64);
    [JsonPropertyOrder(6), JsonPropertyName("trust_store_digest")]
    public string TrustStoreDigest { get; set; } = string.Empty;
    [JsonPropertyOrder(7), JsonPropertyName("root_signer_key_id")]
    public string RootSignerKeyId { get; set; } = string.Empty;
    [JsonPropertyOrder(8), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    [JsonPropertyOrder(9), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

public sealed class Stage3TrustedKeyRecord
{
    public string KeyId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Algorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;
    public string PublicKeyFingerprintSha256 { get; set; } = string.Empty;
    public string ActivationUtc { get; set; } = string.Empty;
    public string ExpirationUtc { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string RevocationReason { get; set; } = string.Empty;
    public long MinimumAcceptedWatcherBuildGeneration { get; set; }
    public string RetiringOverlapEndsUtc { get; set; } = string.Empty;
}

public sealed class Stage3TrustRootPublicV1
{
    public string Schema { get; set; } = "DCS_WATCHER_TRUST_ROOT_PUBLIC_V1";
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;
    public string PublicKeyFingerprintSha256 { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3CodexIntakeFrameV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_CODEX_INTAKE_FRAME_V1";
    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyOrder(2), JsonPropertyName("delivery_classification")]
    public string DeliveryClassification { get; set; } = "watcher_stage3_readiness_test";
    [JsonPropertyOrder(3), JsonPropertyName("signed_transaction_base64")]
    public string SignedTransactionBase64 { get; set; } = string.Empty;
    [JsonPropertyOrder(4), JsonPropertyName("build_attestation_base64")]
    public string BuildAttestationBase64 { get; set; } = string.Empty;
    [JsonPropertyOrder(5), JsonPropertyName("build_attestation_sha256")]
    public string BuildAttestationSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(6), JsonPropertyName("destination_codex_thread_id")]
    public string DestinationCodexThreadId { get; set; } = string.Empty;
    [JsonPropertyOrder(7), JsonPropertyName("sender_process_id")]
    public int SenderProcessId { get; set; }
    [JsonPropertyOrder(8), JsonPropertyName("sender_stage")]
    public string SenderStage { get; set; } = "Stage3ManualPilotReady";
    [JsonPropertyOrder(9), JsonPropertyName("issued_at_utc")]
    public string IssuedAtUtc { get; set; } = string.Empty;
}

public sealed class Stage3CodexIntakeConfiguration
{
    public string ExpectedDirectorThreadId { get; set; } = string.Empty;
    public string TrustStorePath { get; set; } = string.Empty;
    public string TrustRootPath { get; set; } = string.Empty;
    public string TrustAnchorPath { get; set; } = string.Empty;
    public string AllowedBuildAttestationPath { get; set; } = string.Empty;
    public string BuildGenerationAnchorPath { get; set; } = string.Empty;
    public string ConfigurationTemplatePath { get; set; } = string.Empty;
    public string ProvenanceSchemaPath { get; set; } = string.Empty;
    public string VerifierContractPath { get; set; } = string.Empty;
    public string ReplayContractPath { get; set; } = string.Empty;
    public string OutboundLedgerDirectory { get; set; } = string.Empty;
    public string OutboundLedgerInstanceId { get; set; } = string.Empty;
    public string OutboundLedgerMutexName { get; set; } = string.Empty;
    public string OutboundLedgerAnchorDirectory { get; set; } = string.Empty;
    public string IntakeLedgerDirectory { get; set; } = string.Empty;
    public string IntakeLedgerInstanceId { get; set; } = string.Empty;
    public string IntakeLedgerMutexName { get; set; } = string.Empty;
    public string IntakeLedgerAnchorDirectory { get; set; } = string.Empty;
    public string IntakeCheckpointSignerKeyId { get; set; } = string.Empty;
    public string IntakeCheckpointCngKeyName { get; set; } = string.Empty;
    public string QuarantineDirectory { get; set; } = string.Empty;
    public string TestSinkDirectory { get; set; } = string.Empty;
    public int LockTimeoutMilliseconds { get; set; } = 5000;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Stage3CodexIntakePolicyV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_CODEX_INTAKE_POLICY_V1";
    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyOrder(2), JsonPropertyName("policy_generation")]
    public long PolicyGeneration { get; set; } = 1;
    [JsonPropertyOrder(3), JsonPropertyName("expected_trust_root_fingerprint_sha256")]
    public string ExpectedTrustRootFingerprintSha256 { get; set; } = string.Empty;
    [JsonPropertyOrder(4), JsonPropertyName("minimum_build_generation")]
    public long MinimumBuildGeneration { get; set; } = 1;
    [JsonPropertyOrder(5), JsonPropertyName("allowed_source_commit")]
    public string AllowedSourceCommit { get; set; } = string.Empty;
    [JsonPropertyOrder(6), JsonPropertyName("allowed_compiler_identity")]
    public string AllowedCompilerIdentity { get; set; } = string.Empty;
    [JsonPropertyOrder(7), JsonPropertyName("issue_time_utc")]
    public string IssueTimeUtc { get; set; } = string.Empty;
    [JsonPropertyOrder(8), JsonPropertyName("expiry_time_utc")]
    public string ExpiryTimeUtc { get; set; } = string.Empty;
    [JsonPropertyOrder(9), JsonPropertyName("configuration")]
    public Stage3CodexIntakeConfiguration Configuration { get; set; } = new();
    [JsonPropertyOrder(10), JsonPropertyName("signer_key_id")]
    public string SignerKeyId { get; set; } = string.Empty;
    [JsonPropertyOrder(11), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    [JsonPropertyOrder(12), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

public sealed record Stage3IntakePolicyResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    Stage3CodexIntakePolicyV1? Policy = null,
    byte[]? PolicyBytes = null);

public sealed class Stage3RuntimeIdentity
{
    public string Role { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ApplicationAssemblyPath { get; set; } = string.Empty;
    public string ActiveConfigurationPath { get; set; } = string.Empty;
    public string ActiveConfigurationSha256 { get; set; } = string.Empty;
}

public sealed class Stage3IntakeResult
{
    public string Disposition { get; set; } = "REJECTED";
    public string ReasonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string EvidencePath { get; set; } = string.Empty;
    public string TestSinkPath { get; set; } = string.Empty;
    public string EnvelopeSha256 { get; set; } = string.Empty;
    public string ProvenanceSha256 { get; set; } = string.Empty;
    public string SignerFingerprint { get; set; } = string.Empty;
    public string DestinationCodexThreadId { get; set; } = string.Empty;
    public string CodexTurnId { get; set; } = string.Empty;
    public bool ActionableInstructionExposed { get; set; }
}

public sealed record Stage3VerifiedPilotInstruction(
    byte[] EnvelopeBytes,
    Stage2InstructionProvenanceV1 Provenance,
    string ProvenanceSha256,
    string SignerFingerprint);

public sealed record Stage3LiveDeliveryResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    string TurnId = "");

public sealed class Stage3ManualPilotResult
{
    public string Disposition { get; set; } = "FAIL_CLOSED";
    public string ReasonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string NewLocalCommit { get; set; } = string.Empty;
    public long SnapshotDurationMilliseconds { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string CurrentNode { get; set; } = string.Empty;
    public DateTimeOffset? SnapshotTimestampUtc { get; set; }
    public List<string> CurrentPathMessageIds { get; set; } = [];
    public List<string> VisibleActiveBranchMessageIds { get; set; } = [];
    public string TransactionId { get; set; } = string.Empty;
    public string WakeMessageId { get; set; } = string.Empty;
    public string AssistantMessageId { get; set; } = string.Empty;
    public string EnvelopeSha256 { get; set; } = string.Empty;
    public string ProvenanceSha256 { get; set; } = string.Empty;
    public string SignerFingerprint { get; set; } = string.Empty;
    public string DestinationCodexThreadId { get; set; } = string.Empty;
    public string FirstDeliveryResult { get; set; } = string.Empty;
    public string ReplayResult { get; set; } = string.Empty;
    public string OutboundLedgerEntry { get; set; } = string.Empty;
    public string IntakeLedgerEntry { get; set; } = string.Empty;
    public int DuplicateAcceptanceCount { get; set; }
    public int UnauthorizedDeliveryCount { get; set; }
    public bool WatcherStopped { get; set; }
}

public sealed record Stage3ReadinessPipelineResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    byte[]? FrameBytes = null,
    Stage2InstructionProvenanceV1? Provenance = null,
    string FramePath = "");

public sealed class Stage3TestCaseResult
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class Stage3ReadinessTestReport
{
    public string Schema { get; set; } = "watcher-stage3-readiness-test-results-v1";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int Stage2Passed { get; set; }
    public int Stage2Failed { get; set; }
    public int Stage3Passed { get; set; }
    public int Stage3Failed { get; set; }
    public int TotalPassed { get; set; }
    public int TotalFailed { get; set; }
    public List<Stage2TestCaseResult> Stage2Tests { get; set; } = [];
    public List<Stage3TestCaseResult> Stage3Tests { get; set; } = [];
}

public sealed class Stage3FaultInjectionReport
{
    public string Schema { get; set; } = "watcher-stage3-fault-injection-results-v1";
    public int Seed { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int DuplicateTransactionAttempts { get; set; }
    public int DistinctTransactionAttempts { get; set; }
    public int CrashRecoverySimulations { get; set; }
    public int TamperedLedgerStartups { get; set; }
    public int TrustStoreRollbackAttempts { get; set; }
    public int ReplayAttempts { get; set; }
    public int DuplicateAcceptances { get; set; }
    public int UnauthorizedDeliveries { get; set; }
    public int SilentRecoveries { get; set; }
    public int LiveOutputs { get; set; }
    public long DurationMilliseconds { get; set; }
    public int CrossProcessWorkerCount { get; set; }
    public int DuplicateReservationAcceptedCount { get; set; }
    public int DistinctReservationAcceptedCount { get; set; }
    public string LockBehavior { get; set; } = string.Empty;
    public List<int> ProcessIds { get; set; } = [];
    public Dictionary<string, int> RejectionReasons { get; set; } = new(StringComparer.Ordinal);
    public string FinalLedgerState { get; set; } = string.Empty;
}

public sealed class Stage3LedgerWorkerRequest
{
    public string Action { get; set; } = "reserve";
    public Stage3LedgerIdentity Identity { get; set; } = new();
    public string TrustStorePath { get; set; } = string.Empty;
    public string TrustRootPath { get; set; } = string.Empty;
    public string TrustAnchorPath { get; set; } = string.Empty;
    public string CheckpointSignerKeyId { get; set; } = string.Empty;
    public string CheckpointSignerCngKeyName { get; set; } = string.Empty;
    public string CheckpointSignerPurpose { get; set; } = string.Empty;
    public string ProvenancePath { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string NextDisposition { get; set; } = string.Empty;
    public bool IncrementAttempt { get; set; }
    public int LockTimeoutMilliseconds { get; set; } = 5000;
    public int HoldMilliseconds { get; set; }
    public int AttemptCount { get; set; } = 1;
    public bool UniqueTransactions { get; set; }
    public string BatchPrefix { get; set; } = string.Empty;
    public string FaultStopAfterStep { get; set; } = string.Empty;
    public bool TerminateProcessAtFault { get; set; }
    public string ResultPath { get; set; } = string.Empty;
}

public sealed class Stage3LedgerWorkerBatchResult
{
    public int ProcessId { get; set; }
    public int Attempts { get; set; }
    public int Accepted { get; set; }
    public Dictionary<string, int> RejectionReasons { get; set; } = new(StringComparer.Ordinal);
}

public sealed class Stage3SecurityProvisioningSpec
{
    public string TrustStorePath { get; set; } = string.Empty;
    public string TrustRootPath { get; set; } = string.Empty;
    public string TrustAnchorPath { get; set; } = string.Empty;
    public string TrustStoreInstanceId { get; set; } = string.Empty;
    public string AttestationDraftPath { get; set; } = string.Empty;
    public string SignedAttestationPath { get; set; } = string.Empty;
    public string BuildGenerationAnchorPath { get; set; } = string.Empty;
    public string ProvenanceKeyId { get; set; } = string.Empty;
    public string ProvenanceCngKeyName { get; set; } = string.Empty;
    public string TrustRootKeyId { get; set; } = string.Empty;
    public string TrustRootCngKeyName { get; set; } = string.Empty;
    public string BuildAttestationKeyId { get; set; } = string.Empty;
    public string BuildAttestationCngKeyName { get; set; } = string.Empty;
    public string OutboundCheckpointKeyId { get; set; } = string.Empty;
    public string OutboundCheckpointCngKeyName { get; set; } = string.Empty;
    public string IntakeCheckpointKeyId { get; set; } = string.Empty;
    public string IntakeCheckpointCngKeyName { get; set; } = string.Empty;
    public string IntakePolicyKeyId { get; set; } = string.Empty;
    public string IntakePolicyCngKeyName { get; set; } = string.Empty;
    public string IntakePolicyDraftPath { get; set; } = string.Empty;
    public string SignedIntakePolicyPath { get; set; } = string.Empty;
}

public sealed class Stage3SecurityProvisioningResult
{
    public string Algorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;
    public long TrustStoreGeneration { get; set; }
    public string ProvenanceFingerprintSha256 { get; set; } = string.Empty;
    public string TrustRootFingerprintSha256 { get; set; } = string.Empty;
    public string BuildAttestationFingerprintSha256 { get; set; } = string.Empty;
    public string OutboundCheckpointFingerprintSha256 { get; set; } = string.Empty;
    public string IntakeCheckpointFingerprintSha256 { get; set; } = string.Empty;
    public string IntakePolicySignerFingerprintSha256 { get; set; } = string.Empty;
    public string SignedIntakePolicySha256 { get; set; } = string.Empty;
    public string SignedAttestationSha256 { get; set; } = string.Empty;
    public string TrustStorePath { get; set; } = string.Empty;
    public string SignedAttestationPath { get; set; } = string.Empty;
    public string SignedIntakePolicyPath { get; set; } = string.Empty;
    public long OutboundReplayLedgerGeneration { get; set; }
    public long IntakeReplayLedgerGeneration { get; set; }
    public string OutboundReplayLedgerPath { get; set; } = string.Empty;
    public string IntakeReplayLedgerPath { get; set; } = string.Empty;
    public string OutboundMutexName { get; set; } = string.Empty;
    public string IntakeMutexName { get; set; } = string.Empty;
    public string PrivateKeyDisposition { get; set; } = "Windows current-user CNG; non-exportable; excluded from reports and bundles";
}
