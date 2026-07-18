using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public enum Stage2FaultPoint
{
    None,
    BeforeSigning,
    AfterSigningBeforeTestSink
}

public interface IStage2SigningLifecycle
{
    Stage2PipelineResult? BeforeSigning(Stage2InstructionProvenanceV1 provenance, DateTimeOffset nowUtc);
    Stage2PipelineResult? AfterSigning(Stage2InstructionProvenanceV1 provenance, DateTimeOffset nowUtc);
    Stage2PipelineResult? AfterSerialization(Stage2InstructionProvenanceV1 provenance, byte[] transactionBytes, DateTimeOffset nowUtc);
}

public sealed class Stage2DryRunPipeline
{
    public const string HumanDivergenceWarning = "Watcher detected a ChatGPT conversation-branch divergence. No instruction was delivered. Review the human-visible conversation before continuing.";
    public const string DivergenceWarning = BranchLineageSafetyService.DivergenceWarning + ". " + HumanDivergenceWarning;

    private readonly BranchLineageSafetyService _lineage = new();
    private readonly Stage2EnvelopeValidator _envelopes = new();
    private readonly IStage2ProvenanceSigner _signer;
    private readonly Stage2ReplayLedger _outboundLedger;
    private readonly string _transactionDirectory;

    public string SignerKeyId => _signer.KeyId;
    public string SignerPublicKeyFingerprintSha256 => _signer.PublicKeyFingerprintSha256;

    public Stage2DryRunPipeline(
        IStage2ProvenanceSigner signer,
        Stage2ReplayLedger outboundLedger,
        string transactionDirectory)
    {
        _signer = signer;
        _outboundLedger = outboundLedger;
        _transactionDirectory = Path.GetFullPath(transactionDirectory);
    }

