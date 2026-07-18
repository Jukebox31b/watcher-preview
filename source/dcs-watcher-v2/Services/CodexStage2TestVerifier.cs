using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class CodexStage2TestVerifier
{
    private readonly IStage2PublicKeyResolver _keys;
    private readonly Stage2ReplayLedger _intakeLedger;
    private readonly Stage2EnvelopeValidator _envelopes = new();
    private readonly string _expectedThreadId;
    private readonly TimeSpan _maximumClockSkew;

    public CodexStage2TestVerifier(
        IStage2PublicKeyResolver keys,
        Stage2ReplayLedger intakeLedger,
        string expectedThreadId,
        TimeSpan? maximumClockSkew = null)
    {
        _keys = keys;
        _intakeLedger = intakeLedger;
        _expectedThreadId = expectedThreadId;
        _maximumClockSkew = maximumClockSkew ?? TimeSpan.FromMinutes(2);
    }

    public CodexTestVerificationResult Verify(byte[] payloadBytes, DateTimeOffset nowUtc, int maxEnvelopeBytes = 500_000)
    {
        return VerifyCore(payloadBytes, nowUtc, maxEnvelopeBytes, commitReplay: true);
    }

    public CodexTestVerificationResult VerifyWithoutReplayCommit(byte[] payloadBytes, DateTimeOffset nowUtc, int maxEnvelopeBytes = 500_000)
    {
        return VerifyCore(payloadBytes, nowUtc, maxEnvelopeBytes, commitReplay: false);
    }

    private CodexTestVerificationResult VerifyCore(byte[] payloadBytes, DateTimeOffset nowUtc, int maxEnvelopeBytes, bool commitReplay)
    {
        var parsed = Stage2CanonicalJson.ParseTransaction(payloadBytes);
        if (!parsed.Success || parsed.Transaction is null)
        {
            return Reject(parsed.ReasonCode, parsed.Message);
        }

        var transaction = parsed.Transaction;
        var provenance = transaction.Provenance;
        if (!transaction.Schema.Equals(Stage2BoundInstructionTransactionV1.SchemaName, StringComparison.Ordinal) || transaction.Version != 1)
        {
            return Reject("TRANSACTION_SCHEMA_INVALID", "Unsupported transaction schema or version.");
        }

        if (!transaction.DeliveryClassification.Equals(Stage2BoundInstructionTransactionV1.DryRunClassification, StringComparison.Ordinal))
        {
            return Reject("DELIVERY_MODE_INVALID", "Only the signed dry-run classification is accepted by this test verifier.");
        }

        if (!provenance.Schema.Equals(Stage2InstructionProvenanceV1.SchemaName, StringComparison.Ordinal) || provenance.Version != 1)
        {
            return Reject("PROVENANCE_SCHEMA_INVALID", "Unsupported provenance schema or version.");
        }

        if (string.IsNullOrWhiteSpace(provenance.SignatureOrMac))
        {
            return Reject("SIGNATURE_MISSING", "Signed provenance is required.");
        }

        if (!provenance.SignatureOrMacAlgorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal))
        {
            return Reject("SIGNATURE_ALGORITHM_INVALID", "Unsupported signature algorithm.");
        }

        var key = _keys.Find(provenance.SignerKeyId);
        if (key is null)
        {
            return Reject("SIGNER_UNKNOWN", "Signer key ID is not in the verifier registry.");
        }

        if (!key.Status.Equals("active", StringComparison.Ordinal))
        {
            return Reject("SIGNER_REVOKED", "Signer key is not active.");
        }

        if (!key.Algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal))
        {
            return Reject("SIGNER_ALGORITHM_INVALID", "Signer registry algorithm is not ECDSA P-256 SHA-256.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(provenance.SignatureOrMac);
        }
        catch (FormatException)
        {
            return Reject("SIGNATURE_INVALID", "Signature is not valid Base64.");
        }

        var unsigned = Stage2CanonicalJson.SerializeUnsignedProvenance(provenance);
        if (!Stage2Crypto.Verify(key, unsigned, signature))
        {
            return Reject("SIGNATURE_INVALID", "Provenance signature validation failed.");
        }

        var metadataError = ValidateSignedMetadata(provenance);
        if (metadataError is not null)
        {
            return metadataError;
        }

        byte[] envelopeBytes;
        try
        {
            envelopeBytes = Convert.FromBase64String(transaction.EnvelopeBase64);
        }
        catch (FormatException)
        {
            return Reject("ENVELOPE_ENCODING_INVALID", "Envelope is not valid Base64.");
        }

        var envelope = _envelopes.Validate(envelopeBytes, maxEnvelopeBytes);
        if (!envelope.Valid)
        {
            return Reject(envelope.ReasonCode, envelope.Reason);
        }

        if (envelopeBytes.LongLength != provenance.EnvelopeSizeBytes)
        {
            return Reject("ENVELOPE_SIZE_MISMATCH", "Envelope byte size does not match signed provenance.");
        }

        if (!Stage2Crypto.Sha256Hex(envelopeBytes).Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("ENVELOPE_HASH_MISMATCH", "Envelope SHA-256 does not match signed provenance.");
        }

        if (!envelope.TaskId.Equals(provenance.TaskId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(provenance.TaskId))
        {
            return Reject("TASK_ID_MISMATCH", "Envelope task ID does not match signed provenance.");
        }

        if (!envelope.SourceReport.Equals(provenance.SourceReport, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(provenance.SourceReport))
        {
            return Reject("SOURCE_REPORT_MISMATCH", "Envelope source report does not match signed provenance.");
        }

        if (!provenance.DestinationCodexThreadId.Equals(_expectedThreadId, StringComparison.Ordinal))
        {
            return Reject("DESTINATION_THREAD_MISMATCH", "Signed destination is not this Codex Director thread.");
        }

        var lineageError = ValidateLineage(provenance);
        if (lineageError is not null)
        {
            return lineageError;
        }

        var responseError = ValidateResponseContent(transaction.ResponseMessageContentBase64, provenance, envelope.EnvelopeText);
        if (responseError is not null)
        {
            return responseError;
        }

        if (!TryParseUtc(provenance.WakeMessageCreatedAt, out var wakeCreated) ||
            !TryParseUtc(provenance.AssistantMessageCreatedAt, out var assistantCreated) ||
            !TryParseUtc(provenance.BackendVerificationTimestamp, out var backendVerified) ||
            !TryParseUtc(provenance.IssueTimeUtc, out var issued) ||
            !TryParseUtc(provenance.ExpiryTimeUtc, out var expires) ||
            assistantCreated < wakeCreated || backendVerified < assistantCreated || issued < backendVerified ||
            expires <= issued || expires - issued > TimeSpan.FromMinutes(10))
        {
            return Reject("TIME_INVALID", "Issue or expiry time is invalid.");
        }

        var utcNow = nowUtc.ToUniversalTime();
        if (issued.ToUniversalTime() - utcNow > _maximumClockSkew)
        {
            return Reject("CLOCK_SKEW", "Provenance issue time exceeds permitted clock skew.");
        }

        if (utcNow > expires.ToUniversalTime())
        {
            return Reject("PROVENANCE_EXPIRED", "Provenance has expired.");
        }

        var expectedReplayKey = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("|", new[]
        {
            provenance.TransactionId,
            provenance.Nonce,
            provenance.EnvelopeSha256,
            provenance.WakeMessageId,
            provenance.AssistantMessageId,
            provenance.DestinationCodexThreadId
        })));
        if (!expectedReplayKey.Equals(provenance.ReplayLedgerKey, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("REPLAY_KEY_INVALID", "Replay ledger key does not match signed fields.");
        }

        if (!commitReplay)
        {
            return new CodexTestVerificationResult(true, "VERIFIED_BEFORE_REPLAY", "OK", "Signed transaction passed cryptographic and lineage verification before replay commit.", provenance.TransactionId, provenance.TaskId);
        }

        var reservation = _intakeLedger.Reserve(provenance, "verifying");
        if (!reservation.Accepted)
        {
            return Reject(reservation.ReasonCode, reservation.Message, provenance);
        }

        var accepted = _intakeLedger.MarkAttempt(provenance.TransactionId, "accepted");
        if (!accepted.Accepted)
        {
            return Reject(accepted.ReasonCode, accepted.Message, provenance);
        }

        return new CodexTestVerificationResult(true, "ACCEPTED_FOR_TEST_SINK", "OK", "Signed transaction accepted by offline test sink.", provenance.TransactionId, provenance.TaskId);
    }

    private static CodexTestVerificationResult? ValidateLineage(Stage2InstructionProvenanceV1 provenance)
    {
        var required = new[]
        {
            provenance.TransactionId, provenance.ConversationId, provenance.WakeToken,
            provenance.WakeMessageId, provenance.WakeMessageCreatedAt, provenance.AssistantMessageId,
            provenance.AssistantMessageCreatedAt, provenance.AssistantParentMessageId,
            provenance.ExpectedParentMessageId, provenance.CurrentNodeBeforeWake,
            provenance.CurrentNodeAfterResponse, provenance.CurrentNodeAtCapture,
            provenance.CurrentPathRoot, provenance.BackendVerificationTimestamp,
            provenance.Nonce, provenance.SignerKeyId
        };
        if (required.Any(string.IsNullOrWhiteSpace))
        {
            return Reject("PROVENANCE_FIELD_MISSING", "A required signed provenance field is empty.");
        }

        if (!provenance.DirectParentVerified ||
            !provenance.AssistantParentMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal) ||
            !provenance.ExpectedParentMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal))
        {
            return Reject("DIRECT_PARENT_INVALID", "Assistant response is not the direct child of the exact wake.");
        }

        if (!provenance.AncestryVerified || provenance.AncestryMessageIds.Count < 3 ||
            !provenance.AncestryMessageIds.Contains(provenance.WakeMessageId, StringComparer.Ordinal) ||
            !provenance.AncestryMessageIds.Contains(provenance.AssistantMessageId, StringComparer.Ordinal) ||
            !provenance.AncestryMessageIds[0].Equals(provenance.CurrentPathRoot, StringComparison.Ordinal) ||
            !provenance.AncestryMessageIds[^1].Equals(provenance.AssistantMessageId, StringComparison.Ordinal))
        {
            return Reject("ANCESTRY_INVALID", "Signed ancestry does not contain the exact wake-to-response lineage.");
        }

        var ancestryDigest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", provenance.AncestryMessageIds)));
        if (!ancestryDigest.Equals(provenance.AncestryDigestSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("ANCESTRY_DIGEST_INVALID", "Ancestry digest is inconsistent.");
        }

        if (!provenance.CurrentNodeVerified ||
            !provenance.CurrentNodeAfterResponse.Equals(provenance.AssistantMessageId, StringComparison.Ordinal) ||
            !provenance.CurrentNodeAtCapture.Equals(provenance.AssistantMessageId, StringComparison.Ordinal))
        {
            return Reject("CURRENT_NODE_INVALID", "current_node is not the signed assistant response at capture.");
        }

        var wakeIndex = provenance.AncestryMessageIds.IndexOf(provenance.WakeMessageId);
        var responseIndex = provenance.AncestryMessageIds.IndexOf(provenance.AssistantMessageId);
        if (wakeIndex < 1 || responseIndex != wakeIndex + 1 ||
            !provenance.AncestryMessageIds[wakeIndex - 1].Equals(provenance.CurrentNodeBeforeWake, StringComparison.Ordinal))
        {
            return Reject("CURRENT_PATH_INVALID", "Current-path ancestry ordering is invalid.");
        }

        if (!provenance.OnCurrentPath)
        {
            return Reject("OFF_CURRENT_PATH", "Assistant response is not on the current path.");
        }

        if (provenance.FallbackBodyUsed)
        {
            return Reject("FALLBACK_CAPTURE", "fallbackBody capture is prohibited.");
        }

        if (provenance.WholePageCaptureUsed)
        {
            return Reject("WHOLE_PAGE_CAPTURE", "Whole-page capture is prohibited.");
        }

        if (!provenance.BackendMessageObjectVerified ||
            !provenance.CaptureMethod.Equals(BranchLineageSafetyService.AuthorizedCaptureMethod, StringComparison.Ordinal) ||
            provenance.SelectedAssistantIndex < 0)
        {
            return Reject("BACKEND_MESSAGE_UNVERIFIED", "One identified backend assistant message object is required.");
        }

        return null;
    }

    private static CodexTestVerificationResult? ValidateSignedMetadata(Stage2InstructionProvenanceV1 provenance)
    {
        if (!provenance.EnvelopeSchema.Equals("DCS_CODEX_TASK_V1", StringComparison.Ordinal))
        {
            return Reject("ENVELOPE_SCHEMA_INVALID", "Signed envelope schema is unsupported.");
        }

        if (!Guid.TryParseExact(provenance.TransactionId, "D", out _))
        {
            return Reject("TRANSACTION_ID_INVALID", "Transaction ID must be a canonical GUID.");
        }

        if (!IsLowerHex(provenance.Nonce, 64))
        {
            return Reject("NONCE_INVALID", "Nonce must be 32 bytes represented as lowercase hexadecimal.");
        }

        var hashes = new[]
        {
            provenance.AncestryDigestSha256,
            provenance.EnvelopeSha256,
            provenance.ResponseMessageContentSha256,
            provenance.WatcherSourceTreeSha256,
            provenance.WatcherExecutableSha256,
            provenance.WatcherConfigurationSha256,
            provenance.ReplayLedgerKey
        };
        if (hashes.Any(hash => !IsLowerHex(hash, 64)))
        {
            return Reject("HASH_FIELD_INVALID", "A signed SHA-256 field is missing or malformed.");
        }

        if (!IsLowerHex(provenance.WatcherSourceCommit, 40))
        {
            return Reject("BUILD_IDENTITY_INVALID", "Watcher source commit must be a lowercase 40-character Git object ID.");
        }

        if (provenance.EnvelopeSizeBytes is < 1 or > 500_000 ||
            string.IsNullOrWhiteSpace(provenance.TaskId) ||
            string.IsNullOrWhiteSpace(provenance.SourceReport) ||
            string.IsNullOrWhiteSpace(provenance.DestinationCodexThreadId))
        {
            return Reject("PROVENANCE_VALUE_INVALID", "A signed task, report, destination, or size value is invalid.");
        }

        return null;
    }

    private static CodexTestVerificationResult? ValidateResponseContent(
        string responseBase64,
        Stage2InstructionProvenanceV1 provenance,
        string envelopeText)
    {
        byte[] responseBytes;
        try
        {
            responseBytes = Convert.FromBase64String(responseBase64);
        }
        catch (FormatException)
        {
            return Reject("RESPONSE_ENCODING_INVALID", "Assistant response content is not valid Base64.");
        }

        if (responseBytes.Length == 0 || responseBytes.Length > 750_000)
        {
            return Reject("RESPONSE_SIZE_INVALID", "Assistant response content is empty or oversized.");
        }

        for (var index = 0; index < responseBytes.Length; index++)
        {
            var value = responseBytes[index];
            if (value <= 0x08 || value is 0x0B or 0x0C || value is >= 0x0E and <= 0x1F || value == 0x7F)
            {
                return Reject("RESPONSE_CONTROL_BYTE", $"Assistant response has forbidden control byte 0x{value:X2} at offset {index}.");
            }
        }

        string responseText;
        try
        {
            responseText = new UTF8Encoding(false, true).GetString(responseBytes);
        }
        catch (DecoderFallbackException)
        {
            return Reject("RESPONSE_UTF8_INVALID", "Assistant response is not strict UTF-8.");
        }

        if (!Stage2Crypto.Sha256Hex(responseBytes).Equals(provenance.ResponseMessageContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("RESPONSE_HASH_MISMATCH", "Assistant response content SHA-256 does not match signed provenance.");
        }

        var envelopes = ChatGptEnvelopeCapture.ExtractEnvelopes(responseText);
        if (envelopes.Count != 1 || !envelopes[0].Equals(envelopeText, StringComparison.Ordinal))
        {
            return Reject("RESPONSE_ENVELOPE_BINDING_INVALID", "Assistant response does not contain exactly the bound envelope.");
        }

        return null;
    }

    private static bool IsLowerHex(string value, int length)
    {
        return value.Length == length && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant);
    }

    private static bool TryParseUtc(string value, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(value, out timestamp) && timestamp.Offset == TimeSpan.Zero;
    }

    private static CodexTestVerificationResult Reject(
        string code,
        string message,
        Stage2InstructionProvenanceV1? provenance = null)
    {
        return new CodexTestVerificationResult(false, "REJECTED", code, message, provenance?.TransactionId ?? string.Empty, provenance?.TaskId ?? string.Empty);
    }
}

public sealed class Stage2CodexTestSink
{
    private readonly CodexStage2TestVerifier _verifier;

    public Stage2CodexTestSink(CodexStage2TestVerifier verifier)
    {
        _verifier = verifier;
    }

    public int ReceiveCount { get; private set; }
    public int AcceptedCount { get; private set; }

    public CodexTestVerificationResult Receive(byte[] payload, DateTimeOffset nowUtc)
    {
        ReceiveCount++;
        var result = _verifier.Verify(payload, nowUtc);
        if (result.Accepted)
        {
            AcceptedCount++;
        }

        return result;
    }
}
