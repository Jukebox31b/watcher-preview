using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3IntakePolicyService
{
    private readonly InstallationTrustContext? _trustContext;
    private readonly string _counterInstanceId = string.Empty;
    private readonly string _counterScopePath = string.Empty;
    private readonly bool _offlineRegressionAuthority;

    public Stage3IntakePolicyService(InstallationTrustContext trustContext)
        : this(trustContext, false)
    {
    }

    private Stage3IntakePolicyService(InstallationTrustContext trustContext, bool offlineRegressionAuthority)
    {
        _trustContext = trustContext ?? throw new ArgumentNullException(nameof(trustContext));
        _counterInstanceId = trustContext.PolicyCounterInstanceId;
        _counterScopePath = Path.GetFullPath(trustContext.CounterScopePath);
        _offlineRegressionAuthority = offlineRegressionAuthority;
    }

    internal static Stage3IntakePolicyService CreateOfflineRegression(
        string fixtureRoot,
        InstallationTrustContext trustContext)
    {
        var fullRoot = Path.GetFullPath(fixtureRoot);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("DcsWatcherV2-Stage3-", StringComparison.Ordinal) ||
            !trustContext.SecurityRoot.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Offline intake-policy trust requires an isolated Stage 3 temporary fixture root.");
        return new Stage3IntakePolicyService(trustContext, true);
    }

    public byte[] Sign(Stage3CodexIntakePolicyV1 policy, IStage2ProvenanceSigner signer)
    {
        var context = RequireTrust();
        if (!signer.KeyId.Equals(context.ActivePolicySigner.KeyId, StringComparison.Ordinal) ||
            !signer.PublicKeyFingerprintSha256.Equals(context.ActivePolicySigner.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The policy signer is not the active installation policy authority.");
        if (!context.IsDestinationApproved(policy.Configuration.ExpectedDirectorThreadId))
            throw new InvalidOperationException("The policy destination is not approved by installation trust.");
        policy.ExpectedTrustRootFingerprintSha256 = context.TrustRootFingerprintSha256;
        policy.SignerKeyId = signer.KeyId;
        policy.SignatureAlgorithm = Stage2InstructionProvenanceV1.AlgorithmName;
        policy.Signature = string.Empty;
        policy.Signature = Convert.ToBase64String(signer.Sign(SerializeUnsigned(policy)));
        return Serialize(policy);
    }

    public Stage3IntakePolicyResult ValidatePinned(byte[] policyBytes, DateTimeOffset nowUtc)
    {
        if (_trustContext is null)
            return Reject("INSTALLATION_TRUST_REQUIRED", "A validated installation trust context is required for intake policy verification.");
        var cryptographic = ValidateSignatureAndSemantics(policyBytes, nowUtc);
        if (!cryptographic.Accepted || cryptographic.Policy is null) return cryptographic;
        var external = new Stage3WindowsMonotonicCounterStore().Validate(
            "intake-policy",
            _counterInstanceId,
            _counterScopePath,
            cryptographic.Policy.PolicyGeneration,
            Stage2Crypto.Sha256Hex(policyBytes));
        return external.Accepted ? cryptographic : Reject(external.ReasonCode, external.Message);
    }

    public Stage3IntakePolicyResult ActivatePinnedPolicy(byte[] policyBytes, DateTimeOffset nowUtc)
    {
        if (_trustContext is null)
            return Reject("INSTALLATION_TRUST_REQUIRED", "A validated installation trust context is required for intake policy activation.");
        var cryptographic = ValidateSignatureAndSemantics(policyBytes, nowUtc);
        if (!cryptographic.Accepted || cryptographic.Policy is null) return cryptographic;
        var external = new Stage3WindowsMonotonicCounterStore().Advance(
            "intake-policy",
            _counterInstanceId,
            _counterScopePath,
            cryptographic.Policy.PolicyGeneration,
            Stage2Crypto.Sha256Hex(policyBytes),
            nowUtc);
        return external.Accepted ? ValidatePinned(policyBytes, nowUtc) : Reject(external.ReasonCode, external.Message);
    }

    internal void DeleteOfflineRegressionCounter()
    {
        if (!_offlineRegressionAuthority)
            throw new InvalidOperationException("Production installation policy counters cannot be deleted by test cleanup.");
        new Stage3WindowsMonotonicCounterStore().DeleteForOfflineTests(
            "intake-policy",
            _counterInstanceId,
            _counterScopePath);
    }

    internal (string InstanceId, string ScopePath, string MutexName) CounterIdentityForDiagnostics()
    {
        _ = RequireTrust();
        return (_counterInstanceId, _counterScopePath,
            Stage3WindowsMonotonicCounterStore.MutexNameForDiagnostics("intake-policy", _counterInstanceId, _counterScopePath));
    }

    private Stage3IntakePolicyResult ValidateSignatureAndSemantics(byte[] policyBytes, DateTimeOffset nowUtc)
    {
        var context = RequireTrust();
        Stage3CodexIntakePolicyV1? policy;
        try { policy = JsonSerializer.Deserialize<Stage3CodexIntakePolicyV1>(policyBytes, Stage2CanonicalJson.Options); }
        catch (JsonException ex) { return Reject("INTAKE_POLICY_JSON_INVALID", ex.Message); }
        if (policy is null || !policyBytes.AsSpan().SequenceEqual(Serialize(policy)))
            return Reject("INTAKE_POLICY_NONCANONICAL", "Intake policy is invalid or noncanonical.");
        if (!policy.Schema.Equals("DCS_CODEX_INTAKE_POLICY_V1", StringComparison.Ordinal) || policy.Version != 1 ||
            policy.PolicyGeneration < context.MinimumPolicyGeneration)
            return Reject("INTAKE_POLICY_SCHEMA_INVALID", "Intake policy schema, version, or generation is invalid.");
        if (!policy.SignerKeyId.Equals(context.ActivePolicySigner.KeyId, StringComparison.Ordinal) ||
            !policy.SignatureAlgorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal))
            return Reject("INTAKE_POLICY_SIGNER_INVALID", "Intake policy is not signed by the active installation policy authority.");
        if (!policy.ExpectedTrustRootFingerprintSha256.Equals(context.TrustRootFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            return Reject("INTAKE_POLICY_TRUST_ROOT_MISMATCH", "Intake policy names another installation trust root.");
        if (!context.IsDestinationApproved(policy.Configuration.ExpectedDirectorThreadId))
            return Reject("INTAKE_POLICY_DESTINATION_MISMATCH", "Intake policy destination is not approved by installation trust.");
        if (!DateTimeOffset.TryParse(policy.IssueTimeUtc, out var issue) || !DateTimeOffset.TryParse(policy.ExpiryTimeUtc, out var expiry) ||
            expiry <= issue || nowUtc.ToUniversalTime() < issue.ToUniversalTime().AddMinutes(-5) || nowUtc.ToUniversalTime() > expiry.ToUniversalTime())
            return Reject("INTAKE_POLICY_TIME_INVALID", "Intake policy timestamps are invalid, not active, or expired.");
        if (policy.MinimumBuildGeneration < 1 || policy.AllowedSourceCommit.Length != 40 || !policy.AllowedSourceCommit.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(policy.AllowedCompilerIdentity) || policy.AllowedCompilerIdentity.Length > 512)
            return Reject("INTAKE_POLICY_BUILD_RULE_INVALID", "Intake policy build-generation or source-commit rule is invalid.");
        if (!PathsAreAbsolute(policy.Configuration))
            return Reject("INTAKE_POLICY_PATH_INVALID", "Every intake policy path must be absolute.");
        var configurationValidation = ValidateConfiguration(policy.Configuration);
        if (configurationValidation is not null) return configurationValidation;
        try
        {
            var publicBytes = Convert.FromBase64String(context.ActivePolicySigner.PublicKeySpkiBase64);
            if (!Stage2Crypto.Sha256Hex(publicBytes).Equals(context.ActivePolicySigner.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
                return Reject("INSTALLATION_POLICY_KEY_INVALID", "Installation policy public-key fingerprint is inconsistent.");
            if (!Stage2Crypto.Verify(context.ActivePolicySigner, SerializeUnsigned(policy), Convert.FromBase64String(policy.Signature)))
                return Reject("INTAKE_POLICY_SIGNATURE_INVALID", "Intake policy signature is invalid.");
        }
        catch (FormatException)
        {
            return Reject("INTAKE_POLICY_SIGNATURE_INVALID", "Intake policy signature encoding is invalid.");
        }
        return new Stage3IntakePolicyResult(true, "OK", "Installation-bound Codex intake policy is valid.", policy, policyBytes);
    }

    private InstallationTrustContext RequireTrust() => _trustContext ?? throw new InvalidOperationException(
        "A validated installation trust context is required.");

    private static Stage3IntakePolicyResult? ValidateConfiguration(Stage3CodexIntakeConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.OutboundLedgerInstanceId) || string.IsNullOrWhiteSpace(value.IntakeLedgerInstanceId) ||
            string.IsNullOrWhiteSpace(value.IntakeCheckpointSignerKeyId) || string.IsNullOrWhiteSpace(value.IntakeCheckpointCngKeyName) ||
            value.LockTimeoutMilliseconds is < 100 or > 60_000)
            return Reject("INTAKE_POLICY_CONFIGURATION_INVALID", "Signed intake policy has incomplete identities or an invalid lock timeout.");
        try
        {
            var expectedOutboundMutex = Stage3ReplayLedgerV2Service.CreateMutexName("watcher-outbound", value.OutboundLedgerDirectory);
            var expectedIntakeMutex = Stage3ReplayLedgerV2Service.CreateMutexName("codex-intake", value.IntakeLedgerDirectory);
            if (!value.OutboundLedgerMutexName.Equals(expectedOutboundMutex, StringComparison.Ordinal) ||
                !value.IntakeLedgerMutexName.Equals(expectedIntakeMutex, StringComparison.Ordinal))
                return Reject("INTAKE_POLICY_MUTEX_IDENTITY_INVALID", "Ledger mutex identities are not derived from the signed role and directory.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Reject("INTAKE_POLICY_MUTEX_IDENTITY_INVALID", ex.Message);
        }
        return null;
    }

    private static bool PathsAreAbsolute(Stage3CodexIntakeConfiguration value)
    {
        var paths = new[]
        {
            value.TrustStorePath, value.TrustRootPath, value.TrustAnchorPath, value.AllowedBuildAttestationPath,
            value.BuildGenerationAnchorPath, value.ConfigurationTemplatePath, value.ProvenanceSchemaPath,
            value.VerifierContractPath, value.ReplayContractPath, value.OutboundLedgerDirectory,
            value.OutboundLedgerAnchorDirectory, value.IntakeLedgerDirectory, value.IntakeLedgerAnchorDirectory,
            value.QuarantineDirectory, value.TestSinkDirectory
        };
        return paths.All(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path));
    }

    private static byte[] Serialize(Stage3CodexIntakePolicyV1 value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Stage2CanonicalJson.Options);

    private static byte[] SerializeUnsigned(Stage3CodexIntakePolicyV1 value)
    {
        var clone = JsonSerializer.Deserialize<Stage3CodexIntakePolicyV1>(Serialize(value), Stage2CanonicalJson.Options)!;
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static Stage3IntakePolicyResult Reject(string code, string message) => new(false, code, message);
}
