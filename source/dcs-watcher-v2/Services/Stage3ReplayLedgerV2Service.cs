using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3ReplayLedgerV2Service
{
    public const string LedgerFileName = "ledger-v2.json";
    public const string CheckpointFileName = "ledger-v2.checkpoint.json";
    public const string LockOwnerFileName = "ledger-v2.lock-owner.json";

    private static readonly Dictionary<string, HashSet<string>> LegalTransitions = new(StringComparer.Ordinal)
    {
        [Stage3TransactionStates.Reserved] = [Stage3TransactionStates.Validated, Stage3TransactionStates.Rejected, Stage3TransactionStates.Cancelled, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.Validated] = [Stage3TransactionStates.Signed, Stage3TransactionStates.Rejected, Stage3TransactionStates.Cancelled, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.Signed] = [Stage3TransactionStates.Serialized, Stage3TransactionStates.Rejected, Stage3TransactionStates.Cancelled, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.Serialized] = [Stage3TransactionStates.TestSinkSent, Stage3TransactionStates.LiveDeliveryPending, Stage3TransactionStates.Rejected, Stage3TransactionStates.Cancelled, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.TestSinkSent] = [Stage3TransactionStates.TestSinkAccepted, Stage3TransactionStates.Rejected, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.TestSinkAccepted] = [Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.LiveDeliveryPending] = [Stage3TransactionStates.LiveDelivered, Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.LiveDelivered] = [Stage3TransactionStates.RecoveryRequired],
        [Stage3TransactionStates.Rejected] = [],
        [Stage3TransactionStates.Cancelled] = [],
        [Stage3TransactionStates.RecoveryRequired] = []
    };

    private readonly Stage3LedgerIdentity _identity;
    private readonly IStage2ProvenanceSigner _checkpointSigner;
    private readonly IStage2PublicKeyResolver _trustedKeys;
    private readonly TimeSpan _lockTimeout;
    private readonly bool _allowLiveManualPilot;
    private readonly Stage3WindowsMonotonicCounterStore _externalCounter = new();

    public Stage3ReplayLedgerV2Service(
        Stage3LedgerIdentity identity,
        IStage2ProvenanceSigner checkpointSigner,
        IStage2PublicKeyResolver trustedKeys,
        TimeSpan? lockTimeout = null,
        bool allowLiveManualPilot = false)
    {
        _identity = identity;
        _checkpointSigner = checkpointSigner;
        _trustedKeys = trustedKeys;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        _allowLiveManualPilot = allowLiveManualPilot;
        if (string.IsNullOrWhiteSpace(identity.LedgerRole) ||
            string.IsNullOrWhiteSpace(identity.LedgerInstanceId) ||
            string.IsNullOrWhiteSpace(identity.MutexName) ||
            string.IsNullOrWhiteSpace(identity.LedgerDirectory) ||
            string.IsNullOrWhiteSpace(identity.AnchorDirectory))
        {
            throw new ArgumentException("Complete ledger identity is required.", nameof(identity));
        }
        var expectedMutex = CreateMutexName(identity.LedgerRole, identity.LedgerDirectory);
        if (!identity.MutexName.Equals(expectedMutex, StringComparison.Ordinal))
            throw new ArgumentException($"Ledger mutex identity must be derived from role and directory: {expectedMutex}", nameof(identity));
    }

    public string LedgerPath => Path.Combine(_identity.LedgerDirectory, LedgerFileName);
    public string CheckpointPath => Path.Combine(_identity.LedgerDirectory, CheckpointFileName);
    public string LockOwnerPath => Path.Combine(_identity.LedgerDirectory, LockOwnerFileName);
    public string AnchorPath => Path.Combine(_identity.AnchorDirectory, $"{_identity.LedgerInstanceId}.anchor.json");
    public string MutexName => _identity.MutexName;
    public string LedgerRole => _identity.LedgerRole;
    public string LedgerInstanceId => _identity.LedgerInstanceId;

    public Stage3LedgerResult Initialize(DateTimeOffset nowUtc)
    {
        return WithMutex(nowUtc, (abandoned, _) =>
        {
            EnsureDirectoriesRestricted();
            if (File.Exists(LedgerPath) || File.Exists(CheckpointPath) || File.Exists(AnchorPath))
            {
                var existing = ValidateTrustedState(requireNoUnexpectedFiles: true);
                if (!existing.Accepted) return existing;
                var recovery = MarkUnfinishedRecoveryRequired(nowUtc, abandoned);
                return recovery.Accepted ? existing with { AbandonedMutexRecovered = abandoned } : recovery;
            }

            var ledger = new Stage3ReplayLedgerV2
            {
                LedgerRole = _identity.LedgerRole,
                LedgerInstanceId = _identity.LedgerInstanceId,
                LedgerGeneration = 0
            };
            var ledgerBytes = Serialize(ledger);
            WriteDurableAtomic(LedgerPath, ledgerBytes);
            var checkpoint = BuildCheckpoint(ledger, ledgerBytes, nowUtc);
            var checkpointBytes = Serialize(checkpoint);
            WriteDurableAtomic(CheckpointPath, checkpointBytes);
            var anchor = BuildAnchor(ledger, checkpointBytes, nowUtc);
            WriteDurableAtomic(AnchorPath, Serialize(anchor));
            var external = _externalCounter.Advance("replay-ledger", ledger.LedgerInstanceId, AnchorPath,
                ledger.LedgerGeneration, anchor.ObjectDigest, nowUtc);
            if (!external.Accepted) return Reject(external.ReasonCode, external.Message);
            var verified = ValidateTrustedState(requireNoUnexpectedFiles: true);
            return verified.Accepted
                ? verified with { Message = "Replay ledger V2 initialized and verified.", AbandonedMutexRecovered = abandoned }
                : verified;
        });
    }

    public Stage3LedgerResult Reserve(
        Stage2InstructionProvenanceV1 provenance,
        DateTimeOffset nowUtc,
        Stage3LedgerFaultOptions? fault = null)
    {
        return WithMutex(nowUtc, (abandoned, _) =>
        {
            var trusted = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (!trusted.Accepted)
            {
                return trusted;
            }

            var ledger = ReadLedger();
            var conflict = FindReplayConflict(ledger, provenance);
            if (conflict is not null)
            {
                return conflict;
            }

            var entry = BuildEntry(
                ledger,
                provenance.TransactionId,
                provenance.Nonce,
                provenance.EnvelopeSha256,
                provenance.WakeMessageId,
                provenance.AssistantMessageId,
                provenance.DestinationCodexThreadId,
                nowUtc,
                Stage3TransactionStates.Reserved,
                0);
            return CommitEntry(ledger, entry, nowUtc, fault, abandoned);
        });
    }

    public Stage3LedgerResult Transition(
        string transactionId,
        string nextDisposition,
        DateTimeOffset nowUtc,
        bool incrementAttempt = false,
        Stage3LedgerFaultOptions? fault = null)
    {
        return WithMutex(nowUtc, (abandoned, _) =>
        {
            var trusted = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (!trusted.Accepted)
            {
                return trusted;
            }

            if (!_allowLiveManualPilot && nextDisposition is Stage3TransactionStates.LiveDeliveryPending or Stage3TransactionStates.LiveDelivered)
            {
                return Reject("LIVE_STATE_PROHIBITED", "Stage 3 readiness cannot enter a live-delivery state.");
            }

            var ledger = ReadLedger();
            var latest = ledger.Entries.LastOrDefault(entry => entry.TransactionId.Equals(transactionId, StringComparison.Ordinal));
            if (latest is null)
            {
                return Reject("TRANSACTION_UNKNOWN", "Transaction is not present in the ledger.");
            }

            if (!LegalTransitions.TryGetValue(latest.Disposition, out var legal) || !legal.Contains(nextDisposition))
            {
                return Reject("ILLEGAL_STATE_TRANSITION", $"Transition {latest.Disposition} -> {nextDisposition} is prohibited.");
            }

            var entry = BuildEntry(
                ledger,
                latest.TransactionId,
                latest.Nonce,
                latest.EnvelopeSha256,
                latest.WakeMessageId,
                latest.AssistantMessageId,
                latest.DestinationCodexThreadId,
                nowUtc,
                nextDisposition,
                latest.DeliveryAttemptCount + (incrementAttempt ? 1 : 0),
                latest.FirstSeenUtc);
            return CommitEntry(ledger, entry, nowUtc, fault, abandoned);
        });
    }

    public Stage3LedgerResult ValidateOnly(DateTimeOffset nowUtc)
    {
        return WithMutex(nowUtc, (abandoned, _) =>
        {
            var result = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (!result.Accepted) return result;
            var recovery = MarkUnfinishedRecoveryRequired(nowUtc, abandoned);
            return recovery.Accepted ? result with { AbandonedMutexRecovered = abandoned } : recovery;
        });
    }

    public Stage3ReplayLedgerV2 ReadVerifiedLedger(DateTimeOffset nowUtc)
    {
        Stage3ReplayLedgerV2? result = null;
        var validation = WithMutex(nowUtc, (_, _) =>
        {
            var trusted = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (trusted.Accepted)
            {
                result = ReadLedger();
            }
            return trusted;
        });
        if (!validation.Accepted || result is null)
        {
            throw new InvalidDataException($"{validation.ReasonCode}: {validation.Message}");
        }
        return result;
    }

    public Stage3LedgerResult GetLatestTransactionState(string transactionId, DateTimeOffset nowUtc)
    {
        Stage3LedgerResult? state = null;
        var validation = WithMutex(nowUtc, (_, _) =>
        {
            var trusted = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (!trusted.Accepted)
            {
                return trusted;
            }
            var ledger = ReadLedger();
            var entry = ledger.Entries.LastOrDefault(item => item.TransactionId.Equals(transactionId, StringComparison.Ordinal));
            state = entry is null
                ? Reject("TRANSACTION_UNKNOWN", "Transaction is not present in the ledger.")
                : new Stage3LedgerResult(true, "OK", "Latest transaction state is verified.", ledger.LedgerGeneration, entry.Sequence, entry.Disposition);
            return state;
        });
        return state ?? validation;
    }

    public Stage3LedgerResult GetMatchingTransactionState(Stage2InstructionProvenanceV1 provenance, DateTimeOffset nowUtc)
    {
        Stage3LedgerResult? state = null;
        var validation = WithMutex(nowUtc, (_, _) =>
        {
            var trusted = ValidateTrustedState(requireNoUnexpectedFiles: true);
            if (!trusted.Accepted) return trusted;
            var ledger = ReadLedger();
            var entry = ledger.Entries.LastOrDefault(item => item.TransactionId.Equals(provenance.TransactionId, StringComparison.Ordinal));
            if (entry is null) return Reject("TRANSACTION_UNKNOWN", "Transaction is not present in the ledger.");
            if (!entry.Nonce.Equals(provenance.Nonce, StringComparison.Ordinal) ||
                !entry.EnvelopeSha256.Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase) ||
                !entry.WakeMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal) ||
                !entry.AssistantMessageId.Equals(provenance.AssistantMessageId, StringComparison.Ordinal) ||
                !entry.DestinationCodexThreadId.Equals(provenance.DestinationCodexThreadId, StringComparison.Ordinal))
                return Reject("OUTBOUND_PROVENANCE_BINDING_MISMATCH", "Presented provenance does not match the exact outbound ledger reservation.");
            state = new Stage3LedgerResult(true, "OK", "Exact outbound provenance binding is verified.", ledger.LedgerGeneration, entry.Sequence, entry.Disposition);
            return state;
        });
        return state ?? validation;
    }

    private Stage3LedgerResult MarkUnfinishedRecoveryRequired(DateTimeOffset nowUtc, bool abandoned)
    {
        var ledger = ReadLedger();
        var unfinished = ledger.Entries
            .GroupBy(entry => entry.TransactionId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(entry => entry.Disposition is Stage3TransactionStates.Reserved or Stage3TransactionStates.Validated or
                Stage3TransactionStates.Signed or Stage3TransactionStates.Serialized or Stage3TransactionStates.TestSinkSent)
            .ToList();
        foreach (var latest in unfinished)
        {
            var entry = BuildEntry(ledger, latest.TransactionId, latest.Nonce, latest.EnvelopeSha256,
                latest.WakeMessageId, latest.AssistantMessageId, latest.DestinationCodexThreadId, nowUtc,
                Stage3TransactionStates.RecoveryRequired, latest.DeliveryAttemptCount, latest.FirstSeenUtc);
            var committed = CommitEntry(ledger, entry, nowUtc, null, abandoned);
            if (!committed.Accepted) return committed;
        }
        return unfinished.Count == 0
            ? new Stage3LedgerResult(true, "OK", "No unfinished transactions require recovery.")
            : Reject("RECOVERY_REQUIRED", $"{unfinished.Count} unfinished transaction(s) were durably moved to RECOVERY_REQUIRED; automatic resend is prohibited.");
    }

    private Stage3LedgerResult CommitEntry(
        Stage3ReplayLedgerV2 ledger,
        Stage3ReplayLedgerEntryV2 entry,
        DateTimeOffset nowUtc,
        Stage3LedgerFaultOptions? fault,
        bool abandoned)
    {
        if (fault?.StopAfterStep == "before-temporary-write")
        {
            return InjectedStop(fault, "FAULT_BEFORE_TEMPORARY_WRITE", "Injected stop before temporary ledger write.");
        }

        ledger.LedgerGeneration++;
        entry.LedgerGeneration = ledger.LedgerGeneration;
        entry.EntryDigest = ComputeEntryDigest(entry);
        ledger.Entries.Add(entry);
        var ledgerBytes = Serialize(ledger);
        var temporary = LedgerPath + $".{Guid.NewGuid():N}.tmp";

        if (fault?.StopAfterStep == "during-temporary-write")
        {
            WriteDurableFile(temporary, ledgerBytes.AsSpan(0, Math.Max(1, ledgerBytes.Length / 2)).ToArray());
            return InjectedStop(fault, "FAULT_DURING_TEMPORARY_WRITE", "Injected stop during temporary ledger write.");
        }

        WriteDurableFile(temporary, ledgerBytes);
        if (fault?.StopAfterStep is "after-temporary-flush" or "before-atomic-replace")
        {
            return InjectedStop(fault, "FAULT_BEFORE_ATOMIC_REPLACE", "Injected stop after durable temporary ledger write.");
        }

        var rereadTemporary = File.ReadAllBytes(temporary);
        if (!rereadTemporary.AsSpan().SequenceEqual(ledgerBytes) || !ValidateLedgerBytes(rereadTemporary).Accepted)
        {
            return Reject("TEMPORARY_LEDGER_INVALID", "Temporary ledger failed reread verification.");
        }

        ReplaceFromTemporary(temporary, LedgerPath);
        if (!File.ReadAllBytes(LedgerPath).AsSpan().SequenceEqual(ledgerBytes))
        {
            return Reject("AUTHORITATIVE_LEDGER_REREAD_FAILED", "Authoritative ledger differs after atomic replacement.");
        }

        if (fault?.StopAfterStep == "after-atomic-replace")
        {
            return InjectedStop(fault, "FAULT_AFTER_ATOMIC_REPLACE", "Injected stop after ledger replacement and before checkpoint.");
        }

        var checkpoint = BuildCheckpoint(ledger, ledgerBytes, nowUtc);
        var checkpointBytes = Serialize(checkpoint);
        WriteDurableAtomic(CheckpointPath, checkpointBytes);
        if (!ValidateCheckpoint(checkpoint, ledger, ledgerBytes).Accepted)
        {
            return Reject("CHECKPOINT_WRITE_INVALID", "Signed checkpoint failed verification after write.");
        }

        if (fault?.StopAfterStep == "after-checkpoint")
        {
            return InjectedStop(fault, "FAULT_AFTER_CHECKPOINT", "Injected stop after checkpoint and before monotonic anchor.");
        }

        var anchor = BuildAnchor(ledger, checkpointBytes, nowUtc);
        WriteDurableAtomic(AnchorPath, Serialize(anchor));
        var external = _externalCounter.Advance("replay-ledger", ledger.LedgerInstanceId, AnchorPath,
            ledger.LedgerGeneration, anchor.ObjectDigest, nowUtc);
        if (!external.Accepted) return Reject(external.ReasonCode, external.Message);
        var final = ValidateTrustedState(requireNoUnexpectedFiles: true);
        return final.Accepted
            ? new Stage3LedgerResult(true, "OK", "Ledger transition committed and independently verified.", ledger.LedgerGeneration, entry.Sequence, entry.Disposition, abandoned)
            : final;
    }

    private Stage3LedgerResult ValidateTrustedState(bool requireNoUnexpectedFiles)
    {
        if (!File.Exists(LedgerPath)) return Reject("LEDGER_MISSING", "Authoritative ledger is missing.");
        if (!File.Exists(CheckpointPath)) return Reject("CHECKPOINT_MISSING", "Signed ledger checkpoint is missing.");
        if (!File.Exists(AnchorPath)) return Reject("ANCHOR_MISSING", "Monotonic ledger anchor is missing.");

        if (requireNoUnexpectedFiles)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                LedgerFileName,
                CheckpointFileName,
                LockOwnerFileName
            };
            var unexpected = Directory.EnumerateFiles(_identity.LedgerDirectory)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null && !allowed.Contains(name));
            if (unexpected is not null)
            {
                return Reject("UNEXPECTED_LEDGER_FILE", $"Unexpected ledger-directory file requires recovery: {unexpected}");
            }
        }

        var ledgerBytes = File.ReadAllBytes(LedgerPath);
        var ledgerValidation = ValidateLedgerBytes(ledgerBytes);
        if (!ledgerValidation.Accepted) return ledgerValidation;
        var ledger = DeserializeStrict<Stage3ReplayLedgerV2>(ledgerBytes, "LEDGER_JSON_INVALID");
        if (ledger is null) return Reject("LEDGER_JSON_INVALID", "Ledger could not be parsed.");

        var checkpointBytes = File.ReadAllBytes(CheckpointPath);
        var checkpoint = DeserializeStrict<Stage3LedgerCheckpointV1>(checkpointBytes, "CHECKPOINT_JSON_INVALID");
        if (checkpoint is null || !checkpointBytes.AsSpan().SequenceEqual(Serialize(checkpoint)))
        {
            return Reject("CHECKPOINT_NONCANONICAL", "Checkpoint is invalid or noncanonical.");
        }
        var checkpointValidation = ValidateCheckpoint(checkpoint, ledger, ledgerBytes);
        if (!checkpointValidation.Accepted) return checkpointValidation;

        var anchorBytes = File.ReadAllBytes(AnchorPath);
        var anchor = DeserializeStrict<Stage3MonotonicAnchorV1>(anchorBytes, "ANCHOR_JSON_INVALID");
        if (anchor is null || !anchorBytes.AsSpan().SequenceEqual(Serialize(anchor)))
        {
            return Reject("ANCHOR_NONCANONICAL", "Monotonic anchor is invalid or noncanonical.");
        }
        var anchorValidation = ValidateAnchor(anchor, ledger, checkpointBytes);
        if (!anchorValidation.Accepted) return anchorValidation;

        return new Stage3LedgerResult(true, "OK", "Ledger, hash chain, signed checkpoint, and monotonic anchor are valid.", ledger.LedgerGeneration, ledger.Entries.LastOrDefault()?.Sequence ?? 0, ledger.Entries.LastOrDefault()?.Disposition ?? string.Empty);
    }

    private Stage3LedgerResult ValidateLedgerBytes(byte[] bytes)
    {
        var ledger = DeserializeStrict<Stage3ReplayLedgerV2>(bytes, "LEDGER_JSON_INVALID");
        if (ledger is null) return Reject("LEDGER_JSON_INVALID", "Ledger JSON is invalid.");
        if (!bytes.AsSpan().SequenceEqual(Serialize(ledger))) return Reject("LEDGER_NONCANONICAL", "Ledger JSON is noncanonical.");
        if (!ledger.Schema.Equals(Stage3ReplayLedgerV2.SchemaName, StringComparison.Ordinal) || ledger.Version != 2)
            return Reject("LEDGER_SCHEMA_INVALID", "Ledger schema or version is invalid.");
        if (!ledger.LedgerRole.Equals(_identity.LedgerRole, StringComparison.Ordinal))
            return Reject("LEDGER_ROLE_MISMATCH", "Outbound/intake ledger substitution detected.");
        if (!ledger.LedgerInstanceId.Equals(_identity.LedgerInstanceId, StringComparison.Ordinal))
            return Reject("LEDGER_INSTANCE_MISMATCH", "Ledger instance replacement detected.");
        if (ledger.LedgerGeneration != ledger.Entries.Count)
            return Reject("LEDGER_GENERATION_INVALID", "Ledger generation does not equal append-logical entry count.");

        var sequenceSet = new HashSet<long>();
        var expectedPrevious = new string('0', 64);
        for (var index = 0; index < ledger.Entries.Count; index++)
        {
            var entry = ledger.Entries[index];
            if (entry.Sequence != index + 1 || !sequenceSet.Add(entry.Sequence))
                return Reject("LEDGER_SEQUENCE_INVALID", "Ledger sequence is duplicated, missing, reordered, or rolled back.");
            if (entry.LedgerGeneration != entry.Sequence || !entry.LedgerInstanceId.Equals(ledger.LedgerInstanceId, StringComparison.Ordinal))
                return Reject("ENTRY_IDENTITY_INVALID", "Entry generation or instance identity is invalid.");
            if (!entry.PreviousEntryDigest.Equals(expectedPrevious, StringComparison.OrdinalIgnoreCase))
                return Reject("HASH_CHAIN_BROKEN", "Previous-entry digest does not match the ledger chain.");
            var digest = ComputeEntryDigest(entry);
            if (!digest.Equals(entry.EntryDigest, StringComparison.OrdinalIgnoreCase))
                return Reject("ENTRY_DIGEST_INVALID", "Ledger entry digest is invalid.");
            expectedPrevious = entry.EntryDigest;
        }

        var grouped = ledger.Entries.GroupBy(entry => entry.TransactionId, StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            var first = group.First();
            if (group.Any(entry =>
                    !entry.Nonce.Equals(first.Nonce, StringComparison.Ordinal) ||
                    !entry.EnvelopeSha256.Equals(first.EnvelopeSha256, StringComparison.OrdinalIgnoreCase) ||
                    !entry.WakeMessageId.Equals(first.WakeMessageId, StringComparison.Ordinal) ||
                    !entry.AssistantMessageId.Equals(first.AssistantMessageId, StringComparison.Ordinal) ||
                    !entry.DestinationCodexThreadId.Equals(first.DestinationCodexThreadId, StringComparison.Ordinal)))
                return Reject("TRANSACTION_IDENTITY_MUTATED", "A transaction identity changed across transitions.");
            var previousState = string.Empty;
            foreach (var entry in group)
            {
                if (previousState.Length > 0 && (!LegalTransitions.TryGetValue(previousState, out var legal) || !legal.Contains(entry.Disposition)))
                    return Reject("LEDGER_TRANSITION_INVALID", "Ledger contains an illegal transaction-state transition.");
                previousState = entry.Disposition;
            }
        }

        return new Stage3LedgerResult(true, "OK", "Ledger hash chain is valid.", ledger.LedgerGeneration);
    }

    private Stage3LedgerResult ValidateCheckpoint(Stage3LedgerCheckpointV1 checkpoint, Stage3ReplayLedgerV2 ledger, byte[] ledgerBytes)
    {
        if (!checkpoint.Schema.Equals("DCS_WATCHER_LEDGER_CHECKPOINT_V1", StringComparison.Ordinal) || checkpoint.Version != 1)
            return Reject("CHECKPOINT_SCHEMA_INVALID", "Checkpoint schema or version is invalid.");
        if (!checkpoint.LedgerRole.Equals(ledger.LedgerRole, StringComparison.Ordinal) ||
            !checkpoint.LedgerInstanceId.Equals(ledger.LedgerInstanceId, StringComparison.Ordinal))
            return Reject("CHECKPOINT_IDENTITY_MISMATCH", "Checkpoint is for another ledger.");
        if (checkpoint.LedgerGeneration != ledger.LedgerGeneration || checkpoint.EntryCount != ledger.Entries.Count)
            return checkpoint.LedgerGeneration > ledger.LedgerGeneration
                ? Reject("CHECKPOINT_NEWER_THAN_LEDGER", "Checkpoint generation is newer than the ledger.")
                : Reject("LEDGER_NEWER_THAN_CHECKPOINT", "Ledger generation is newer than the checkpoint.");
        var head = ledger.Entries.LastOrDefault()?.EntryDigest ?? new string('0', 64);
        if (!checkpoint.HeadEntryDigest.Equals(head, StringComparison.OrdinalIgnoreCase) ||
            !checkpoint.LedgerSha256.Equals(Stage2Crypto.Sha256Hex(ledgerBytes), StringComparison.OrdinalIgnoreCase))
            return Reject("CHECKPOINT_DIGEST_MISMATCH", "Checkpoint does not bind the exact ledger head and bytes.");
        if (!VerifySignedObject(checkpoint.SignerKeyId, checkpoint.SignatureAlgorithm, checkpoint.Signature, SerializeUnsigned(checkpoint)))
            return Reject("CHECKPOINT_SIGNATURE_INVALID", "Signed checkpoint verification failed.");
        return new Stage3LedgerResult(true, "OK", "Signed checkpoint is valid.", ledger.LedgerGeneration);
    }

    private Stage3LedgerResult ValidateAnchor(Stage3MonotonicAnchorV1 anchor, Stage3ReplayLedgerV2 ledger, byte[] checkpointBytes)
    {
        if (!anchor.Schema.Equals("DCS_WATCHER_MONOTONIC_ANCHOR_V1", StringComparison.Ordinal) || anchor.Version != 1 ||
            !anchor.AnchorPurpose.Equals("replay-ledger", StringComparison.Ordinal))
            return Reject("ANCHOR_SCHEMA_INVALID", "Monotonic anchor schema or purpose is invalid.");
        if (!anchor.ObjectInstanceId.Equals(ledger.LedgerInstanceId, StringComparison.Ordinal))
            return Reject("ANCHOR_INSTANCE_MISMATCH", "Monotonic anchor is for another ledger instance.");
        if (anchor.MaximumGeneration > ledger.LedgerGeneration)
            return Reject("LEDGER_GENERATION_ROLLBACK", "Ledger is older than its monotonic anchor.");
        if (anchor.MaximumGeneration < ledger.LedgerGeneration)
            return Reject("ANCHOR_GENERATION_STALE", "Ledger is newer than its monotonic anchor; recovery is required.");
        if (!anchor.ObjectDigest.Equals(Stage2Crypto.Sha256Hex(checkpointBytes), StringComparison.OrdinalIgnoreCase))
            return Reject("ANCHOR_DIGEST_MISMATCH", "Anchor does not bind the current signed checkpoint.");
        if (!VerifySignedObject(anchor.SignerKeyId, anchor.SignatureAlgorithm, anchor.Signature, SerializeUnsigned(anchor)))
            return Reject("ANCHOR_SIGNATURE_INVALID", "Monotonic anchor signature is invalid.");
        var external = _externalCounter.Validate("replay-ledger", ledger.LedgerInstanceId, AnchorPath,
            ledger.LedgerGeneration, anchor.ObjectDigest);
        if (!external.Accepted) return Reject(external.ReasonCode, external.Message);
        return new Stage3LedgerResult(true, "OK", "Monotonic anchor is valid.", ledger.LedgerGeneration);
    }

    private Stage3LedgerCheckpointV1 BuildCheckpoint(Stage3ReplayLedgerV2 ledger, byte[] ledgerBytes, DateTimeOffset nowUtc)
    {
        var checkpoint = new Stage3LedgerCheckpointV1
        {
            LedgerRole = ledger.LedgerRole,
            LedgerInstanceId = ledger.LedgerInstanceId,
            LedgerGeneration = ledger.LedgerGeneration,
            EntryCount = ledger.Entries.Count,
            HeadEntryDigest = ledger.Entries.LastOrDefault()?.EntryDigest ?? new string('0', 64),
            LedgerSha256 = Stage2Crypto.Sha256Hex(ledgerBytes),
            IssuedAtUtc = nowUtc.ToUniversalTime().ToString("O"),
            SignerKeyId = _checkpointSigner.KeyId
        };
        checkpoint.Signature = Convert.ToBase64String(_checkpointSigner.Sign(SerializeUnsigned(checkpoint)));
        return checkpoint;
    }

    private Stage3MonotonicAnchorV1 BuildAnchor(Stage3ReplayLedgerV2 ledger, byte[] checkpointBytes, DateTimeOffset nowUtc)
    {
        var anchor = new Stage3MonotonicAnchorV1
        {
            AnchorPurpose = "replay-ledger",
            ObjectInstanceId = ledger.LedgerInstanceId,
            MaximumGeneration = ledger.LedgerGeneration,
            ObjectDigest = Stage2Crypto.Sha256Hex(checkpointBytes),
            IssuedAtUtc = nowUtc.ToUniversalTime().ToString("O"),
            SignerKeyId = _checkpointSigner.KeyId
        };
        anchor.Signature = Convert.ToBase64String(_checkpointSigner.Sign(SerializeUnsigned(anchor)));
        return anchor;
    }

    private Stage3ReplayLedgerEntryV2 BuildEntry(
        Stage3ReplayLedgerV2 ledger,
        string transactionId,
        string nonce,
        string envelopeSha256,
        string wakeMessageId,
        string assistantMessageId,
        string destinationThreadId,
        DateTimeOffset nowUtc,
        string disposition,
        int attemptCount,
        string? firstSeenUtc = null)
    {
        return new Stage3ReplayLedgerEntryV2
        {
            LedgerInstanceId = ledger.LedgerInstanceId,
            Sequence = ledger.Entries.Count + 1,
            TransactionId = transactionId,
            Nonce = nonce,
            EnvelopeSha256 = envelopeSha256,
            WakeMessageId = wakeMessageId,
            AssistantMessageId = assistantMessageId,
            DestinationCodexThreadId = destinationThreadId,
            FirstSeenUtc = firstSeenUtc ?? nowUtc.ToUniversalTime().ToString("O"),
            LastTransitionUtc = nowUtc.ToUniversalTime().ToString("O"),
            Disposition = disposition,
            DeliveryAttemptCount = attemptCount,
            PreviousEntryDigest = ledger.Entries.LastOrDefault()?.EntryDigest ?? new string('0', 64),
            CheckpointSignerKeyId = _checkpointSigner.KeyId
        };
    }

    private Stage3LedgerResult? FindReplayConflict(Stage3ReplayLedgerV2 ledger, Stage2InstructionProvenanceV1 provenance)
    {
        foreach (var entry in ledger.Entries.GroupBy(item => item.TransactionId, StringComparer.Ordinal).Select(group => group.First()))
        {
            if (entry.TransactionId.Equals(provenance.TransactionId, StringComparison.Ordinal))
                return Reject("TRANSACTION_REPLAY", "Transaction ID already exists in the ledger.");
            if (entry.Nonce.Equals(provenance.Nonce, StringComparison.Ordinal))
                return Reject("NONCE_REPLAY", "Nonce already exists in the ledger.");
            if (entry.AssistantMessageId.Equals(provenance.AssistantMessageId, StringComparison.Ordinal) &&
                !entry.WakeMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal))
                return Reject("ASSISTANT_RESPONSE_REPLAY", "Assistant response is already bound to another wake.");
            if (entry.EnvelopeSha256.Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (!entry.DestinationCodexThreadId.Equals(provenance.DestinationCodexThreadId, StringComparison.Ordinal))
                    return Reject("DESTINATION_REPLAY", "Envelope is already bound to another destination.");
                return Reject("ENVELOPE_REPLAY", "Envelope already exists in the ledger.");
            }
        }
        return null;
    }

    private Stage3ReplayLedgerV2 ReadLedger()
    {
        return DeserializeStrict<Stage3ReplayLedgerV2>(File.ReadAllBytes(LedgerPath), "LEDGER_JSON_INVALID")
            ?? throw new InvalidDataException("Ledger JSON is invalid after validation.");
    }

    private Stage3LedgerResult WithMutex(DateTimeOffset nowUtc, Func<bool, Mutex, Stage3LedgerResult> action)
    {
        using var mutex = new Mutex(false, _identity.MutexName);
        var acquired = false;
        var abandoned = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(_lockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                abandoned = true;
            }

            if (!acquired)
            {
                return Reject("LOCK_TIMEOUT", $"Named mutex acquisition timed out: {_identity.MutexName}");
            }

            if (!abandoned && File.Exists(LockOwnerPath))
            {
                var previousOwner = DeserializeStrict<Stage3LockOwnerMetadata>(File.ReadAllBytes(LockOwnerPath), "LOCK_OWNER_INVALID");
                if (previousOwner is not null)
                {
                    try
                    {
                        using var previousProcess = Process.GetProcessById(previousOwner.ProcessId);
                        var currentIdentity = previousProcess.StartTime.ToUniversalTime().ToString("O");
                        if (!currentIdentity.Equals(previousOwner.ProcessStartTimeUtc, StringComparison.Ordinal))
                            abandoned = true;
                    }
                    catch (ArgumentException)
                    {
                        abandoned = true;
                    }
                }
            }

            Directory.CreateDirectory(_identity.LedgerDirectory);
            var process = Process.GetCurrentProcess();
            var owner = new Stage3LockOwnerMetadata
            {
                MutexName = _identity.MutexName,
                ProcessId = Environment.ProcessId,
                ProcessStartTimeUtc = process.StartTime.ToUniversalTime().ToString("O"),
                AcquiredAtUtc = nowUtc.ToUniversalTime().ToString("O"),
                AbandonedMutexRecovery = abandoned
            };
            WriteDurableAtomic(LockOwnerPath, Serialize(owner));
            return action(abandoned, mutex);
        }
        catch (Exception ex)
        {
            return Reject("LEDGER_OPERATION_FAILED", ex.Message);
        }
        finally
        {
            if (acquired)
            {
                try { File.Delete(LockOwnerPath); } catch { }
                mutex.ReleaseMutex();
            }
        }
    }

    private bool VerifySignedObject(string keyId, string algorithm, string signatureBase64, byte[] unsignedBytes)
    {
        if (!algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal)) return false;
        var key = _trustedKeys.Find(keyId);
        if (key is null || !key.Status.Equals("active", StringComparison.Ordinal)) return false;
        try
        {
            return Stage2Crypto.Verify(key, unsignedBytes, Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ComputeEntryDigest(Stage3ReplayLedgerEntryV2 entry)
    {
        var clone = Clone(entry);
        clone.EntryDigest = string.Empty;
        return Stage2Crypto.Sha256Hex(Serialize(clone));
    }

    private static byte[] SerializeUnsigned(Stage3LedgerCheckpointV1 checkpoint)
    {
        var clone = Clone(checkpoint);
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static byte[] SerializeUnsigned(Stage3MonotonicAnchorV1 anchor)
    {
        var clone = Clone(anchor);
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(Serialize(value), Stage2CanonicalJson.Options)
            ?? throw new InvalidOperationException("Canonical clone failed.");
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Stage2CanonicalJson.Options);

    private static T? DeserializeStrict<T>(byte[] bytes, string reasonCode)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Stage2CanonicalJson.Options);
        }
        catch (JsonException)
        {
            _ = reasonCode;
            return default;
        }
    }

    private static void WriteDurableAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        WriteDurableFile(temporary, bytes);
        if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes))
            throw new IOException("Durable temporary-file reread mismatch.");
        ReplaceFromTemporary(temporary, path);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new IOException("Authoritative-file reread mismatch.");
    }

    private static void WriteDurableFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceFromTemporary(string temporary, string authoritative)
    {
        if (Path.GetPathRoot(temporary) != Path.GetPathRoot(authoritative))
            throw new IOException("Atomic replacement requires the same volume.");
        File.Move(temporary, authoritative, overwrite: true);
    }

    private void EnsureDirectoriesRestricted()
    {
        Directory.CreateDirectory(_identity.LedgerDirectory);
        Directory.CreateDirectory(_identity.AnchorDirectory);
        if (!OperatingSystem.IsWindows()) return;
        RestrictDirectory(_identity.LedgerDirectory);
        RestrictDirectory(_identity.AnchorDirectory);
    }

    private static void RestrictDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new SecurityException("Current Windows SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static Stage3LedgerResult Reject(string code, string message) => new(false, code, message);

    private static Stage3LedgerResult InjectedStop(Stage3LedgerFaultOptions fault, string code, string message)
    {
        if (fault.TerminateProcess)
            Environment.Exit(70);
        return Reject(code, message);
    }

    public static string CreateMutexName(string ledgerRole, string absoluteLedgerDirectory)
    {
        var identity = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes($"{ledgerRole}|{Path.GetFullPath(absoluteLedgerDirectory).ToUpperInvariant()}"));
        return $@"Global\DcsWatcherV2-LedgerV2-{identity[..32]}";
    }
}
