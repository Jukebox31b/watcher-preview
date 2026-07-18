using System.Text.Json;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record Stage3BuildAttestationResult(bool Accepted, string ReasonCode, string Message, Stage3BuildAttestationV1? Attestation = null);

public sealed class Stage3BuildAttestationService
{
    private readonly Stage3WindowsMonotonicCounterStore _externalCounter = new();

    public void WriteGenerationAnchor(
        string path,
        byte[] attestationBytes,
        Stage3BuildAttestationV1 attestation,
        IStage2ProvenanceSigner signer,
        DateTimeOffset nowUtc)
    {
        var anchor = new Stage3MonotonicAnchorV1
        {
            AnchorPurpose = "build-attestation",
            ObjectInstanceId = "DCS-WATCHER-BUILD",
            MaximumGeneration = attestation.BuildGeneration,
            ObjectDigest = Stage2Crypto.Sha256Hex(attestationBytes),
            IssuedAtUtc = nowUtc.ToUniversalTime().ToString("O"),
            SignerKeyId = signer.KeyId
        };
        var unsigned = SerializeAnchorUnsigned(anchor);
        anchor.Signature = Convert.ToBase64String(signer.Sign(unsigned));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(anchor, Stage2CanonicalJson.Options));
        var external = _externalCounter.Advance("build-attestation", anchor.ObjectInstanceId, path,
            anchor.MaximumGeneration, anchor.ObjectDigest, nowUtc);
        if (!external.Accepted) throw new InvalidOperationException($"{external.ReasonCode}: {external.Message}");
    }

    public Stage3BuildAttestationResult ValidateGenerationAnchor(
        string path,
        byte[] attestationBytes,
        Stage3BuildAttestationV1 attestation,
        Stage3TrustStoreService trustStore,
        DateTimeOffset nowUtc)
    {
        if (!File.Exists(path)) return Reject("BUILD_GENERATION_ANCHOR_MISSING", "Build-generation anchor is missing.");
        var bytes = File.ReadAllBytes(path);
        Stage3MonotonicAnchorV1? anchor;
        try { anchor = JsonSerializer.Deserialize<Stage3MonotonicAnchorV1>(bytes, Stage2CanonicalJson.Options); }
        catch (JsonException) { anchor = null; }
        if (anchor is null || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(anchor, Stage2CanonicalJson.Options)) ||
            !anchor.AnchorPurpose.Equals("build-attestation", StringComparison.Ordinal) ||
            !anchor.ObjectInstanceId.Equals("DCS-WATCHER-BUILD", StringComparison.Ordinal))
            return Reject("BUILD_GENERATION_ANCHOR_INVALID", "Build-generation anchor is invalid.");
        var eligible = trustStore.EvaluateKey(anchor.SignerKeyId, "build-attestation", attestation.BuildGeneration, nowUtc);
        if (!eligible.Accepted || eligible.Key is null) return Reject(eligible.ReasonCode, eligible.Message);
        var publicKey = ToPublicRecord(eligible.Key);
        try
        {
            if (!Stage2Crypto.Verify(publicKey, SerializeAnchorUnsigned(anchor), Convert.FromBase64String(anchor.Signature)))
                return Reject("BUILD_GENERATION_ANCHOR_SIGNATURE_INVALID", "Build-generation anchor signature is invalid.");
        }
        catch (FormatException)
        {
            return Reject("BUILD_GENERATION_ANCHOR_SIGNATURE_INVALID", "Build-generation anchor signature encoding is invalid.");
        }
        if (anchor.MaximumGeneration > attestation.BuildGeneration)
            return Reject("BUILD_GENERATION_ROLLBACK", "Attestation generation is older than the signed anchor.");
        if (anchor.MaximumGeneration < attestation.BuildGeneration)
            return Reject("BUILD_GENERATION_ANCHOR_STALE", "Attestation is newer than its generation anchor.");
        if (!anchor.ObjectDigest.Equals(Stage2Crypto.Sha256Hex(attestationBytes), StringComparison.OrdinalIgnoreCase))
            return Reject("BUILD_GENERATION_ANCHOR_DIGEST_MISMATCH", "Build-generation anchor does not bind the attestation.");
        var external = _externalCounter.Validate("build-attestation", anchor.ObjectInstanceId, path,
            anchor.MaximumGeneration, anchor.ObjectDigest);
        if (!external.Accepted) return Reject(external.ReasonCode, external.Message);
        return new Stage3BuildAttestationResult(true, "OK", "Build-generation anchor is valid.", attestation);
    }
    public byte[] Sign(Stage3BuildAttestationV1 attestation, IStage2ProvenanceSigner signer)
    {
        attestation.AttestationSignerKeyId = signer.KeyId;
        attestation.SignatureAlgorithm = Stage2InstructionProvenanceV1.AlgorithmName;
        attestation.Signature = string.Empty;
        attestation.Signature = Convert.ToBase64String(signer.Sign(SerializeUnsigned(attestation)));
        return Serialize(attestation);
    }

    public Stage3BuildAttestationResult Validate(
        byte[] attestationBytes,
        Stage3TrustStoreService trustStore,
        DateTimeOffset nowUtc,
        long minimumBuildGeneration = 1,
        IReadOnlySet<string>? allowedSourceCommits = null,
        bool verifyRuntimeFiles = true,
        IReadOnlySet<string>? allowedCompilerIdentities = null)
    {
        var attestation = Deserialize<Stage3BuildAttestationV1>(attestationBytes);
        if (attestation is null || !attestationBytes.AsSpan().SequenceEqual(Serialize(attestation)))
            return Reject("BUILD_ATTESTATION_NONCANONICAL", "Build attestation is invalid or noncanonical.");
        if (!attestation.Schema.Equals("DCS_WATCHER_BUILD_ATTESTATION_V1", StringComparison.Ordinal) || attestation.Version != 1)
            return Reject("BUILD_ATTESTATION_SCHEMA_INVALID", "Build-attestation schema or version is invalid.");
        if (attestation.BuildGeneration < minimumBuildGeneration)
            return Reject("BUILD_GENERATION_ROLLBACK", "Build attestation is older than the allowed generation.");
        if (allowedSourceCommits is not null && !allowedSourceCommits.Contains(attestation.SourceCommit))
            return Reject("SOURCE_COMMIT_UNTRUSTED", "Source commit is not allowed by local policy.");
        if (!attestation.SourceStatus.Equals("clean", StringComparison.Ordinal))
            return Reject("SOURCE_TREE_DIRTY", "Trusted build attestation requires a clean committed source tree.");
        if (allowedCompilerIdentities is not null && !allowedCompilerIdentities.Contains(attestation.CompilerIdentity))
            return Reject("TOOLCHAIN_MISMATCH", "Build compiler/toolchain identity is not allowed by local policy.");
        if (attestation.WarningsCount != 0 || attestation.ErrorsCount != 0 || attestation.TestCount < 97)
            return Reject("BUILD_RESULT_UNTRUSTED", "Build or test result does not meet readiness policy.");
        if (!IsSha(attestation.SourceTreeSha256) || !IsSha(attestation.TrackedManifestSha256) ||
            !IsSha(attestation.ExecutableSha256) || !IsSha(attestation.ApplicationDllSha256) ||
            !IsSha(attestation.ConfigurationTemplateSha256) || !IsSha(attestation.ProvenanceSchemaSha256) ||
            !IsSha(attestation.VerifierContractSha256) || !IsSha(attestation.ReplayLedgerContractSha256) ||
            !IsSha(attestation.IntakeExecutableSha256) || !IsSha(attestation.IntakeApplicationDllSha256) ||
            !IsSha(attestation.IntakePolicySha256))
            return Reject("BUILD_HASH_FIELD_INVALID", "Build attestation has a malformed SHA-256 field.");

        var keyEligibility = trustStore.EvaluateKey(attestation.AttestationSignerKeyId, "build-attestation", attestation.BuildGeneration, nowUtc);
        if (!keyEligibility.Accepted || keyEligibility.Key is null)
            return Reject(keyEligibility.ReasonCode, keyEligibility.Message);
        var publicKey = ToPublicRecord(keyEligibility.Key);
        try
        {
            if (!Stage2Crypto.Verify(publicKey, SerializeUnsigned(attestation), Convert.FromBase64String(attestation.Signature)))
                return Reject("BUILD_ATTESTATION_SIGNATURE_INVALID", "Build-attestation signature is invalid.");
        }
        catch (FormatException)
        {
            return Reject("BUILD_ATTESTATION_SIGNATURE_INVALID", "Build-attestation signature encoding is invalid.");
        }

        if (verifyRuntimeFiles)
        {
            var fileChecks = new List<(string Path, string Hash, string Code)>
            {
                (attestation.ExecutablePath, attestation.ExecutableSha256, "EXECUTABLE_HASH_MISMATCH"),
                (attestation.ApplicationDllPath, attestation.ApplicationDllSha256, "APPLICATION_DLL_HASH_MISMATCH"),
                (attestation.IntakeExecutablePath, attestation.IntakeExecutableSha256, "INTAKE_EXECUTABLE_HASH_MISMATCH"),
                (attestation.IntakeApplicationDllPath, attestation.IntakeApplicationDllSha256, "INTAKE_APPLICATION_DLL_HASH_MISMATCH")
            };
            fileChecks.AddRange(attestation.SupportingDlls.Select(file => (file.Path, file.Sha256, "SUPPORTING_DLL_HASH_MISMATCH")));
            foreach (var (path, hash, code) in fileChecks)
            {
                if (!File.Exists(path) || !Stage2Crypto.Sha256Hex(File.ReadAllBytes(path)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return Reject(code, $"Runtime file does not match signed build attestation: {path}");
            }
        }
        return new Stage3BuildAttestationResult(true, "OK", "Signed build attestation is valid.", attestation);
    }

    public Stage3BuildAttestationResult ValidateRuntimeDependencies(
        Stage3BuildAttestationV1 attestation,
        string configurationPath,
        string provenanceSchemaPath,
        string verifierContractPath,
        string replayContractPath)
    {
        var checks = new[]
        {
            (configurationPath, attestation.ConfigurationTemplateSha256, "CONFIGURATION_HASH_MISMATCH"),
            (provenanceSchemaPath, attestation.ProvenanceSchemaSha256, "PROVENANCE_SCHEMA_HASH_MISMATCH"),
            (verifierContractPath, attestation.VerifierContractSha256, "VERIFIER_CONTRACT_HASH_MISMATCH"),
            (replayContractPath, attestation.ReplayLedgerContractSha256, "REPLAY_CONTRACT_HASH_MISMATCH")
        };
        foreach (var (path, expected, code) in checks)
        {
            if (!File.Exists(path) || !Stage2Crypto.Sha256Hex(File.ReadAllBytes(path)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                return Reject(code, $"Runtime dependency does not match signed build attestation: {path}");
        }
        return new Stage3BuildAttestationResult(true, "OK", "Runtime dependency hashes match the signed attestation.", attestation);
    }

    public Stage3BuildAttestationResult ValidateExecutingRuntime(
        Stage3BuildAttestationV1 attestation,
        Stage3RuntimeIdentity runtime)
    {
        string expectedExecutable;
        string expectedAssembly;
        string expectedConfigurationHash;
        if (runtime.Role.Equals("watcher", StringComparison.Ordinal))
        {
            expectedExecutable = attestation.ExecutablePath;
            expectedAssembly = attestation.ApplicationDllPath;
            expectedConfigurationHash = attestation.ConfigurationTemplateSha256;
        }
        else if (runtime.Role.Equals("codex-intake", StringComparison.Ordinal))
        {
            expectedExecutable = attestation.IntakeExecutablePath;
            expectedAssembly = attestation.IntakeApplicationDllPath;
            expectedConfigurationHash = attestation.IntakePolicySha256;
        }
        else
        {
            return Reject("RUNTIME_ROLE_INVALID", "Runtime attestation role is invalid.");
        }

        if (!SamePath(runtime.ExecutablePath, expectedExecutable))
            return Reject("EXECUTING_PROCESS_PATH_MISMATCH", "The executing process is not the executable named by the signed attestation.");
        if (!SamePath(runtime.ApplicationAssemblyPath, expectedAssembly))
            return Reject("EXECUTING_ASSEMBLY_PATH_MISMATCH", "The loaded Watcher assembly is not the assembly named by the signed attestation.");
        if (!File.Exists(runtime.ExecutablePath) || !File.Exists(runtime.ApplicationAssemblyPath))
            return Reject("EXECUTING_RUNTIME_FILE_MISSING", "An executing process or assembly identity file is missing.");
        var expectedExecutableHash = runtime.Role.Equals("watcher", StringComparison.Ordinal)
            ? attestation.ExecutableSha256 : attestation.IntakeExecutableSha256;
        var expectedAssemblyHash = runtime.Role.Equals("watcher", StringComparison.Ordinal)
            ? attestation.ApplicationDllSha256 : attestation.IntakeApplicationDllSha256;
        if (!Stage2Crypto.Sha256Hex(File.ReadAllBytes(runtime.ExecutablePath)).Equals(expectedExecutableHash, StringComparison.OrdinalIgnoreCase))
            return Reject("EXECUTING_PROCESS_HASH_MISMATCH", "The executing process hash does not match the signed attestation.");
        if (!Stage2Crypto.Sha256Hex(File.ReadAllBytes(runtime.ApplicationAssemblyPath)).Equals(expectedAssemblyHash, StringComparison.OrdinalIgnoreCase))
            return Reject("EXECUTING_ASSEMBLY_HASH_MISMATCH", "The loaded Watcher assembly hash does not match the signed attestation.");
        var activeConfigurationHash = !string.IsNullOrWhiteSpace(runtime.ActiveConfigurationPath)
            ? File.Exists(runtime.ActiveConfigurationPath) ? Stage2Crypto.Sha256Hex(File.ReadAllBytes(runtime.ActiveConfigurationPath)) : string.Empty
            : runtime.ActiveConfigurationSha256;
        if (!activeConfigurationHash.Equals(expectedConfigurationHash, StringComparison.OrdinalIgnoreCase))
            return Reject("ACTIVE_CONFIGURATION_HASH_MISMATCH", "The active configuration or signed intake policy does not match the attestation.");
        return new Stage3BuildAttestationResult(true, "OK", "The actual executing process, assembly, and active configuration match the signed attestation.", attestation);
    }

    public static byte[] SerializeUnsigned(Stage3BuildAttestationV1 attestation)
    {
        var clone = Deserialize<Stage3BuildAttestationV1>(Serialize(attestation))!;
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    public static byte[] Serialize(Stage3BuildAttestationV1 attestation) => JsonSerializer.SerializeToUtf8Bytes(attestation, Stage2CanonicalJson.Options);
    public static Stage3BuildAttestationV1? Deserialize(byte[] bytes) => Deserialize<Stage3BuildAttestationV1>(bytes);
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Stage2CanonicalJson.Options);
    private static T? Deserialize<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Stage2CanonicalJson.Options); }
        catch (JsonException) { return default; }
    }
    private static bool IsSha(string value) => value.Length == 64 && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant);
    private static bool SamePath(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static byte[] SerializeAnchorUnsigned(Stage3MonotonicAnchorV1 anchor)
    {
        var clone = JsonSerializer.Deserialize<Stage3MonotonicAnchorV1>(JsonSerializer.SerializeToUtf8Bytes(anchor, Stage2CanonicalJson.Options), Stage2CanonicalJson.Options)!;
        clone.Signature = string.Empty;
        return JsonSerializer.SerializeToUtf8Bytes(clone, Stage2CanonicalJson.Options);
    }
    private static Stage2PublicKeyRecord ToPublicRecord(Stage3TrustedKeyRecord key) => new()
    {
        KeyId = key.KeyId,
        Algorithm = key.Algorithm,
        PublicKeySpkiBase64 = key.PublicKeySpkiBase64,
        PublicKeyFingerprintSha256 = key.PublicKeyFingerprintSha256,
        Status = "active"
    };
    private static Stage3BuildAttestationResult Reject(string code, string message) => new(false, code, message);
}