    public static WakeTransactionRecord PrepareSyntheticWake(
        string conversationId,
        string currentNode,
        IReadOnlyList<string> visibleAncestry,
        string browserTabIdentity,
        string wakeToken,
        string sourceReport,
        string activeTask)
    {
        return new WakeTransactionRecord
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            ConversationId = conversationId,
            CurrentNodeBeforeWake = currentNode,
            VisibleBranchAncestry = [.. visibleAncestry],
            VisibleParentMessageId = currentNode,
            BrowserTabIdentity = browserTabIdentity,
            WakeToken = wakeToken,
            IntendedSourceReport = sourceReport,
            IntendedActiveTask = activeTask,
            Status = "prepared-offline",
            HumanConfirmed = true
        };
    }

    public Stage2PipelineResult BuildSignedDryRunTransaction(
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string destinationThreadId,
        Stage2BuildIdentity buildIdentity,
        DateTimeOffset nowUtc,
        Stage2FaultPoint faultPoint = Stage2FaultPoint.None,
        int maxEnvelopeBytes = 500_000,
        IStage2SigningLifecycle? signingLifecycle = null)
    {
        if (string.IsNullOrWhiteSpace(wake.TransactionId) || string.IsNullOrWhiteSpace(wake.Nonce))
        {
            return Reject("TRANSACTION_IDENTITY_MISSING", "Transaction ID and nonce must exist before a wake is represented.");
        }

        if (response.AssistantSelectionAmbiguous || response.SelectedAssistantIndex is null or < 0)
        {
            return Reject("ASSISTANT_SELECTION_AMBIGUOUS", "Exactly one indexed assistant message object must be selected.");
        }

        if (response.FallbackBody)
        {
            return RejectUnsafeCapture(wake, snapshot, response, "FALLBACK_CAPTURE", "fallbackBody cannot authorize a transaction.");
        }

        if (response.WholePageCaptureUsed)
        {
            return RejectUnsafeCapture(wake, snapshot, response, "WHOLE_PAGE_CAPTURE", "Whole-page capture cannot authorize a transaction.");
        }

        if (!response.CurrentNodeAtCapture.Equals(snapshot.CurrentNode, StringComparison.Ordinal))
        {
            return Divergence(wake, snapshot, response, "current_node changed before capture.");
        }

        var lineage = _lineage.Validate(wake, snapshot, response);
        if (!lineage.Eligible)
        {
            return lineage.BranchDivergence
                ? Divergence(wake, snapshot, response, lineage.Reason)
                : Reject("LINEAGE_REJECTED", lineage.Reason);
        }

        var envelopeBytes = new UTF8Encoding(false, true).GetBytes(lineage.EnvelopeText);
        var envelope = _envelopes.Validate(envelopeBytes, maxEnvelopeBytes);
        if (!envelope.Valid)
        {
            return Reject(envelope.ReasonCode, envelope.Reason);
        }

        if (!envelope.SourceReport.Equals(wake.IntendedSourceReport, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("SOURCE_REPORT_MISMATCH", "Envelope source report does not match the wake transaction.");
        }

        var ancestry = BranchLineageSafetyService.BuildActionableAncestry(
            snapshot, snapshot.CurrentNode, wake.WakeMessageId, response.MessageId).ToList();
        var issue = nowUtc.ToUniversalTime();
        var provenance = new Stage2InstructionProvenanceV1
        {
            TransactionId = wake.TransactionId,
            ConversationId = wake.ConversationId,
            WakeToken = wake.WakeToken,
            WakeMessageId = wake.WakeMessageId,
            WakeMessageCreatedAt = wake.WakeCreatedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty,
            AssistantMessageId = response.MessageId,
            AssistantMessageCreatedAt = response.CreatedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty,
            AssistantParentMessageId = response.ParentMessageId,
            ExpectedParentMessageId = wake.WakeMessageId,
            CurrentNodeBeforeWake = wake.CurrentNodeBeforeWake,
            CurrentNodeAfterResponse = snapshot.CurrentNode,
            CurrentNodeAtCapture = response.CurrentNodeAtCapture,
            CurrentPathRoot = ancestry.FirstOrDefault() ?? string.Empty,
            OnCurrentPath = response.OnCurrentPath is true,
            AncestryMessageIds = ancestry,
            AncestryDigestSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", ancestry))),
            AncestryVerified = ancestry.Contains(wake.WakeMessageId, StringComparer.Ordinal) && ancestry.Contains(response.MessageId, StringComparer.Ordinal),
            DirectParentVerified = response.ParentMessageId.Equals(wake.WakeMessageId, StringComparison.Ordinal),
            CurrentNodeVerified = snapshot.CurrentNode.Equals(response.MessageId, StringComparison.Ordinal) && response.CurrentNodeAtCapture.Equals(response.MessageId, StringComparison.Ordinal),
            SelectedAssistantIndex = response.SelectedAssistantIndex.Value,
            CaptureMethod = response.CaptureMethod,
            FallbackBodyUsed = response.FallbackBody,
            WholePageCaptureUsed = response.WholePageCaptureUsed,
            BackendMessageObjectVerified = snapshot.ApiVerified && response.ApiVerified,
            BackendVerificationTimestamp = issue.ToString("O"),
            TaskId = envelope.TaskId,
            SourceReport = envelope.SourceReport,
            EnvelopeSizeBytes = envelopeBytes.LongLength,
            EnvelopeSha256 = Stage2Crypto.Sha256Hex(envelopeBytes),
            ResponseMessageContentSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(response.Content)),
            DestinationCodexThreadId = destinationThreadId,
            WatcherSourceCommit = buildIdentity.SourceCommit,
            WatcherSourceTreeSha256 = buildIdentity.SourceTreeSha256,
            WatcherExecutableSha256 = buildIdentity.ExecutableSha256,
            WatcherConfigurationSha256 = buildIdentity.ConfigurationSha256,
            IssueTimeUtc = issue.ToString("O"),
            ExpiryTimeUtc = issue.AddMinutes(5).ToString("O"),
            Nonce = wake.Nonce,
            SignerKeyId = _signer.KeyId,
            SignatureOrMacAlgorithm = Stage2InstructionProvenanceV1.AlgorithmName
        };
        provenance.ReplayLedgerKey = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("|", new[]
        {
            provenance.TransactionId,
            provenance.Nonce,
            provenance.EnvelopeSha256,
            provenance.WakeMessageId,
            provenance.AssistantMessageId,
            provenance.DestinationCodexThreadId
        })));

        var recordPath = PersistTransactionRecord(provenance, "validated-before-signing", string.Empty);
        var reserved = _outboundLedger.Reserve(provenance, "validated-before-signing");
        if (!reserved.Accepted)
        {
            return Reject(reserved.ReasonCode, reserved.Message, recordPath);
        }

        var beforeSigning = signingLifecycle?.BeforeSigning(provenance, nowUtc);
        if (beforeSigning is not null) return beforeSigning;

        if (faultPoint == Stage2FaultPoint.BeforeSigning)
        {
            PersistTransactionRecord(provenance, "interrupted-before-signing", string.Empty, recordPath);
            return Reject("FAULT_BEFORE_SIGNING", "Injected interruption before signing.", recordPath);
        }

        var unsigned = Stage2CanonicalJson.SerializeUnsignedProvenance(provenance);
        provenance.SignatureOrMac = Convert.ToBase64String(_signer.Sign(unsigned));
        PersistTransactionRecord(provenance, "signed-dry-run", provenance.SignatureOrMac, recordPath);
        var afterSigning = signingLifecycle?.AfterSigning(provenance, nowUtc);
        if (afterSigning is not null) return afterSigning;

        if (faultPoint == Stage2FaultPoint.AfterSigningBeforeTestSink)
        {
            return Reject("FAULT_AFTER_SIGNING", "Injected interruption after signing and before test-sink delivery.", recordPath);
        }

        var transaction = new Stage2BoundInstructionTransactionV1
        {
            Provenance = provenance,
            EnvelopeBase64 = Convert.ToBase64String(envelopeBytes),
            ResponseMessageContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(response.Content))
        };
        var transactionBytes = Stage2CanonicalJson.SerializeTransaction(transaction);
        var afterSerialization = signingLifecycle?.AfterSerialization(provenance, transactionBytes, nowUtc);
        return afterSerialization ?? new Stage2PipelineResult(true, "OK", "Signed dry-run transaction created for the offline test sink.", transactionBytes, provenance, recordPath);
    }

    private Stage2PipelineResult Divergence(
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string reason)
    {
        Directory.CreateDirectory(_transactionDirectory);
        var path = Path.Combine(_transactionDirectory, $"divergence-{wake.TransactionId}.json");
        var record = new
        {
            schema = "DCS_WATCHER_BRANCH_DIVERGENCE_V1",
            transaction_id = wake.TransactionId,
            wake_message_id = wake.WakeMessageId,
            response_message_id = response.MessageId,
            current_node = snapshot.CurrentNode,
            reason,
            warning = DivergenceWarning,
            automatic_retry = false,
            signed_provenance_created = false,
            ipc_payload_created = false
        };
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record, Stage2CanonicalJson.Options));
        return new Stage2PipelineResult(false, "BRANCH_DIVERGENCE", $"{DivergenceWarning} {reason}", TransactionRecordPath: path);
    }

    private Stage2PipelineResult RejectUnsafeCapture(
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string reasonCode,
        string reason)
    {
        Directory.CreateDirectory(_transactionDirectory);
        var path = Path.Combine(_transactionDirectory, $"unsafe-capture-{wake.TransactionId}.json");
        var record = new
        {
            schema = "DCS_WATCHER_UNSAFE_CAPTURE_REJECTION_V1",
            transaction_id = wake.TransactionId,
            wake_message_id = wake.WakeMessageId,
            response_message_id = response.MessageId,
            current_node = snapshot.CurrentNode,
            capture_method = response.CaptureMethod,
            fallback_body = response.FallbackBody,
            whole_page_capture = response.WholePageCaptureUsed,
            reason_code = reasonCode,
            reason,
            warning = HumanDivergenceWarning,
            automatic_retry = false,
            signed_provenance_created = false,
            ipc_payload_created = false
        };
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record, Stage2CanonicalJson.Options));
        return new Stage2PipelineResult(false, reasonCode, $"{HumanDivergenceWarning} {reason}", TransactionRecordPath: path);
    }

    private string PersistTransactionRecord(
        Stage2InstructionProvenanceV1 provenance,
        string status,
        string signature,
        string? existingPath = null)
    {
        Directory.CreateDirectory(_transactionDirectory);
        var path = existingPath ?? Path.Combine(_transactionDirectory, $"transaction-{provenance.TransactionId}.json");
        var record = new
        {
            schema = "DCS_WATCHER_DRY_RUN_TRANSACTION_RECORD_V1",
            status,
            transaction_id = provenance.TransactionId,
            nonce = provenance.Nonce,
            wake_message_id = provenance.WakeMessageId,
            assistant_message_id = provenance.AssistantMessageId,
            destination_codex_thread_id = provenance.DestinationCodexThreadId,
            envelope_sha256 = provenance.EnvelopeSha256,
            signer_key_id = provenance.SignerKeyId,
            signature_or_mac = signature
        };
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record, Stage2CanonicalJson.Options));
        return path;
    }

    private static Stage2PipelineResult Reject(string code, string message, string recordPath = "")
    {
        return new Stage2PipelineResult(false, code, message, TransactionRecordPath: recordPath);
    }
}
