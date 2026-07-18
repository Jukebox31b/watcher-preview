using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;

namespace DcsWatcherV2.Services;

internal sealed class Stage3RegressionFixture : IDisposable
{
    public const string ThreadId = "stage3-synthetic-codex-thread";
    public const string SourceReport = "CGPT-REPORT-20260716-170000-stage3-offline-fixture.md";

    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _cngKeyNames = [];
    private readonly List<(string Purpose, string InstanceId, string AnchorPath)> _externalCounters = [];

    public Stage3RegressionFixture(string? root = null, bool cngIntakeSigner = false, string? intakeExecutablePath = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "DcsWatcherV2-Stage3-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
        NowUtc = DateTimeOffset.UtcNow;

        InstallationTrustService = new InstallationTrustAnchorService();
        InstallationSecurityRoot = Path.Combine(Root, "installation-security");
        var installation = InstallationTrustService.Provision(new InstallationTrustProvisioningOptions
        {
            SecurityRoot = InstallationSecurityRoot,
            DestinationId = ThreadId,
            NowUtc = NowUtc
        });
        if (!installation.Accepted || installation.Context is null)
            throw new InvalidOperationException($"{installation.ReasonCode}: {installation.Message}");
        InstallationTrust = installation.Context;

        ProvenanceSigner = Own(new EphemeralStage2ProvenanceSigner("stage3-test-provenance"));
        BuildSigner = Own(new EphemeralStage2ProvenanceSigner("stage3-test-build"));
        RootSigner = Own(InstallationTrustService.OpenTrustRootSigner(InstallationTrust));
        IntakePolicySigner = Own(InstallationTrustService.OpenActivePolicySigner(InstallationTrust));
        if (cngIntakeSigner)
        {
            OutboundCheckpointCngKeyName = "DCS-Watcher-Stage3-Test-Outbound-" + Guid.NewGuid().ToString("N");
            _cngKeyNames.Add(OutboundCheckpointCngKeyName);
            OutboundCheckpointSigner = Own(WindowsCngStage2ProvenanceSigner.OpenOrCreate("stage3-test-outbound-checkpoint", OutboundCheckpointCngKeyName));
            IntakeCheckpointCngKeyName = "DCS-Watcher-Stage3-Test-" + Guid.NewGuid().ToString("N");
            _cngKeyNames.Add(IntakeCheckpointCngKeyName);
            IntakeCheckpointSigner = Own(WindowsCngStage2ProvenanceSigner.OpenOrCreate("stage3-test-intake-checkpoint", IntakeCheckpointCngKeyName));
        }
        else
        {
            OutboundCheckpointSigner = Own(new EphemeralStage2ProvenanceSigner("stage3-test-outbound-checkpoint"));
            IntakeCheckpointCngKeyName = "DCS-Watcher-Stage3-Test-" + Guid.NewGuid().ToString("N");
            _cngKeyNames.Add(IntakeCheckpointCngKeyName);
            IntakeCheckpointSigner = Own(WindowsCngStage2ProvenanceSigner.OpenOrCreate("stage3-test-intake-checkpoint", IntakeCheckpointCngKeyName));
        }

        TrustStorePath = Path.Combine(Root, "trust", "trust-store.json");
        TrustRootPath = Path.Combine(Root, "trust", "trust-root.json");
        TrustAnchorPath = Path.Combine(Root, "trust-anchor", "trust-anchor.json");
        TrustStoreInstanceId = "stage3-test-trust-" + Guid.NewGuid().ToString("N");
        _externalCounters.Add(("trust-store", TrustStoreInstanceId, TrustAnchorPath));
        TrustStore = new Stage3TrustStoreService(TrustStorePath, TrustRootPath, TrustAnchorPath, RootSigner, NowUtc);
        Assert(TrustStore.Initialize(TrustStoreInstanceId, new[]
        {
            Trusted(ProvenanceSigner, "provenance"),
            Trusted(BuildSigner, "build-attestation"),
            Trusted(OutboundCheckpointSigner, "outbound-ledger-checkpoint"),
            Trusted(IntakeCheckpointSigner, "intake-ledger-checkpoint")
        }, NowUtc));

        ConfigurationPath = Write("runtime/config-template.json", "{\"operating_stage\":\"Stage3ManualPilotReady\"}");
        ProvenanceSchemaPath = Write("runtime/provenance-schema.json", "{\"schema\":\"DCS_WATCHER_INSTRUCTION_PROVENANCE_V1\"}");
        VerifierContractPath = Write("runtime/verifier-contract.md", "Stage 3 offline verifier contract");
        ReplayContractPath = Write("runtime/replay-contract.json", "{\"schema\":\"DCS_WATCHER_REPLAY_LEDGER_V2\"}");
        ExecutablePath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Regression executable path is unavailable."));
        ApplicationDllPath = Path.GetFullPath(typeof(Stage3ReadinessPipeline).Assembly.Location);
        IntakeExecutablePath = Path.GetFullPath(intakeExecutablePath ?? ExecutablePath);
        IntakeApplicationDllPath = intakeExecutablePath is null
            ? ApplicationDllPath
            : Path.Combine(Path.GetDirectoryName(IntakeExecutablePath)!, Path.GetFileName(ApplicationDllPath));
        if (!File.Exists(IntakeExecutablePath) || !File.Exists(IntakeApplicationDllPath))
            throw new FileNotFoundException("The attested intake executable or application assembly is missing.");
        SupportingDllPath = Write("runtime/DcsWatcherV2.Security.dll", "synthetic-stage3-supporting-dll");
        AttestationPath = Path.Combine(Root, "attestation", "build-attestation.json");
        BuildGenerationAnchorPath = Path.Combine(Root, "build-anchor", "build-generation.anchor.json");
        _externalCounters.Add(("build-attestation", "DCS-WATCHER-BUILD", BuildGenerationAnchorPath));
        OutboundIdentity = Identity("watcher-outbound", "stage3-test-outbound", "outbound");
        IntakeIdentity = Identity("codex-intake", "stage3-test-intake", "intake");
        IntakeConfig = new Stage3CodexIntakeConfiguration
        {
            ExpectedDirectorThreadId = ThreadId,
            TrustStorePath = TrustStorePath,
            TrustRootPath = TrustRootPath,
            TrustAnchorPath = TrustAnchorPath,
            AllowedBuildAttestationPath = AttestationPath,
            BuildGenerationAnchorPath = BuildGenerationAnchorPath,
            ConfigurationTemplatePath = ConfigurationPath,
            ProvenanceSchemaPath = ProvenanceSchemaPath,
            VerifierContractPath = VerifierContractPath,
            ReplayContractPath = ReplayContractPath,
            OutboundLedgerDirectory = OutboundIdentity.LedgerDirectory,
            OutboundLedgerInstanceId = OutboundIdentity.LedgerInstanceId,
            OutboundLedgerMutexName = OutboundIdentity.MutexName,
            OutboundLedgerAnchorDirectory = OutboundIdentity.AnchorDirectory,
            IntakeLedgerDirectory = IntakeIdentity.LedgerDirectory,
            IntakeLedgerInstanceId = IntakeIdentity.LedgerInstanceId,
            IntakeLedgerMutexName = IntakeIdentity.MutexName,
            IntakeLedgerAnchorDirectory = IntakeIdentity.AnchorDirectory,
            IntakeCheckpointSignerKeyId = IntakeCheckpointSigner.KeyId,
            IntakeCheckpointCngKeyName = IntakeCheckpointCngKeyName,
            QuarantineDirectory = Path.Combine(Root, "quarantine"),
            TestSinkDirectory = Path.Combine(Root, "test-sink"),
            LockTimeoutMilliseconds = 3000
        };
        IntakePolicy = new Stage3CodexIntakePolicyV1
        {
            PolicyGeneration = 1,
            ExpectedTrustRootFingerprintSha256 = InstallationTrust.TrustRootFingerprintSha256,
            MinimumBuildGeneration = 1,
            AllowedSourceCommit = new string('a', 40),
            AllowedCompilerIdentity = "Roslyn net8.0 synthetic fixture",
            IssueTimeUtc = NowUtc.AddMinutes(-1).ToUniversalTime().ToString("O"),
            ExpiryTimeUtc = NowUtc.AddDays(1).ToUniversalTime().ToString("O"),
            Configuration = IntakeConfig
        };
        IntakePolicyService = Stage3IntakePolicyService.CreateOfflineRegression(Root, InstallationTrust);
        IntakeGate = new Stage3CodexIntakeGate(IntakePolicyService, InstallationTrust);
        IntakePolicyBytes = IntakePolicyService.Sign(IntakePolicy, IntakePolicySigner);
        var policyActivation = IntakePolicyService.ActivatePinnedPolicy(IntakePolicyBytes, NowUtc);
        if (!policyActivation.Accepted)
            throw new InvalidOperationException($"{policyActivation.ReasonCode}: {policyActivation.Message}");
        IntakePolicyPath = Path.Combine(Root, "policy", "signed-intake-policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(IntakePolicyPath)!);
        File.WriteAllBytes(IntakePolicyPath, IntakePolicyBytes);

        Attestation = CreateAttestation();
        AttestationBytes = new Stage3BuildAttestationService().Sign(Attestation, BuildSigner);
        Directory.CreateDirectory(Path.GetDirectoryName(AttestationPath)!);
        File.WriteAllBytes(AttestationPath, AttestationBytes);
        new Stage3BuildAttestationService().WriteGenerationAnchor(BuildGenerationAnchorPath, AttestationBytes, Attestation, BuildSigner, NowUtc);

        TransactionBytes = CreateSignedTransaction();
        Transaction = Stage2CanonicalJson.ParseTransaction(TransactionBytes).Transaction
            ?? throw new InvalidOperationException("Synthetic Stage 3 transaction did not parse.");
        ProvenancePath = Path.Combine(Root, "transport", "provenance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ProvenancePath)!);
        File.WriteAllBytes(ProvenancePath, Stage2CanonicalJson.SerializeSignedProvenance(Transaction.Provenance));

        OutboundLedger = new Stage3ReplayLedgerV2Service(OutboundIdentity, OutboundCheckpointSigner,
            new Stage3PurposeKeyResolver(TrustStore, "outbound-ledger-checkpoint", 1, NowUtc));
        IntakeLedger = new Stage3ReplayLedgerV2Service(IntakeIdentity, IntakeCheckpointSigner,
            new Stage3PurposeKeyResolver(TrustStore, "intake-ledger-checkpoint", 1, NowUtc));
        Assert(OutboundLedger.Initialize(NowUtc));
        Assert(IntakeLedger.Initialize(NowUtc));
    }

    public string Root { get; }
    public DateTimeOffset NowUtc { get; }
    public InstallationTrustAnchorService InstallationTrustService { get; }
    public string InstallationSecurityRoot { get; }
    public InstallationTrustContext InstallationTrust { get; }
    public IStage2ProvenanceSigner ProvenanceSigner { get; }
    public IStage2ProvenanceSigner BuildSigner { get; }
    public IStage2ProvenanceSigner RootSigner { get; }
    public IStage2ProvenanceSigner IntakePolicySigner { get; }
    public IStage2ProvenanceSigner OutboundCheckpointSigner { get; }
    public IStage2ProvenanceSigner IntakeCheckpointSigner { get; }
    public string OutboundCheckpointCngKeyName { get; } = string.Empty;
    public string IntakeCheckpointCngKeyName { get; } = string.Empty;
    public string TrustStorePath { get; }
    public string TrustRootPath { get; }
    public string TrustAnchorPath { get; }
    public string TrustStoreInstanceId { get; }
    public Stage3TrustStoreService TrustStore { get; }
    public string ConfigurationPath { get; }
    public string ProvenanceSchemaPath { get; }
    public string VerifierContractPath { get; }
    public string ReplayContractPath { get; }
    public string ExecutablePath { get; }
    public string ApplicationDllPath { get; }
    public string IntakeExecutablePath { get; }
    public string IntakeApplicationDllPath { get; }
    public string SupportingDllPath { get; }
    public Stage3BuildAttestationV1 Attestation { get; }
    public byte[] AttestationBytes { get; }
    public string AttestationPath { get; }
    public string BuildGenerationAnchorPath { get; }
    public byte[] TransactionBytes { get; }
    public Stage2BoundInstructionTransactionV1 Transaction { get; }
    public string ProvenancePath { get; }
    public Stage3LedgerIdentity OutboundIdentity { get; }
    public Stage3LedgerIdentity IntakeIdentity { get; }
    public Stage3ReplayLedgerV2Service OutboundLedger { get; }
    public Stage3ReplayLedgerV2Service IntakeLedger { get; }
    public Stage3CodexIntakeConfiguration IntakeConfig { get; }
    public Stage3CodexIntakePolicyV1 IntakePolicy { get; }
    public Stage3IntakePolicyService IntakePolicyService { get; }
    public Stage3CodexIntakeGate IntakeGate { get; }
    public byte[] IntakePolicyBytes { get; }
    public string IntakePolicyPath { get; }
    public byte[] EnvelopeBytes => Convert.FromBase64String(Transaction.EnvelopeBase64);

    public void PrepareOutboundForTestSink()
    {
        Assert(OutboundLedger.Reserve(Transaction.Provenance, NowUtc));
        foreach (var state in new[] { Stage3TransactionStates.Validated, Stage3TransactionStates.Signed, Stage3TransactionStates.Serialized, Stage3TransactionStates.TestSinkSent })
        {
            Assert(OutboundLedger.Transition(Transaction.Provenance.TransactionId, state, NowUtc, state == Stage3TransactionStates.TestSinkSent));
        }
    }

    public void PrepareOutboundForManualPilot()
    {
        var ledger = NewLedgerForIdentity(OutboundIdentity, OutboundCheckpointSigner, "outbound-ledger-checkpoint", allowLiveManualPilot: true);
        Assert(ledger.Reserve(Transaction.Provenance, NowUtc));
        foreach (var state in new[]
                 {
                     Stage3TransactionStates.Validated,
                     Stage3TransactionStates.Signed,
                     Stage3TransactionStates.Serialized,
                     Stage3TransactionStates.LiveDeliveryPending
                 })
        {
            Assert(ledger.Transition(Transaction.Provenance.TransactionId, state, NowUtc,
                state == Stage3TransactionStates.LiveDeliveryPending));
        }
    }

    public Stage3CodexIntakeFrameV1 CreateFrame(byte[]? transactionBytes = null, byte[]? attestationBytes = null) => new()
    {
        SignedTransactionBase64 = Convert.ToBase64String(transactionBytes ?? TransactionBytes),
        BuildAttestationBase64 = Convert.ToBase64String(attestationBytes ?? AttestationBytes),
        BuildAttestationSha256 = Stage2Crypto.Sha256Hex(attestationBytes ?? AttestationBytes),
        DestinationCodexThreadId = ThreadId,
        SenderProcessId = Environment.ProcessId,
        IssuedAtUtc = DateTimeOffset.UtcNow.ToString("O")
    };

    public Stage3CodexIntakeFrameV1 CreateManualPilotFrame(byte[]? transactionBytes = null)
    {
        var frame = CreateFrame(transactionBytes);
        frame.DeliveryClassification = "watcher_stage3_manual_pilot";
        frame.SenderStage = nameof(WatcherOperatingStage.Stage3ManualPilot);
        return frame;
    }

    public Stage3CodexIntakeFrameV1 CreateLimitedAutomaticFrame(byte[]? transactionBytes = null)
    {
        var frame = CreateFrame(transactionBytes);
        frame.DeliveryClassification = "watcher_stage4_limited_automatic";
        frame.SenderStage = nameof(WatcherOperatingStage.Stage4LimitedAutomatic);
        return frame;
    }

    public string WriteIntakePolicy(string? path = null)
    {
        path ??= IntakePolicyPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, IntakePolicyBytes);
        return path;
    }

    public string WriteFrame(Stage3CodexIntakeFrameV1 frame, string? path = null)
    {
        path ??= Path.Combine(Root, "transport", "intake-frame.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Stage3CodexIntakeGate.SerializeFrame(frame));
        return path;
    }

    public Stage2InstructionProvenanceV1 CloneProvenance(string? suffix = null)
    {
        var clone = Stage2CanonicalJson.CloneProvenance(Transaction.Provenance);
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            clone.TransactionId = $"{clone.TransactionId}-{suffix}";
            clone.Nonce = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(clone.Nonce + suffix));
            clone.AssistantMessageId += "-" + suffix;
            clone.WakeMessageId += "-" + suffix;
            clone.EnvelopeSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(clone.EnvelopeSha256 + suffix));
        }
        return clone;
    }

    public Stage3ReplayLedgerV2Service NewLedger(string role, string instance, string leaf, IStage2ProvenanceSigner? signer = null, TimeSpan? timeout = null)
    {
        var identity = Identity(role, instance, leaf);
        var effectiveSigner = signer ?? (role.Equals("codex-intake", StringComparison.Ordinal)
            ? IntakeCheckpointSigner
            : OutboundCheckpointSigner);
        return new Stage3ReplayLedgerV2Service(identity, effectiveSigner,
            new Stage3PurposeKeyResolver(TrustStore,
                role.Equals("codex-intake", StringComparison.Ordinal) ? "intake-ledger-checkpoint" : "outbound-ledger-checkpoint",
                1, NowUtc), timeout);
    }

    private Stage3ReplayLedgerV2Service NewLedgerForIdentity(
        Stage3LedgerIdentity identity,
        IStage2ProvenanceSigner signer,
        string purpose,
        bool allowLiveManualPilot) => new(
            identity,
            signer,
            new Stage3PurposeKeyResolver(TrustStore, purpose, 1, NowUtc),
            allowLiveManualPilot: allowLiveManualPilot);

    public static Stage3TrustedKeyRecord Trusted(IStage2ProvenanceSigner signer, string purpose, string status = "active", DateTimeOffset? activation = null) => new()
    {
        KeyId = signer.KeyId,
        Purpose = purpose,
        PublicKeySpkiBase64 = Convert.ToBase64String(signer.PublicKeySpki),
        PublicKeyFingerprintSha256 = signer.PublicKeyFingerprintSha256,
        ActivationUtc = (activation ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToUniversalTime().ToString("O"),
        ExpirationUtc = DateTimeOffset.UtcNow.AddDays(30).ToUniversalTime().ToString("O"),
        Status = status,
        MinimumAcceptedWatcherBuildGeneration = 1
    };

    public static void Assert(Stage3LedgerResult result)
    {
        if (!result.Accepted) throw new InvalidOperationException($"{result.ReasonCode}: {result.Message}");
    }

    public static void Assert(Stage3TrustResult result)
    {
        if (!result.Accepted) throw new InvalidOperationException($"{result.ReasonCode}: {result.Message}");
    }

    public void Dispose()
    {
        for (var index = _disposables.Count - 1; index >= 0; index--) _disposables[index].Dispose();
        var counters = new Stage3WindowsMonotonicCounterStore();
        foreach (var (purpose, instanceId, anchorPath) in _externalCounters)
            counters.DeleteForOfflineTests(purpose, instanceId, anchorPath);
        IntakePolicyService.DeleteOfflineRegressionCounter();
        InstallationTrustService.DeleteForOfflineTests(InstallationTrust);
        foreach (var name in _cngKeyNames)
        {
            try
            {
                using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
                key.Delete();
            }
            catch { }
        }
        try { Directory.Delete(Root, true); } catch { }
    }

    private T Own<T>(T value) where T : IDisposable
    {
        _disposables.Add(value);
        return value;
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private Stage3LedgerIdentity Identity(string role, string instance, string leaf)
    {
        var directory = Path.Combine(Root, "ledgers", leaf);
        var identity = new Stage3LedgerIdentity
        {
            LedgerRole = role,
            LedgerInstanceId = instance,
            LedgerDirectory = directory,
            AnchorDirectory = Path.Combine(Root, "anchors", leaf),
            MutexName = Stage3ReplayLedgerV2Service.CreateMutexName(role, directory)
        };
        _externalCounters.Add(("replay-ledger", instance, Path.Combine(identity.AnchorDirectory, $"{instance}.anchor.json")));
        return identity;
    }

    private Stage3BuildAttestationV1 CreateAttestation() => new()
    {
        BuildGeneration = 1,
        RepositoryPath = Root,
        Branch = "stage3-offline-test",
        SourceCommit = new string('a', 40),
        SourceTreeSha256 = Hash("source-tree"),
        SourceStatus = "clean",
        TrackedManifestSha256 = Hash("tracked-manifest"),
        UntrackedFileDisposition = "excluded test fixtures",
        BuildTimestampUtc = NowUtc.ToString("O"),
        WindowsVersion = Environment.OSVersion.VersionString,
        DotnetSdkVersion = Environment.Version.ToString(),
        DotnetRuntimeVersion = Environment.Version.ToString(),
        CompilerIdentity = "Roslyn net8.0 synthetic fixture",
        BuildConfiguration = "Release",
        TargetRuntime = "win-x64",
        PublishMode = "framework-dependent",
        ExactBuildCommand = "offline synthetic fixture",
        ExactTestCommand = "offline Stage3RegressionSuite",
        WarningsCount = 0,
        ErrorsCount = 0,
        TestCount = 207,
        ExecutablePath = ExecutablePath,
        ExecutableSha256 = FileHash(ExecutablePath),
        ApplicationDllPath = ApplicationDllPath,
        ApplicationDllSha256 = FileHash(ApplicationDllPath),
        IntakeExecutablePath = IntakeExecutablePath,
        IntakeExecutableSha256 = FileHash(IntakeExecutablePath),
        IntakeApplicationDllPath = IntakeApplicationDllPath,
        IntakeApplicationDllSha256 = FileHash(IntakeApplicationDllPath),
        IntakePolicySha256 = Stage2Crypto.Sha256Hex(IntakePolicyBytes),
        SupportingDlls = [new Stage3FileHashRecord { Path = SupportingDllPath, SizeBytes = new FileInfo(SupportingDllPath).Length, Sha256 = FileHash(SupportingDllPath) }],
        ConfigurationTemplateSha256 = FileHash(ConfigurationPath),
        ProvenanceSchemaSha256 = FileHash(ProvenanceSchemaPath),
        VerifierContractSha256 = FileHash(VerifierContractPath),
        ReplayLedgerContractSha256 = FileHash(ReplayContractPath),
        ProvenanceSignerPublicKeyFingerprint = ProvenanceSigner.PublicKeyFingerprintSha256
    };

    private byte[] CreateSignedTransaction()
    {
        var wake = Stage2DryRunPipeline.PrepareSyntheticWake(
            "stage3-test-conversation", "pre-wake", ["root", "pre-wake"], "offline-tab", "stage3-wake-token", SourceReport, "SC-STAGE3");
        wake.WakeMessageId = "wake-message";
        wake.WakeParentMessageId = "pre-wake";
        wake.WakeCreatedAtUtc = NowUtc.AddSeconds(-2);
        var envelope = BuildEnvelope();
        var snapshot = new ConversationLineageSnapshot
        {
            ConversationId = wake.ConversationId,
            CurrentNode = "assistant-message",
            BrowserTabIdentity = wake.BrowserTabIdentity,
            ApiVerified = true,
            ApiStatusCode = 200,
            BrowserBackendAgree = true,
            BrowserVisibleMessageIds = ["root", "pre-wake", "wake-message", "assistant-message"],
            Nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
            {
                ["root"] = Node("root", "", "system", "root", ["pre-wake"]),
                ["pre-wake"] = Node("pre-wake", "root", "user", "prior", ["wake-message"]),
                ["wake-message"] = Node("wake-message", "pre-wake", "user", "stage3 wake", ["assistant-message"]),
                ["assistant-message"] = Node("assistant-message", "wake-message", "assistant", envelope, [])
            }
        };
        var response = new AssistantResponseObservation
        {
            MessageId = "assistant-message",
            ParentMessageId = "wake-message",
            Role = "assistant",
            Content = envelope,
            Complete = true,
            OnCurrentPath = true,
            WakeToken = wake.WakeToken,
            SourceReport = SourceReport,
            CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
            FallbackBody = false,
            ApiVerified = true,
            SelectedAssistantIndex = 0,
            AssistantSelectionAmbiguous = false,
            WholePageCaptureUsed = false,
            CurrentNodeAtCapture = "assistant-message",
            CreatedAtUtc = NowUtc.AddSeconds(-1)
        };
        var pipeline = new Stage2DryRunPipeline(ProvenanceSigner,
            new Stage2ReplayLedger(Path.Combine(Root, "stage2-outbound.json")),
            Path.Combine(Root, "stage2-transactions"));
        var build = new Stage2BuildIdentity
        {
            SourceCommit = Attestation.SourceCommit,
            SourceTreeSha256 = Attestation.SourceTreeSha256,
            ExecutableSha256 = Attestation.ExecutableSha256,
            ConfigurationSha256 = Attestation.ConfigurationTemplateSha256
        };
        var result = pipeline.BuildSignedDryRunTransaction(wake, snapshot, response, ThreadId, build, NowUtc);
        if (!result.Success) throw new InvalidOperationException($"Fixture transaction failed: {result.ReasonCode} {result.Message}");
        return result.PayloadBytes!;
    }

    private static ConversationNodeRecord Node(string id, string parent, string role, string content, List<string> children) => new()
    {
        MessageId = id,
        ParentMessageId = parent,
        Role = role,
        Content = content,
        Complete = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ChildMessageIds = children
    };

    private static string BuildEnvelope() => $"""
<<<DCS_CODEX_TASK_V1>>>
task_id: SC-STAGE3-20260716-170000
origin: chatgpt-ui
repo: example/watcher-regression
target: codex-director
mode: instruction
created_at: 2026-07-16T17:00:00Z
source_report: {SourceReport}

BEGIN_INSTRUCTION
This is a synthetic offline Stage 3 readiness instruction. It exists only to verify exact branch lineage, signed provenance, build attestation, replay resistance, destination binding, and isolated test-sink enforcement. It must never be exposed as a live Codex task.
END_INSTRUCTION
<<<END_DCS_CODEX_TASK_V1>>>
""";

    private static string Hash(string text) => Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(text));
    private static string FileHash(string path) => Stage2Crypto.Sha256Hex(File.ReadAllBytes(path));
}
