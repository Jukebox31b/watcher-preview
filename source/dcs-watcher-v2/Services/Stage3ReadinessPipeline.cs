using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3ReadinessPipeline
{
    private readonly Stage2DryRunPipeline _stage2;
    private readonly Stage3ReplayLedgerV2Service _outboundLedger;
    private readonly Stage3BuildAttestationService _attestations;
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly InstallationTrustContext _trustContext;

    public Stage3ReadinessPipeline(
        Stage2DryRunPipeline stage2,
        Stage3ReplayLedgerV2Service outboundLedger,
        Stage3BuildAttestationService attestations,
        AppConfig config,
        ConfigService configService,
        InstallationTrustContext trustContext)
    {
        _stage2 = stage2;
        _outboundLedger = outboundLedger;
        _attestations = attestations;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _trustContext = trustContext ?? throw new ArgumentNullException(nameof(trustContext));
    }

    public Stage3ReadinessPipelineResult BuildOfflineTestFrame(
        string provenanceSchemaPath,
        string verifierContractPath,
        string replayContractPath,
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string destinationThreadId,
        string framePath,
        DateTimeOffset nowUtc) => BuildFrame(
            provenanceSchemaPath, verifierContractPath, replayContractPath, wake, snapshot, response,
            destinationThreadId, framePath, nowUtc, manualPilot: false);

    public Stage3ReadinessPipelineResult BuildManualPilotFrame(
        string provenanceSchemaPath,
        string verifierContractPath,
        string replayContractPath,
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string destinationThreadId,
        string framePath,
        DateTimeOffset nowUtc) => BuildFrame(
            provenanceSchemaPath, verifierContractPath, replayContractPath, wake, snapshot, response,
            destinationThreadId, framePath, nowUtc, manualPilot: true);

    public Stage3ReadinessPipelineResult BuildLimitedAutomaticFrame(
        string provenanceSchemaPath,
        string verifierContractPath,
        string replayContractPath,
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string destinationThreadId,
        string framePath,
        DateTimeOffset nowUtc) => BuildFrame(
            provenanceSchemaPath, verifierContractPath, replayContractPath, wake, snapshot, response,
            destinationThreadId, framePath, nowUtc, manualPilot: false, limitedAutomatic: true);

    private Stage3ReadinessPipelineResult BuildFrame(
        string provenanceSchemaPath,
        string verifierContractPath,
        string replayContractPath,
        WakeTransactionRecord wake,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        string destinationThreadId,
        string framePath,
        DateTimeOffset nowUtc,
        bool manualPilot,
        bool limitedAutomatic = false)
    {
        var config = _config;
        var configurationPath = _configService.GetConfigPath(config);
        string safetyReason;
        var authorized = limitedAutomatic
            ? WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(config, out safetyReason)
            : manualPilot
            ? WatcherSafetyPolicy.CanRunStage3ManualPilot(config, out safetyReason)
            : WatcherSafetyPolicy.CanRunStage3Readiness(config, out safetyReason);
        if (!authorized)
            return Reject(limitedAutomatic ? "STAGE4_LIMITED_AUTOMATIC_NOT_AUTHORIZED" : manualPilot ? "STAGE3_MANUAL_PILOT_NOT_AUTHORIZED" : "STAGE3_READINESS_NOT_AUTHORIZED", safetyReason);

        if (!File.Exists(configurationPath) || !File.Exists(config.Stage3IntakePolicyPath) ||
            !File.Exists(config.Stage3BuildAttestationPath) || !File.Exists(config.Stage3BuildGenerationAnchorPath) ||
            !File.Exists(config.Stage3TrustStorePath) || !File.Exists(config.Stage3TrustRootPath) || !File.Exists(config.Stage3TrustAnchorPath))
            return Reject("STAGE3_SECURITY_STATE_MISSING", "Active Stage 3 configuration does not name a complete local security state.");
        var policyBytes = File.ReadAllBytes(config.Stage3IntakePolicyPath);
        var policyValidation = new Stage3IntakePolicyService(_trustContext).ValidatePinned(policyBytes, nowUtc);
        if (!policyValidation.Accepted || policyValidation.Policy is null)
            return Reject(policyValidation.ReasonCode, policyValidation.Message);
        var policy = policyValidation.Policy;
        if (!SamePath(policy.Configuration.ConfigurationTemplatePath, configurationPath) ||
            !SamePath(policy.Configuration.AllowedBuildAttestationPath, config.Stage3BuildAttestationPath) ||
            !SamePath(policy.Configuration.BuildGenerationAnchorPath, config.Stage3BuildGenerationAnchorPath) ||
            !SamePath(policy.Configuration.TrustStorePath, config.Stage3TrustStorePath) ||
            !SamePath(policy.Configuration.TrustRootPath, config.Stage3TrustRootPath) ||
            !SamePath(policy.Configuration.TrustAnchorPath, config.Stage3TrustAnchorPath))
            return Reject("ACTIVE_POLICY_CONFIGURATION_MISMATCH", "Active Watcher configuration does not match the pinned signed intake policy.");
        if (!_outboundLedger.LedgerRole.Equals("watcher-outbound", StringComparison.Ordinal) ||
            !_outboundLedger.LedgerInstanceId.Equals(policy.Configuration.OutboundLedgerInstanceId, StringComparison.Ordinal) ||
            !_outboundLedger.MutexName.Equals(policy.Configuration.OutboundLedgerMutexName, StringComparison.Ordinal) ||
            !SamePath(Path.GetDirectoryName(_outboundLedger.LedgerPath)!, policy.Configuration.OutboundLedgerDirectory) ||
            !SamePath(Path.GetDirectoryName(_outboundLedger.AnchorPath)!, policy.Configuration.OutboundLedgerAnchorDirectory))
            return Reject("OUTBOUND_LEDGER_POLICY_MISMATCH", "Watcher outbound ledger does not match the signed intake policy.");

        var buildAttestationBytes = File.ReadAllBytes(config.Stage3BuildAttestationPath);
        var trustStore = new Stage3TrustStoreService(config.Stage3TrustStorePath, config.Stage3TrustRootPath,
            config.Stage3TrustAnchorPath, rootSigner: null, evaluationTimeUtc: nowUtc);

        var attestation = _attestations.Validate(buildAttestationBytes, trustStore, nowUtc,
            minimumBuildGeneration: policy.MinimumBuildGeneration,
            allowedSourceCommits: new HashSet<string>(StringComparer.Ordinal) { policy.AllowedSourceCommit },
            verifyRuntimeFiles: true,
            allowedCompilerIdentities: new HashSet<string>(StringComparer.Ordinal) { policy.AllowedCompilerIdentity });
        if (!attestation.Accepted || attestation.Attestation is null)
            return Reject(attestation.ReasonCode, attestation.Message);
        if (!attestation.Attestation.IntakePolicySha256.Equals(Stage2Crypto.Sha256Hex(policyBytes), StringComparison.OrdinalIgnoreCase))
            return Reject("INTAKE_POLICY_BUILD_BINDING_MISMATCH", "Build attestation does not bind the active signed intake policy.");
        if (!_stage2.SignerPublicKeyFingerprintSha256.Equals(attestation.Attestation.ProvenanceSignerPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            return Reject("PROVENANCE_SIGNER_BUILD_BINDING_MISMATCH", "Watcher signing key does not match the signed build attestation.");
        var executingRuntime = _attestations.ValidateExecutingRuntime(attestation.Attestation, new Stage3RuntimeIdentity
        {
            Role = "watcher",
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            ApplicationAssemblyPath = typeof(Stage3ReadinessPipeline).Assembly.Location,
            ActiveConfigurationPath = configurationPath
        });
        if (!executingRuntime.Accepted)
            return Reject(executingRuntime.ReasonCode, executingRuntime.Message);
        var dependencies = _attestations.ValidateRuntimeDependencies(attestation.Attestation, configurationPath,
            provenanceSchemaPath, verifierContractPath, replayContractPath);
        if (!dependencies.Accepted) return Reject(dependencies.ReasonCode, dependencies.Message);
        var anchor = _attestations.ValidateGenerationAnchor(config.Stage3BuildGenerationAnchorPath, buildAttestationBytes,
            attestation.Attestation, trustStore, nowUtc);
        if (!anchor.Accepted) return Reject(anchor.ReasonCode, anchor.Message);
        var buildIdentity = new Stage2BuildIdentity
        {
            SourceCommit = attestation.Attestation.SourceCommit,
            SourceTreeSha256 = attestation.Attestation.SourceTreeSha256,
            ExecutableSha256 = attestation.Attestation.ExecutableSha256,
            ConfigurationSha256 = attestation.Attestation.ConfigurationTemplateSha256
        };
        if (!buildIdentity.SourceCommit.Equals(attestation.Attestation.SourceCommit, StringComparison.Ordinal) ||
            !buildIdentity.SourceTreeSha256.Equals(attestation.Attestation.SourceTreeSha256, StringComparison.OrdinalIgnoreCase) ||
            !buildIdentity.ExecutableSha256.Equals(attestation.Attestation.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !buildIdentity.ConfigurationSha256.Equals(attestation.Attestation.ConfigurationTemplateSha256, StringComparison.OrdinalIgnoreCase))
            return Reject("RUNTIME_BUILD_IDENTITY_MISMATCH", "Runtime build identity differs from the trusted attestation.");

        var ledgerStartup = _outboundLedger.ValidateOnly(nowUtc);
        if (!ledgerStartup.Accepted) return Reject(ledgerStartup.ReasonCode, ledgerStartup.Message);
        var lifecycle = new LedgerLifecycle(_outboundLedger);
        var stage2 = _stage2.BuildSignedDryRunTransaction(wake, snapshot, response, destinationThreadId,
            buildIdentity, nowUtc, signingLifecycle: lifecycle);
        if (!stage2.Success || stage2.PayloadBytes is null || stage2.Provenance is null)
            return Reject(stage2.ReasonCode, stage2.Message);

        var frame = new Stage3CodexIntakeFrameV1
        {
            DeliveryClassification = limitedAutomatic ? "watcher_stage4_limited_automatic" : manualPilot ? "watcher_stage3_manual_pilot" : "watcher_stage3_readiness_test",
            SignedTransactionBase64 = Convert.ToBase64String(stage2.PayloadBytes),
            BuildAttestationBase64 = Convert.ToBase64String(buildAttestationBytes),
            BuildAttestationSha256 = Stage2Crypto.Sha256Hex(buildAttestationBytes),
            DestinationCodexThreadId = destinationThreadId,
            SenderProcessId = Environment.ProcessId,
            SenderStage = limitedAutomatic ? nameof(WatcherOperatingStage.Stage4LimitedAutomatic) : manualPilot ? nameof(WatcherOperatingStage.Stage3ManualPilot) : nameof(WatcherOperatingStage.Stage3ManualPilotReady),
            IssuedAtUtc = nowUtc.ToUniversalTime().ToString("O")
        };
        var frameBytes = Stage3CodexIntakeGate.SerializeFrame(frame);
        Directory.CreateDirectory(Path.GetDirectoryName(framePath)!);
        Stage2AtomicFile.WriteAllBytes(framePath, frameBytes);
        var outboundState = manualPilot || limitedAutomatic ? Stage3TransactionStates.LiveDeliveryPending : Stage3TransactionStates.TestSinkSent;
        var sent = _outboundLedger.Transition(stage2.Provenance.TransactionId, outboundState,
            nowUtc, incrementAttempt: true);
        if (!sent.Accepted) return Reject(sent.ReasonCode, sent.Message);
        return new Stage3ReadinessPipelineResult(true, "OK",
            manualPilot || limitedAutomatic
                ? "Runtime-attested transaction passed Watcher verification and is pending one verified live-intake attempt."
                : "Runtime-attested transaction was reserved before signing and serialized only for the isolated test sink.",
            frameBytes, stage2.Provenance, framePath);
    }

    private static Stage3ReadinessPipelineResult Reject(string code, string message) => new(false, code, message);

    private static bool SamePath(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed class LedgerLifecycle : IStage2SigningLifecycle
    {
        private readonly Stage3ReplayLedgerV2Service _ledger;
        public LedgerLifecycle(Stage3ReplayLedgerV2Service ledger) => _ledger = ledger;

        public Stage2PipelineResult? BeforeSigning(Stage2InstructionProvenanceV1 provenance, DateTimeOffset nowUtc)
        {
            var reserve = _ledger.Reserve(provenance, nowUtc);
            if (!reserve.Accepted) return Reject(reserve);
            var validate = _ledger.Transition(provenance.TransactionId, Stage3TransactionStates.Validated, nowUtc);
            return validate.Accepted ? null : Reject(validate);
        }

        public Stage2PipelineResult? AfterSigning(Stage2InstructionProvenanceV1 provenance, DateTimeOffset nowUtc)
        {
            var result = _ledger.Transition(provenance.TransactionId, Stage3TransactionStates.Signed, nowUtc);
            return result.Accepted ? null : Reject(result);
        }

        public Stage2PipelineResult? AfterSerialization(Stage2InstructionProvenanceV1 provenance, byte[] transactionBytes, DateTimeOffset nowUtc)
        {
            _ = transactionBytes;
            var result = _ledger.Transition(provenance.TransactionId, Stage3TransactionStates.Serialized, nowUtc);
            return result.Accepted ? null : Reject(result);
        }

        private static Stage2PipelineResult Reject(Stage3LedgerResult result) =>
            new(false, result.ReasonCode, result.Message);
    }
}
