using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3CodexIntakeGate
{
    public const int MaximumFrameBytes = 3_000_000;
    private readonly Stage3IntakePolicyService _policyService;
    private readonly InstallationTrustContext? _trustContext;

    public Stage3CodexIntakeGate(InstallationTrustContext trustContext)
        : this(new Stage3IntakePolicyService(trustContext), trustContext)
    {
    }

    internal Stage3CodexIntakeGate(Stage3IntakePolicyService policyService, InstallationTrustContext trustContext)
    {
        _policyService = policyService ?? throw new ArgumentNullException(nameof(policyService));
        _trustContext = trustContext ?? throw new ArgumentNullException(nameof(trustContext));
    }

    public Stage3IntakeResult ProcessFrame(
        byte[] frameBytes,
        byte[] signedPolicyBytes,
        IStage2ProvenanceSigner intakeCheckpointSigner,
        DateTimeOffset nowUtc) => ProcessFrameCore(
            frameBytes, signedPolicyBytes, intakeCheckpointSigner, nowUtc,
            manualPilot: false, liveDelivery: null);

    public Stage3IntakeResult ProcessManualPilotFrame(
        byte[] frameBytes,
        byte[] signedPolicyBytes,
        IStage2ProvenanceSigner intakeCheckpointSigner,
        DateTimeOffset nowUtc,
        Func<Stage3VerifiedPilotInstruction, Stage3LiveDeliveryResult> liveDelivery) => ProcessFrameCore(
            frameBytes, signedPolicyBytes, intakeCheckpointSigner, nowUtc,
            manualPilot: true, liveDelivery ?? throw new ArgumentNullException(nameof(liveDelivery)));

    public Stage3IntakeResult ProcessLimitedAutomaticFrame(
        byte[] frameBytes,
        byte[] signedPolicyBytes,
        IStage2ProvenanceSigner intakeCheckpointSigner,
        DateTimeOffset nowUtc,
        Func<Stage3VerifiedPilotInstruction, Stage3LiveDeliveryResult> liveDelivery) => ProcessFrameCore(
            frameBytes, signedPolicyBytes, intakeCheckpointSigner, nowUtc,
            manualPilot: false, liveDelivery ?? throw new ArgumentNullException(nameof(liveDelivery)), limitedAutomatic: true);

    private Stage3IntakeResult ProcessFrameCore(
        byte[] frameBytes,
        byte[] signedPolicyBytes,
        IStage2ProvenanceSigner intakeCheckpointSigner,
        DateTimeOffset nowUtc,
        bool manualPilot,
        Func<Stage3VerifiedPilotInstruction, Stage3LiveDeliveryResult>? liveDelivery,
        bool limitedAutomatic = false)
    {
        var policyValidation = _policyService.ValidatePinned(signedPolicyBytes, nowUtc);
        if (!policyValidation.Accepted || policyValidation.Policy is null)
            return RejectWithoutEvidence(policyValidation.ReasonCode, policyValidation.Message);
        if (_trustContext is null)
            return RejectWithoutEvidence("INSTALLATION_TRUST_REQUIRED", "A validated installation trust context is required before frame processing.");

        var policy = policyValidation.Policy;
        var configuration = policy.Configuration;
        if (frameBytes.Length == 0 || frameBytes.Length > MaximumFrameBytes)
            return Quarantine(frameBytes, configuration, "FRAME_SIZE_INVALID", "Intake frame is empty or oversized.");

        Stage3CodexIntakeFrameV1? frame;
        try
        {
            frame = JsonSerializer.Deserialize<Stage3CodexIntakeFrameV1>(frameBytes, Stage2CanonicalJson.Options);
        }
        catch (JsonException ex)
        {
            return Quarantine(frameBytes, configuration, "FRAME_JSON_INVALID", ex.Message);
        }
        if (frame is null || !frameBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(frame, Stage2CanonicalJson.Options)))
            return Quarantine(frameBytes, configuration, "FRAME_NONCANONICAL", "Intake frame is not canonical JSON.");
        if (!frame.Schema.Equals("DCS_CODEX_INTAKE_FRAME_V1", StringComparison.Ordinal) || frame.Version != 1)
            return Quarantine(frameBytes, configuration, "FRAME_SCHEMA_INVALID", "Intake-frame schema or version is invalid.");
        var expectedClassification = limitedAutomatic ? "watcher_stage4_limited_automatic" : manualPilot ? "watcher_stage3_manual_pilot" : "watcher_stage3_readiness_test";
        var expectedSenderStage = limitedAutomatic ? nameof(WatcherOperatingStage.Stage4LimitedAutomatic) : manualPilot ? nameof(WatcherOperatingStage.Stage3ManualPilot) : nameof(WatcherOperatingStage.Stage3ManualPilotReady);
        if (!frame.DeliveryClassification.Equals(expectedClassification, StringComparison.Ordinal) ||
            !frame.SenderStage.Equals(expectedSenderStage, StringComparison.Ordinal))
            return Quarantine(frameBytes, configuration, "DELIVERY_CLASSIFICATION_INVALID",
                limitedAutomatic ? "Only the explicitly authorized Stage 4 limited-automatic transport is accepted." : manualPilot ? "Only the explicit one-shot Stage 3 manual-pilot transport is accepted." : "Only Stage 3 readiness test transport is accepted.");
        if (!frame.DestinationCodexThreadId.Equals(configuration.ExpectedDirectorThreadId, StringComparison.Ordinal))
            return Quarantine(frameBytes, configuration, "DESTINATION_THREAD_MISMATCH", "Frame targets another Codex Director thread.");

        byte[] transactionBytes;
        byte[] attestationBytes;
        try
        {
            transactionBytes = Convert.FromBase64String(frame.SignedTransactionBase64);
            attestationBytes = Convert.FromBase64String(frame.BuildAttestationBase64);
        }
        catch (FormatException)
        {
            return Quarantine(frameBytes, configuration, "FRAME_BASE64_INVALID", "Frame payload encoding is invalid.");
        }
        if (!Stage2Crypto.Sha256Hex(attestationBytes).Equals(frame.BuildAttestationSha256, StringComparison.OrdinalIgnoreCase))
            return Quarantine(frameBytes, configuration, "BUILD_ATTESTATION_FRAME_HASH_MISMATCH", "Frame does not bind the exact build attestation.");
        if (!File.Exists(configuration.AllowedBuildAttestationPath) ||
            !File.ReadAllBytes(configuration.AllowedBuildAttestationPath).AsSpan().SequenceEqual(attestationBytes))
            return Quarantine(frameBytes, configuration, "BUILD_ATTESTATION_NOT_ALLOWED", "Frame attestation is not the locally allowed attestation.");

        var trustStore = new Stage3TrustStoreService(
            configuration.TrustStorePath,
            configuration.TrustRootPath,
            configuration.TrustAnchorPath,
            rootSigner: null,
            nowUtc);
        var trustValidation = trustStore.Validate(nowUtc);
        if (!trustValidation.Accepted)
            return Quarantine(frameBytes, configuration, trustValidation.ReasonCode, trustValidation.Message);
        var trustRootPin = ValidateInstallationTrustRoot(configuration.TrustRootPath, _trustContext);
        if (!trustRootPin.Accepted)
            return Quarantine(frameBytes, configuration, trustRootPin.ReasonCode, trustRootPin.Message);

        var attestationService = new Stage3BuildAttestationService();
        var allowedSourceCommits = new HashSet<string>(StringComparer.Ordinal) { policy.AllowedSourceCommit };
        var allowedCompilerIdentities = new HashSet<string>(StringComparer.Ordinal) { policy.AllowedCompilerIdentity };
        var attestationValidation = attestationService.Validate(
            attestationBytes,
            trustStore,
            nowUtc,
            minimumBuildGeneration: policy.MinimumBuildGeneration,
            allowedSourceCommits: allowedSourceCommits,
            verifyRuntimeFiles: true,
            allowedCompilerIdentities: allowedCompilerIdentities);
        if (!attestationValidation.Accepted || attestationValidation.Attestation is null)
            return Quarantine(frameBytes, configuration, attestationValidation.ReasonCode, attestationValidation.Message);
        var attestation = attestationValidation.Attestation;
        if (!attestation.IntakePolicySha256.Equals(Stage2Crypto.Sha256Hex(signedPolicyBytes), StringComparison.OrdinalIgnoreCase))
            return Quarantine(frameBytes, configuration, "INTAKE_POLICY_BUILD_BINDING_MISMATCH", "Build attestation does not bind the active signed intake policy.");
        var runtimeValidation = attestationService.ValidateExecutingRuntime(attestation, new Stage3RuntimeIdentity
        {
            Role = "codex-intake",
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            ApplicationAssemblyPath = typeof(Stage3CodexIntakeGate).Assembly.Location,
            ActiveConfigurationPath = string.Empty,
            ActiveConfigurationSha256 = Stage2Crypto.Sha256Hex(signedPolicyBytes)
        });
        if (!runtimeValidation.Accepted)
            return Quarantine(frameBytes, configuration, runtimeValidation.ReasonCode, runtimeValidation.Message);
        var dependencyValidation = attestationService.ValidateRuntimeDependencies(
            attestation,
            configuration.ConfigurationTemplatePath,
            configuration.ProvenanceSchemaPath,
            configuration.VerifierContractPath,
            configuration.ReplayContractPath);
        if (!dependencyValidation.Accepted)
            return Quarantine(frameBytes, configuration, dependencyValidation.ReasonCode, dependencyValidation.Message);
        var anchorValidation = attestationService.ValidateGenerationAnchor(
            configuration.BuildGenerationAnchorPath,
            attestationBytes,
            attestation,
            trustStore,
            nowUtc);
        if (!anchorValidation.Accepted)
            return Quarantine(frameBytes, configuration, anchorValidation.ReasonCode, anchorValidation.Message);

        var provenanceResolver = new Stage3PurposeKeyResolver(trustStore, "provenance", attestation.BuildGeneration, nowUtc);
        var stage2Verifier = new CodexStage2TestVerifier(
            provenanceResolver,
            new Stage2ReplayLedger(Path.Combine(configuration.QuarantineDirectory, "unused-stage2-ledger.json")),
            configuration.ExpectedDirectorThreadId);
        var cryptographic = stage2Verifier.VerifyWithoutReplayCommit(transactionBytes, nowUtc);
        if (!cryptographic.Accepted)
            return Quarantine(frameBytes, configuration, cryptographic.ReasonCode, cryptographic.Message);

        var transactionParse = Stage2CanonicalJson.ParseTransaction(transactionBytes);
        if (!transactionParse.Success || transactionParse.Transaction is null)
            return Quarantine(frameBytes, configuration, transactionParse.ReasonCode, transactionParse.Message);
        var transaction = transactionParse.Transaction;
        var provenance = transaction.Provenance;

        var provenanceTrust = trustStore.EvaluateKey(provenance.SignerKeyId, "provenance", attestation.BuildGeneration, nowUtc);
        if (!provenanceTrust.Accepted || provenanceTrust.Key is null)
            return Quarantine(frameBytes, configuration, provenanceTrust.ReasonCode, provenanceTrust.Message);
        if (!provenanceTrust.Key.PublicKeyFingerprintSha256.Equals(attestation.ProvenanceSignerPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            return Quarantine(frameBytes, configuration, "PROVENANCE_KEY_BUILD_BINDING_MISMATCH", "Build attestation names another provenance signer fingerprint.");
        if (!provenance.WatcherSourceCommit.Equals(attestation.SourceCommit, StringComparison.Ordinal) ||
            !provenance.WatcherSourceTreeSha256.Equals(attestation.SourceTreeSha256, StringComparison.OrdinalIgnoreCase) ||
            !provenance.WatcherExecutableSha256.Equals(attestation.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !provenance.WatcherConfigurationSha256.Equals(attestation.ConfigurationTemplateSha256, StringComparison.OrdinalIgnoreCase))
            return Quarantine(frameBytes, configuration, "PROVENANCE_BUILD_IDENTITY_MISMATCH", "Provenance build identity does not match trusted attestation.");

        var outboundLedgerResolver = new Stage3PurposeKeyResolver(trustStore, "outbound-ledger-checkpoint", attestation.BuildGeneration, nowUtc);
        var outbound = new Stage3ReplayLedgerV2Service(
            new Stage3LedgerIdentity
            {
                LedgerRole = "watcher-outbound",
                LedgerInstanceId = configuration.OutboundLedgerInstanceId,
                MutexName = configuration.OutboundLedgerMutexName,
                LedgerDirectory = configuration.OutboundLedgerDirectory,
                AnchorDirectory = configuration.OutboundLedgerAnchorDirectory
            },
            intakeCheckpointSigner,
            outboundLedgerResolver,
            TimeSpan.FromMilliseconds(configuration.LockTimeoutMilliseconds),
            allowLiveManualPilot: manualPilot || limitedAutomatic);
        var outboundState = outbound.GetMatchingTransactionState(provenance, nowUtc);
        if (!outboundState.Accepted)
            return Quarantine(frameBytes, configuration, outboundState.ReasonCode, outboundState.Message);
        var requiredOutboundState = manualPilot || limitedAutomatic ? Stage3TransactionStates.LiveDeliveryPending : Stage3TransactionStates.TestSinkSent;
        if (!outboundState.Disposition.Equals(requiredOutboundState, StringComparison.Ordinal))
            return Quarantine(frameBytes, configuration, "OUTBOUND_STATE_INVALID", $"Outbound transaction has not reached {requiredOutboundState}.");

        var intakeLedgerResolver = new Stage3PurposeKeyResolver(trustStore, "intake-ledger-checkpoint", attestation.BuildGeneration, nowUtc);
        var intake = new Stage3ReplayLedgerV2Service(
            new Stage3LedgerIdentity
            {
                LedgerRole = "codex-intake",
                LedgerInstanceId = configuration.IntakeLedgerInstanceId,
                MutexName = configuration.IntakeLedgerMutexName,
                LedgerDirectory = configuration.IntakeLedgerDirectory,
                AnchorDirectory = configuration.IntakeLedgerAnchorDirectory
            },
            intakeCheckpointSigner,
            intakeLedgerResolver,
            TimeSpan.FromMilliseconds(configuration.LockTimeoutMilliseconds),
            allowLiveManualPilot: manualPilot || limitedAutomatic);
        var intakeStartup = intake.ValidateOnly(nowUtc);
        if (!intakeStartup.Accepted)
            return Quarantine(frameBytes, configuration, intakeStartup.ReasonCode, intakeStartup.Message);

        var reserve = intake.Reserve(provenance, nowUtc);
        if (!reserve.Accepted)
            return Quarantine(frameBytes, configuration, reserve.ReasonCode, reserve.Message);
        var intakeStates = manualPilot || limitedAutomatic
            ? new[] { Stage3TransactionStates.Validated, Stage3TransactionStates.Signed, Stage3TransactionStates.Serialized, Stage3TransactionStates.LiveDeliveryPending }
            : new[] { Stage3TransactionStates.Validated, Stage3TransactionStates.Signed, Stage3TransactionStates.Serialized, Stage3TransactionStates.TestSinkSent };
        foreach (var state in intakeStates)
        {
            var transition = intake.Transition(provenance.TransactionId, state, nowUtc,
                incrementAttempt: state is Stage3TransactionStates.TestSinkSent or Stage3TransactionStates.LiveDeliveryPending);
            if (!transition.Accepted)
                return Quarantine(frameBytes, configuration, transition.ReasonCode, transition.Message, provenance.TransactionId, provenance.TaskId);
        }

        byte[] envelopeBytes;
        try { envelopeBytes = Convert.FromBase64String(transaction.EnvelopeBase64); }
        catch (FormatException) { return Quarantine(frameBytes, configuration, "ENVELOPE_ENCODING_INVALID", "Envelope encoding changed after verification."); }
        if (manualPilot || limitedAutomatic)
        {
            var provenanceSha256 = Stage2Crypto.Sha256Hex(Stage2CanonicalJson.SerializeSignedProvenance(provenance));
            Stage3LiveDeliveryResult delivered;
            try
            {
                delivered = liveDelivery!(new Stage3VerifiedPilotInstruction(
                    envelopeBytes,
                    provenance,
                    provenanceSha256,
                    provenanceTrust.Key.PublicKeyFingerprintSha256));
            }
            catch (Exception ex)
            {
                delivered = new Stage3LiveDeliveryResult(false, "LIVE_DELIVERY_CALLBACK_FAILED", ex.Message);
            }
            if (!delivered.Accepted)
            {
                _ = intake.Transition(provenance.TransactionId, Stage3TransactionStates.RecoveryRequired, nowUtc);
                return Quarantine(frameBytes, configuration, delivered.ReasonCode, delivered.Message, provenance.TransactionId, provenance.TaskId);
            }
            var liveAccepted = intake.Transition(provenance.TransactionId, Stage3TransactionStates.LiveDelivered, nowUtc);
            if (!liveAccepted.Accepted)
                return Quarantine(frameBytes, configuration, liveAccepted.ReasonCode, liveAccepted.Message, provenance.TransactionId, provenance.TaskId);
            return new Stage3IntakeResult
            {
                Disposition = "ACCEPTED_FOR_LIVE_CODEX",
                ReasonCode = "OK",
                Message = limitedAutomatic
                    ? "The independently verified limited-automatic transaction reached the configured Codex thread."
                    : "The independently verified one-shot pilot transaction reached the configured Codex thread.",
                TransactionId = provenance.TransactionId,
                TaskId = provenance.TaskId,
                EnvelopeSha256 = provenance.EnvelopeSha256,
                ProvenanceSha256 = provenanceSha256,
                SignerFingerprint = provenanceTrust.Key.PublicKeyFingerprintSha256,
                DestinationCodexThreadId = provenance.DestinationCodexThreadId,
                CodexTurnId = delivered.TurnId,
                ActionableInstructionExposed = true
            };
        }

        string testSinkPath;
        try
        {
            testSinkPath = CommitTestSinkEnvelope(configuration.TestSinkDirectory, provenance.TransactionId, envelopeBytes);
        }
        catch (Exception ex)
        {
            return Quarantine(frameBytes, configuration, "TEST_SINK_COMMIT_FAILED", ex.Message, provenance.TransactionId, provenance.TaskId);
        }
        var accepted = intake.Transition(provenance.TransactionId, Stage3TransactionStates.TestSinkAccepted, nowUtc);
        if (!accepted.Accepted)
            return Quarantine(frameBytes, configuration, accepted.ReasonCode, accepted.Message, provenance.TransactionId, provenance.TaskId);
        return new Stage3IntakeResult
        {
            Disposition = "ACCEPTED_FOR_TEST_SINK",
            ReasonCode = "OK",
            Message = "Cross-process frame passed trust, build, provenance, outbound, intake, and replay gates.",
            TransactionId = provenance.TransactionId,
            TaskId = provenance.TaskId,
            TestSinkPath = testSinkPath,
            ActionableInstructionExposed = false
        };
    }

    private static Stage3LedgerResult ValidateInstallationTrustRoot(
        string trustRootPath,
        InstallationTrustContext installationTrust)
    {
        Stage3TrustRootPublicV1? root;
        try
        {
            var bytes = File.ReadAllBytes(trustRootPath);
            root = JsonSerializer.Deserialize<Stage3TrustRootPublicV1>(bytes, Stage2CanonicalJson.Options);
            if (root is null || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(root, Stage2CanonicalJson.Options)))
                return new Stage3LedgerResult(false, "TRUST_ROOT_NONCANONICAL", "Trust-root public record is invalid or noncanonical.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Stage3LedgerResult(false, "TRUST_ROOT_READ_FAILED", ex.Message);
        }

        if (!root.PublicKeyFingerprintSha256.Equals(installationTrust.TrustRootFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            return new Stage3LedgerResult(false, "TRUST_ROOT_PIN_MISMATCH", "Active trust root differs from the validated installation trust binding.");
        try
        {
            if (!Stage2Crypto.Sha256Hex(Convert.FromBase64String(root.PublicKeySpkiBase64))
                    .Equals(root.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
                return new Stage3LedgerResult(false, "TRUST_ROOT_FINGERPRINT_INVALID", "Trust-root public-key bytes do not match the declared fingerprint.");
        }
        catch (FormatException)
        {
            return new Stage3LedgerResult(false, "TRUST_ROOT_PUBLIC_KEY_INVALID", "Trust-root public-key encoding is invalid.");
        }
        return new Stage3LedgerResult(true, "OK", "Trust root matches validated installation trust.");
    }

    private static string CommitTestSinkEnvelope(string directory, string transactionId, byte[] envelopeBytes)
    {
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"{transactionId}.test-only.envelope.txt");
        var pendingPath = Path.Combine(directory, $".{transactionId}.{Guid.NewGuid():N}.pending");
        using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(envelopeBytes);
            stream.Flush(flushToDisk: true);
        }
        if (!File.ReadAllBytes(pendingPath).AsSpan().SequenceEqual(envelopeBytes))
            throw new IOException("Pending test-sink envelope failed durable reread verification.");
        File.Move(pendingPath, finalPath, overwrite: false);
        if (!File.ReadAllBytes(finalPath).AsSpan().SequenceEqual(envelopeBytes))
            throw new IOException("Committed test-sink envelope failed reread verification.");
        return finalPath;
    }

    private static Stage3IntakeResult RejectWithoutEvidence(string reasonCode, string message) => new()
    {
        Disposition = "REJECTED",
        ReasonCode = reasonCode,
        Message = message,
        ActionableInstructionExposed = false
    };

    private static Stage3IntakeResult Quarantine(
        byte[] frameBytes,
        Stage3CodexIntakeConfiguration configuration,
        string reasonCode,
        string message,
        string transactionId = "",
        string taskId = "")
    {
        Directory.CreateDirectory(configuration.QuarantineDirectory);
        var id = Guid.NewGuid().ToString("N");
        var evidencePath = Path.Combine(configuration.QuarantineDirectory, $"rejected-{id}.frame.bin");
        Stage2AtomicFile.WriteAllBytes(evidencePath, frameBytes);
        var result = new Stage3IntakeResult
        {
            Disposition = "REJECTED",
            ReasonCode = reasonCode,
            Message = message,
            TransactionId = transactionId,
            TaskId = taskId,
            EvidencePath = evidencePath,
            ActionableInstructionExposed = false
        };
        Stage2AtomicFile.WriteAllBytes(
            Path.Combine(configuration.QuarantineDirectory, $"rejected-{id}.result.json"),
            JsonSerializer.SerializeToUtf8Bytes(result, Stage2CanonicalJson.Options));
        return result;
    }

    public static byte[] SerializeFrame(Stage3CodexIntakeFrameV1 frame) =>
        JsonSerializer.SerializeToUtf8Bytes(frame, Stage2CanonicalJson.Options);
}
