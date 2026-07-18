using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Security;

public sealed class InstallationTrustAnchorService
{
    public const string BundleFileName = "installation-trust.public.json";
    public const string CounterScopeFileName = "installation-trust.anchor";
    internal const string CommitIntentFileName = "installation-trust.commit.json";
    private const string CounterPurpose = "installation-trust";
    private readonly Stage3WindowsMonotonicCounterStore _counterStore = new();

    internal string? FaultStopAfterStep { get; set; }

    public static string DefaultSecurityRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Watcher",
        "security");

    public InstallationTrustResult Provision(InstallationTrustProvisioningOptions options)
    {
        if (!OperatingSystem.IsWindows())
            return Reject("INSTALLATION_TRUST_WINDOWS_REQUIRED", "Installation trust provisioning requires Windows CNG and ACL protection.");

        var nowUtc = (options.NowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var rootValidation = ResolveSecurityRoot(options.SecurityRoot);
        if (!rootValidation.Accepted || rootValidation.Root is null)
            return Reject(rootValidation.ReasonCode, rootValidation.Message);
        var securityRoot = rootValidation.Root;
        if (string.IsNullOrWhiteSpace(options.DestinationId) || options.DestinationId.Length > 512)
            return Reject("INSTALLATION_DESTINATION_INVALID", "One non-empty destination identity is required for provisioning.");
        CommitTransactionLock transactionLock;
        try { transactionLock = AcquireTransactionLock(securityRoot); }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or SecurityException)
        {
            return Reject("INSTALLATION_TRUST_LOCK_FAILED", ex.Message);
        }
        using var heldTransactionLock = transactionLock;

        var bundlePath = Path.Combine(securityRoot, BundleFileName);
        var counterScopePath = Path.Combine(securityRoot, CounterScopeFileName);
        var intentPath = Path.Combine(securityRoot, CommitIntentFileName);
        if (File.Exists(intentPath))
        {
            var recovered = RecoverPendingIntent(securityRoot, bundlePath, counterScopePath, intentPath, options.NowUtc ?? DateTimeOffset.UtcNow);
            if (!recovered.Accepted) return recovered;
        }
        if (File.Exists(bundlePath))
            return Load(securityRoot);

        try
        {
            Directory.CreateDirectory(securityRoot);
            var createdRoot = ResolveSecurityRoot(securityRoot);
            if (!createdRoot.Accepted || createdRoot.Root is null || !SamePath(createdRoot.Root, securityRoot))
                return Reject(createdRoot.ReasonCode, createdRoot.Message);
            RestrictDirectory(securityRoot);

            var installationId = Guid.NewGuid().ToString("N");
            var rootKeyId = $"watcher-installation-root-{installationId}";
            var policyKeyId = $"watcher-installation-policy-{installationId}-g1";
            using var rootSigner = WindowsCngStage2ProvenanceSigner.OpenOrCreate(rootKeyId, RootCngKeyName(installationId));
            using var policySigner = WindowsCngStage2ProvenanceSigner.OpenOrCreate(policyKeyId, PolicyCngKeyName(installationId, 1));
            var bundle = new InstallationTrustBundleV1
            {
                InstallationId = installationId,
                Generation = 1,
                CreatedAtUtc = nowUtc.ToString("O"),
                TrustRoot = PublicKey(rootSigner, 1, nowUtc),
                PolicySigningKeys = [PublicKey(policySigner, 1, nowUtc)],
                ApprovedDestinations =
                [
                    new InstallationDestinationV1
                    {
                        DestinationId = options.DestinationId,
                        Status = "active",
                        ApprovedAtUtc = nowUtc.ToString("O")
                    }
                ],
                PolicyCounterInstanceId = CounterInstanceId(bundlePath),
                PolicyCounterScopeIdentity = ScopeIdentity(counterScopePath)
            };
            SignBundle(bundle, rootSigner);
            WriteScopeMarker(counterScopePath, bundle.InstallationId);
            RestrictFile(counterScopePath);
            WriteCommitIntent(intentPath, null, bundle, nowUtc);
            RestrictFile(intentPath);

            Interrupt("before-bundle-write");
            WriteBundle(bundlePath, bundle);
            RestrictFile(bundlePath);
            Interrupt("after-bundle-write");
            Interrupt("before-counter-advance");

            var advanced = _counterStore.Advance(
                CounterPurpose,
                bundle.PolicyCounterInstanceId,
                counterScopePath,
                bundle.Generation,
                bundle.BundleDigestSha256,
                nowUtc);
            if (!advanced.Accepted)
                return Reject(advanced.ReasonCode, advanced.Message);
            Interrupt("after-counter-advance");
            DeleteCommitIntent(intentPath);
            return Load(securityRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecurityException or TimeoutException)
        {
            return Reject("INSTALLATION_TRUST_PROVISION_FAILED", ex.Message);
        }
    }

    public InstallationTrustResult Load(string? securityRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            return Reject("INSTALLATION_TRUST_WINDOWS_REQUIRED", "Installation trust validation requires Windows CNG and ACL protection.");

        var rootValidation = ResolveSecurityRoot(securityRoot);
        if (!rootValidation.Accepted || rootValidation.Root is null)
            return Reject(rootValidation.ReasonCode, rootValidation.Message);
        var root = rootValidation.Root;
        CommitTransactionLock transactionLock;
        try { transactionLock = AcquireTransactionLock(root); }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or SecurityException)
        {
            return Reject("INSTALLATION_TRUST_LOCK_FAILED", ex.Message);
        }
        using var heldTransactionLock = transactionLock;
        var bundlePath = Path.Combine(root, BundleFileName);
        var counterScopePath = Path.Combine(root, CounterScopeFileName);
        var intentPath = Path.Combine(root, CommitIntentFileName);
        if (File.Exists(intentPath))
        {
            var recovered = RecoverPendingIntent(root, bundlePath, counterScopePath, intentPath, DateTimeOffset.UtcNow);
            if (!recovered.Accepted) return recovered;
        }
        if (!File.Exists(bundlePath)) return Reject("INSTALLATION_TRUST_MISSING", "The installation trust bundle is missing.");
        if (!File.Exists(counterScopePath)) return Reject("INSTALLATION_TRUST_ANCHOR_MISSING", "The installation trust scope anchor is missing.");

        try
        {
            var acl = ValidateAcl(root, bundlePath, counterScopePath);
            if (!acl.Accepted) return acl;
            var bytes = File.ReadAllBytes(bundlePath);
            var bundle = JsonSerializer.Deserialize<InstallationTrustBundleV1>(bytes, Stage2CanonicalJson.Options);
            if (bundle is null || !bytes.AsSpan().SequenceEqual(Serialize(bundle)))
                return Reject("INSTALLATION_TRUST_NONCANONICAL", "The installation trust bundle is invalid or noncanonical.");
            var semantic = ValidateBundle(bundle, bundlePath, counterScopePath);
            if (!semantic.Accepted) return semantic;
            if (!File.ReadAllText(counterScopePath, Encoding.UTF8).Equals(bundle.InstallationId, StringComparison.Ordinal))
                return Reject("INSTALLATION_TRUST_SCOPE_MISMATCH", "The trust scope anchor names another installation.");

            var external = _counterStore.Validate(
                CounterPurpose,
                bundle.PolicyCounterInstanceId,
                counterScopePath,
                bundle.Generation,
                bundle.BundleDigestSha256);
            if (!external.Accepted) return Reject(external.ReasonCode, external.Message);

            var activeKeys = bundle.PolicySigningKeys.Where(key => key.Status.Equals("active", StringComparison.Ordinal)).ToList();
            if (activeKeys.Count != 1)
                return Reject("INSTALLATION_POLICY_KEY_STATE_INVALID", "Exactly one policy-signing key must be active.");
            var active = activeKeys[0];
            return new InstallationTrustResult(
                true,
                "OK",
                "Installation trust is valid.",
                new InstallationTrustContext(root, bundlePath, counterScopePath, bundle, ToStage2Key(active)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or SecurityException or TimeoutException)
        {
            return Reject("INSTALLATION_TRUST_LOAD_FAILED", ex.Message);
        }
    }

    public IStage2ProvenanceSigner OpenActivePolicySigner(InstallationTrustContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = context.Bundle.PolicySigningKeys.Single(candidate => candidate.Status.Equals("active", StringComparison.Ordinal));
        return WindowsCngStage2ProvenanceSigner.OpenExisting(
            key.KeyId,
            PolicyCngKeyName(context.Bundle.InstallationId, key.Generation));
    }

    internal IStage2ProvenanceSigner OpenTrustRootSigner(InstallationTrustContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WindowsCngStage2ProvenanceSigner.OpenExisting(
            context.Bundle.TrustRoot.KeyId,
            RootCngKeyName(context.Bundle.InstallationId));
    }

    public InstallationTrustResult RotatePolicyKey(string? securityRoot = null, DateTimeOffset? nowUtc = null)
    {
        var loaded = Load(securityRoot);
        if (!loaded.Accepted || loaded.Context is null) return loaded;
        var context = loaded.Context;
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        try
        {
            var bundle = Clone(context.Bundle);
            foreach (var active in bundle.PolicySigningKeys.Where(key => key.Status.Equals("active", StringComparison.Ordinal)))
                active.Status = "retiring";
            var generation = bundle.Generation + 1;
            var keyId = $"watcher-installation-policy-{bundle.InstallationId}-g{generation}";
            using var policySigner = WindowsCngStage2ProvenanceSigner.OpenOrCreate(
                keyId,
                PolicyCngKeyName(bundle.InstallationId, generation));
            bundle.PolicySigningKeys.Add(PublicKey(policySigner, generation, now));
            bundle.Generation = generation;
            bundle.RotatedAtUtc = now.ToString("O");
            return CommitMutation(context, bundle, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecurityException)
        {
            return Reject("INSTALLATION_TRUST_ROTATION_FAILED", ex.Message);
        }
    }

    public InstallationTrustResult RevokePolicyKey(
        string keyId,
        string reason,
        string? securityRoot = null,
        DateTimeOffset? nowUtc = null)
    {
        var loaded = Load(securityRoot);
        if (!loaded.Accepted || loaded.Context is null) return loaded;
        if (string.IsNullOrWhiteSpace(reason)) return Reject("INSTALLATION_REVOCATION_REASON_REQUIRED", "A revocation reason is required.");
        var context = loaded.Context;
        var bundle = Clone(context.Bundle);
        var matches = bundle.PolicySigningKeys.Where(key => key.KeyId.Equals(keyId, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) return Reject("INSTALLATION_POLICY_KEY_UNKNOWN", "The policy-signing key is unknown.");
        if (matches[0].Status.Equals("revoked", StringComparison.Ordinal))
            return Reject("INSTALLATION_POLICY_KEY_REVOKED", "The policy-signing key is already revoked.");
        if (matches[0].Status.Equals("active", StringComparison.Ordinal) &&
            bundle.PolicySigningKeys.Count(key => key.Status.Equals("active", StringComparison.Ordinal)) == 1)
            return Reject("INSTALLATION_ACTIVE_KEY_REQUIRED", "Rotate the active policy key before revoking it.");
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        matches[0].Status = "revoked";
        matches[0].RevokedAtUtc = now.ToString("O");
        matches[0].RevocationReason = reason;
        bundle.Generation++;
        bundle.RotatedAtUtc = now.ToString("O");
        return CommitMutation(context, bundle, now);
    }

    public InstallationTrustResult ReplaceApprovedDestinations(
        IReadOnlyCollection<string> destinations,
        string? securityRoot = null,
        DateTimeOffset? nowUtc = null)
    {
        var normalized = destinations.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        if (normalized.Count == 0 || normalized.Any(value => value.Length > 512))
            return Reject("INSTALLATION_DESTINATION_INVALID", "At least one valid destination identity is required.");
        var loaded = Load(securityRoot);
        if (!loaded.Accepted || loaded.Context is null) return loaded;
        var context = loaded.Context;
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var bundle = Clone(context.Bundle);
        bundle.ApprovedDestinations = normalized.Select(value => new InstallationDestinationV1
        {
            DestinationId = value,
            Status = "active",
            ApprovedAtUtc = now.ToString("O")
        }).ToList();
        bundle.Generation++;
        bundle.RotatedAtUtc = now.ToString("O");
        return CommitMutation(context, bundle, now);
    }

    public InstallationTrustResult ExportPublicVerificationMaterial(string destinationPath, string? securityRoot = null)
    {
        var loaded = Load(securityRoot);
        if (!loaded.Accepted || loaded.Context is null) return loaded;
        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            Stage2AtomicFile.WriteAllBytes(fullPath, Serialize(loaded.Context.Bundle));
            return new InstallationTrustResult(true, "OK", $"Public verification material exported to {fullPath}.", loaded.Context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject("INSTALLATION_PUBLIC_EXPORT_FAILED", ex.Message);
        }
    }

    internal void DeleteForOfflineTests(InstallationTrustContext context)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!context.SecurityRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Offline installation trust must be under the Windows temporary directory.");
        _counterStore.DeleteForOfflineTests(
            CounterPurpose,
            context.PolicyCounterInstanceId,
            context.CounterScopePath);
        foreach (var keyName in new[] { RootCngKeyName(context.Bundle.InstallationId) }.Concat(
                     context.Bundle.PolicySigningKeys.Select(key => PolicyCngKeyName(context.Bundle.InstallationId, key.Generation))))
        {
            try
            {
                if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey)) continue;
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
                key.Delete();
            }
            catch (CryptographicException) { }
        }
    }

    private InstallationTrustResult CommitMutation(InstallationTrustContext context, InstallationTrustBundleV1 bundle, DateTimeOffset nowUtc)
    {
        try
        {
            using var transactionLock = AcquireTransactionLock(context.SecurityRoot);
            var current = Load(context.SecurityRoot);
            if (!current.Accepted || current.Context is null) return current;
            if (current.Context.Bundle.Generation != context.Bundle.Generation ||
                !current.Context.Bundle.BundleDigestSha256.Equals(context.Bundle.BundleDigestSha256, StringComparison.OrdinalIgnoreCase))
                return Reject("INSTALLATION_TRUST_STALE_MUTATION", "Installation trust changed before this mutation acquired the commit lock.");
            using var rootSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(
                bundle.TrustRoot.KeyId,
                RootCngKeyName(bundle.InstallationId));
            SignBundle(bundle, rootSigner);
            var intentPath = Path.Combine(context.SecurityRoot, CommitIntentFileName);
            WriteCommitIntent(intentPath, context.Bundle, bundle, nowUtc);
            RestrictFile(intentPath);
            Interrupt("before-bundle-write");
            WriteBundle(context.BundlePath, bundle);
            RestrictFile(context.BundlePath);
            Interrupt("after-bundle-write");
            Interrupt("before-counter-advance");
            var advanced = _counterStore.Advance(
                CounterPurpose,
                bundle.PolicyCounterInstanceId,
                context.CounterScopePath,
                bundle.Generation,
                bundle.BundleDigestSha256,
                nowUtc);
            if (!advanced.Accepted) return Reject(advanced.ReasonCode, advanced.Message);
            Interrupt("after-counter-advance");
            DeleteCommitIntent(intentPath);
            return Load(context.SecurityRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecurityException or TimeoutException)
        {
            return Reject("INSTALLATION_TRUST_COMMIT_FAILED", ex.Message);
        }
    }

    private InstallationTrustResult RecoverPendingIntent(
        string securityRoot,
        string bundlePath,
        string counterScopePath,
        string intentPath,
        DateTimeOffset nowUtc)
    {
        try
        {
            if (!File.Exists(counterScopePath))
                return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "A pending installation-trust commit has no scope anchor.");
            var protectedFiles = new[] { bundlePath, counterScopePath, intentPath }.Where(File.Exists).ToArray();
            var acl = ValidateAcl(securityRoot, protectedFiles);
            if (!acl.Accepted) return acl;

            var intentBytes = File.ReadAllBytes(intentPath);
            var intent = JsonSerializer.Deserialize<InstallationTrustCommitIntent>(intentBytes, Stage2CanonicalJson.Options);
            if (intent is null || !intentBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent, Stage2CanonicalJson.Options)) ||
                !intent.Schema.Equals("DCS_WATCHER_INSTALLATION_TRUST_COMMIT_V1", StringComparison.Ordinal) ||
                intent.Version != 1 || !DateTimeOffset.TryParse(intent.CreatedAtUtc, out _))
                return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The pending installation-trust commit intent is invalid or noncanonical.");

            var targetRead = ReadCanonicalBundle(intent.TargetBundleBase64);
            if (targetRead.Bundle is null || targetRead.Bytes is null)
                return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The pending installation-trust target bundle is invalid or noncanonical.");
            var target = targetRead.Bundle;
            var targetValidation = ValidateBundle(target, bundlePath, counterScopePath);
            if (!targetValidation.Accepted) return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", targetValidation.Message);
            if (!File.ReadAllText(counterScopePath, Encoding.UTF8).Equals(target.InstallationId, StringComparison.Ordinal))
                return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The pending commit and scope anchor identify different installations.");

            InstallationTrustBundleV1? previous = null;
            byte[]? previousBytes = null;
            if (intent.PreviousGeneration == 0)
            {
                if (!string.IsNullOrEmpty(intent.PreviousBundleBase64) || target.Generation != 1)
                    return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The provisioning commit intent has an invalid predecessor.");
            }
            else
            {
                var previousRead = ReadCanonicalBundle(intent.PreviousBundleBase64);
                previous = previousRead.Bundle;
                previousBytes = previousRead.Bytes;
                if (previous is null || previousBytes is null || previous.Generation != intent.PreviousGeneration ||
                    !previous.BundleDigestSha256.Equals(intent.PreviousDigestSha256, StringComparison.OrdinalIgnoreCase) ||
                    target.Generation != previous.Generation + 1 ||
                    !SameTrustIdentity(previous, target))
                    return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The pending installation-trust commit does not form one contiguous signed transition.");
                var previousValidation = ValidateBundle(previous, bundlePath, counterScopePath);
                if (!previousValidation.Accepted)
                    return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", previousValidation.Message);
            }

            byte[]? installedBytes = null;
            if (File.Exists(bundlePath))
            {
                installedBytes = File.ReadAllBytes(bundlePath);
                if (!installedBytes.AsSpan().SequenceEqual(targetRead.Bytes) &&
                    (previousBytes is null || !installedBytes.AsSpan().SequenceEqual(previousBytes)))
                    return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The installed trust bundle matches neither side of the pending commit.");
            }

            var targetCounter = _counterStore.Validate(
                CounterPurpose, target.PolicyCounterInstanceId, counterScopePath, target.Generation, target.BundleDigestSha256);
            var previousCounter = previous is null
                ? null
                : _counterStore.Validate(CounterPurpose, previous.PolicyCounterInstanceId, counterScopePath,
                    previous.Generation, previous.BundleDigestSha256);

            if (!targetCounter.Accepted && previousCounter?.Accepted != true &&
                !(previous is null && targetCounter.ReasonCode.Equals("EXTERNAL_MONOTONIC_COUNTER_MISSING", StringComparison.Ordinal)))
                return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", "The external counter matches neither side of the pending commit.");

            if (!targetCounter.Accepted)
            {
                WriteBundle(bundlePath, target);
                RestrictFile(bundlePath);
                var advanced = _counterStore.Advance(
                    CounterPurpose, target.PolicyCounterInstanceId, counterScopePath,
                    target.Generation, target.BundleDigestSha256, nowUtc.ToUniversalTime());
                if (!advanced.Accepted) return Reject(advanced.ReasonCode, advanced.Message);
            }
            else if (installedBytes is null || !installedBytes.AsSpan().SequenceEqual(targetRead.Bytes))
            {
                WriteBundle(bundlePath, target);
                RestrictFile(bundlePath);
            }

            var verified = _counterStore.Validate(
                CounterPurpose, target.PolicyCounterInstanceId, counterScopePath, target.Generation, target.BundleDigestSha256);
            if (!verified.Accepted) return Reject(verified.ReasonCode, verified.Message);
            DeleteCommitIntent(intentPath);
            return new InstallationTrustResult(true, "OK", "Pending installation-trust commit recovered deterministically.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or SecurityException)
        {
            return Reject("INSTALLATION_TRUST_RECOVERY_AMBIGUOUS", ex.Message);
        }
    }

    private static InstallationTrustResult ValidateBundle(InstallationTrustBundleV1 bundle, string bundlePath, string counterScopePath)
    {
        if (!bundle.Schema.Equals("DCS_WATCHER_INSTALLATION_TRUST_V1", StringComparison.Ordinal) ||
            bundle.Version != 1 || bundle.Generation < 1 || !Guid.TryParseExact(bundle.InstallationId, "N", out _) ||
            !DateTimeOffset.TryParse(bundle.CreatedAtUtc, out _))
            return Reject("INSTALLATION_TRUST_SCHEMA_INVALID", "Installation trust schema, version, identity, generation, or timestamp is invalid.");
        if (!bundle.PolicyCounterInstanceId.Equals(CounterInstanceId(bundlePath), StringComparison.Ordinal) ||
            !bundle.PolicyCounterScopeIdentity.Equals(ScopeIdentity(counterScopePath), StringComparison.OrdinalIgnoreCase))
            return Reject("INSTALLATION_TRUST_COUNTER_IDENTITY_INVALID", "Installation trust counter identities do not match the fixed installation paths.");
        if (!ValidPublicKey(bundle.TrustRoot) || !bundle.TrustRoot.Status.Equals("active", StringComparison.Ordinal))
            return Reject("INSTALLATION_TRUST_ROOT_INVALID", "Installation trust-root public material is invalid.");
        if (bundle.PolicySigningKeys.Count == 0 ||
            bundle.PolicySigningKeys.GroupBy(key => key.KeyId, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            bundle.PolicySigningKeys.GroupBy(key => key.PublicKeyFingerprintSha256, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1) ||
            bundle.PolicySigningKeys.Any(key => !ValidPublicKey(key) || key.Generation < 1 || key.Generation > bundle.Generation ||
                key.Status is not ("active" or "retiring" or "revoked")))
            return Reject("INSTALLATION_POLICY_KEYS_INVALID", "Installation policy-signing key records are invalid or ambiguous.");
        if (bundle.PolicySigningKeys.Count(key => key.Status.Equals("active", StringComparison.Ordinal)) != 1)
            return Reject("INSTALLATION_POLICY_KEY_STATE_INVALID", "Exactly one installation policy-signing key must be active.");
        if (bundle.ApprovedDestinations.Count == 0 ||
            bundle.ApprovedDestinations.GroupBy(value => value.DestinationId, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            bundle.ApprovedDestinations.Any(value => string.IsNullOrWhiteSpace(value.DestinationId) || value.DestinationId.Length > 512 ||
                value.Status is not ("active" or "revoked")))
            return Reject("INSTALLATION_DESTINATIONS_INVALID", "Installation-approved destination records are invalid.");
        if (!bundle.SignatureAlgorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal))
            return Reject("INSTALLATION_TRUST_ALGORITHM_INVALID", "Installation trust uses an unsupported signature algorithm.");
        var expectedDigest = ComputeDigest(bundle);
        if (!expectedDigest.Equals(bundle.BundleDigestSha256, StringComparison.OrdinalIgnoreCase))
            return Reject("INSTALLATION_TRUST_DIGEST_INVALID", "Installation trust bundle digest is invalid.");
        try
        {
            var root = ToStage2Key(bundle.TrustRoot);
            if (!Stage2Crypto.Verify(root, SerializeUnsigned(bundle), Convert.FromBase64String(bundle.Signature)))
                return Reject("INSTALLATION_TRUST_SIGNATURE_INVALID", "Installation trust bundle signature is invalid.");
        }
        catch (FormatException)
        {
            return Reject("INSTALLATION_TRUST_SIGNATURE_INVALID", "Installation trust signature encoding is invalid.");
        }
        return new InstallationTrustResult(true, "OK", "Installation trust bundle is valid.");
    }

    private static void SignBundle(InstallationTrustBundleV1 bundle, IStage2ProvenanceSigner rootSigner)
    {
        if (!bundle.TrustRoot.KeyId.Equals(rootSigner.KeyId, StringComparison.Ordinal) ||
            !bundle.TrustRoot.PublicKeyFingerprintSha256.Equals(rootSigner.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The installation root signer does not match the trust bundle.");
        bundle.SignatureAlgorithm = Stage2InstructionProvenanceV1.AlgorithmName;
        bundle.BundleDigestSha256 = ComputeDigest(bundle);
        bundle.Signature = Convert.ToBase64String(rootSigner.Sign(SerializeUnsigned(bundle)));
    }

    private static string ComputeDigest(InstallationTrustBundleV1 bundle) =>
        Stage2Crypto.Sha256Hex(SerializeDigestInput(bundle));

    private static byte[] SerializeDigestInput(InstallationTrustBundleV1 bundle)
    {
        var clone = Clone(bundle);
        clone.BundleDigestSha256 = string.Empty;
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static byte[] SerializeUnsigned(InstallationTrustBundleV1 bundle)
    {
        var clone = Clone(bundle);
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static InstallationPublicKeyV1 PublicKey(IStage2ProvenanceSigner signer, long generation, DateTimeOffset nowUtc) => new()
    {
        KeyId = signer.KeyId,
        PublicKeySpkiBase64 = Convert.ToBase64String(signer.PublicKeySpki),
        PublicKeyFingerprintSha256 = signer.PublicKeyFingerprintSha256,
        Status = "active",
        Generation = generation,
        CreatedAtUtc = nowUtc.ToString("O"),
        ActivatedAtUtc = nowUtc.ToString("O")
    };

    private static bool ValidPublicKey(InstallationPublicKeyV1 key)
    {
        if (string.IsNullOrWhiteSpace(key.KeyId) || key.KeyId.Length > 256 ||
            !key.Algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal) ||
            key.PublicKeyFingerprintSha256.Length != 64 || !key.PublicKeyFingerprintSha256.All(Uri.IsHexDigit) ||
            !DateTimeOffset.TryParse(key.CreatedAtUtc, out _) || !DateTimeOffset.TryParse(key.ActivatedAtUtc, out _))
            return false;
        try
        {
            return Stage2Crypto.Sha256Hex(Convert.FromBase64String(key.PublicKeySpkiBase64))
                .Equals(key.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException) { return false; }
    }

    private static Stage2PublicKeyRecord ToStage2Key(InstallationPublicKeyV1 key) => new()
    {
        KeyId = key.KeyId,
        Algorithm = key.Algorithm,
        PublicKeySpkiBase64 = key.PublicKeySpkiBase64,
        PublicKeyFingerprintSha256 = key.PublicKeyFingerprintSha256,
        Status = key.Status
    };

    private static void WriteBundle(string path, InstallationTrustBundleV1 bundle) =>
        DurableWriteAllBytes(path, Serialize(bundle));

    private static void WriteScopeMarker(string path, string installationId) =>
        DurableWriteAllBytes(path, Encoding.UTF8.GetBytes(installationId));

    private static void WriteCommitIntent(
        string path,
        InstallationTrustBundleV1? previous,
        InstallationTrustBundleV1 target,
        DateTimeOffset nowUtc)
    {
        var intent = new InstallationTrustCommitIntent
        {
            PreviousGeneration = previous?.Generation ?? 0,
            PreviousDigestSha256 = previous?.BundleDigestSha256 ?? string.Empty,
            PreviousBundleBase64 = previous is null ? string.Empty : Convert.ToBase64String(Serialize(previous)),
            TargetBundleBase64 = Convert.ToBase64String(Serialize(target)),
            CreatedAtUtc = nowUtc.ToUniversalTime().ToString("O")
        };
        DurableWriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(intent, Stage2CanonicalJson.Options));
    }

    private static void DeleteCommitIntent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static (InstallationTrustBundleV1? Bundle, byte[]? Bytes) ReadCanonicalBundle(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var bundle = JsonSerializer.Deserialize<InstallationTrustBundleV1>(bytes, Stage2CanonicalJson.Options);
            return bundle is not null && bytes.AsSpan().SequenceEqual(Serialize(bundle))
                ? (bundle, bytes)
                : (null, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return (null, null);
        }
    }

    private static bool SameTrustIdentity(InstallationTrustBundleV1 previous, InstallationTrustBundleV1 target) =>
        previous.InstallationId.Equals(target.InstallationId, StringComparison.Ordinal) &&
        previous.PolicyCounterInstanceId.Equals(target.PolicyCounterInstanceId, StringComparison.Ordinal) &&
        previous.PolicyCounterScopeIdentity.Equals(target.PolicyCounterScopeIdentity, StringComparison.OrdinalIgnoreCase) &&
        Serialize(previous.TrustRoot).AsSpan().SequenceEqual(Serialize(target.TrustRoot));

    private static byte[] Serialize(InstallationPublicKeyV1 key) =>
        JsonSerializer.SerializeToUtf8Bytes(key, Stage2CanonicalJson.Options);

    private static byte[] Serialize(InstallationTrustBundleV1 bundle) =>
        JsonSerializer.SerializeToUtf8Bytes(bundle, Stage2CanonicalJson.Options);

    private static InstallationTrustBundleV1 Clone(InstallationTrustBundleV1 bundle) =>
        JsonSerializer.Deserialize<InstallationTrustBundleV1>(Serialize(bundle), Stage2CanonicalJson.Options)!;

    private static string CounterInstanceId(string bundlePath) =>
        "watcher-installation-trust-" + Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(bundlePath).ToUpperInvariant()));

    private static string ScopeIdentity(string scopePath) =>
        Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(scopePath).ToUpperInvariant()));

    private static string RootCngKeyName(string installationId) => $"Watcher-Installation-{installationId}-Root";
    private static string PolicyCngKeyName(string installationId, long generation) => $"Watcher-Installation-{installationId}-Policy-G{generation}";

    private static (bool Accepted, string ReasonCode, string Message, string? Root) ResolveSecurityRoot(string? requested)
    {
        string root;
        try
        {
            root = ResolveReparsePoints(Path.GetFullPath(string.IsNullOrWhiteSpace(requested) ? DefaultSecurityRoot : requested));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            return (false, "INSTALLATION_TRUST_PATH_AMBIGUOUS", $"The installation security path could not be resolved: {ex.Message}", null);
        }
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return (false, "INSTALLATION_TRUST_SOURCE_PATH_REJECTED", "Installation trust cannot be stored inside a source-control worktree.", null);
        }
        return (true, "OK", "Security root is valid.", root);
    }

    private static string ResolveReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath) ?? throw new IOException("The security path has no volume root.");
        var current = volumeRoot;
        foreach (var segment in fullPath[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment)) continue;
            var candidate = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (FileNotFoundException)
            {
                current = candidate;
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                current = candidate;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = candidate;
                continue;
            }

            FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            var target = entry.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException($"Reparse point has no resolvable target: {candidate}");
            current = Path.GetFullPath(target.FullName);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static InstallationTrustResult ValidateAcl(string root, params string[] files)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid is null) return Reject("INSTALLATION_TRUST_ACL_INVALID", "The current Windows SID is unavailable.");
        var trusted = new HashSet<string>(StringComparer.Ordinal)
        {
            sid.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };
        var directorySecurity = new DirectoryInfo(root).GetAccessControl();
        if (!AclIsAllowlisted(directorySecurity, sid, trusted))
            return Reject("INSTALLATION_TRUST_ACL_INVALID", "The installation security directory ACL is not restricted to trusted local principals.");
        foreach (var file in files)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                return Reject("INSTALLATION_TRUST_ACL_INVALID", $"Installation trust file is a reparse point: {Path.GetFileName(file)}");
            var security = new FileInfo(file).GetAccessControl();
            if (!AclIsAllowlisted(security, sid, trusted))
                return Reject("INSTALLATION_TRUST_ACL_INVALID", $"Installation trust file ACL is not restricted: {Path.GetFileName(file)}");
        }
        return new InstallationTrustResult(true, "OK", "Installation trust ACLs are restricted.");
    }

    private static bool AclIsAllowlisted(FileSystemSecurity security, SecurityIdentifier currentUser, HashSet<string> trusted)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value) ||
            !HasFullControl(security, currentUser))
            return false;
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().All(rule =>
            rule.AccessControlType != AccessControlType.Allow ||
            rule.IdentityReference is SecurityIdentifier ruleSid && trusted.Contains(ruleSid.Value));
    }

    private static bool HasFullControl(FileSystemSecurity security, SecurityIdentifier sid) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
            rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

    private static void RestrictDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new SecurityException("Current Windows SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        AddFullControl(security, sid, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestrictFile(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new SecurityException("Current Windows SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        AddFullControl(security, sid, InheritanceFlags.None);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), InheritanceFlags.None);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), InheritanceFlags.None);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFullControl(FileSystemSecurity security, SecurityIdentifier sid, InheritanceFlags inheritance) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static CommitTransactionLock AcquireTransactionLock(string securityRoot)
    {
        var digest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(securityRoot).ToUpperInvariant()));
        var mutex = new Mutex(false, $@"Local\DcsWatcherV2.InstallationTrust.Commit.{digest}");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Timed out waiting for the installation-trust commit lock.");
            return new CommitTransactionLock(mutex);
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private void Interrupt(string step)
    {
        if (FaultStopAfterStep?.Equals(step, StringComparison.Ordinal) == true)
            throw new IOException($"Synthetic installation-trust interruption at {step}.");
    }

    private static void DurableWriteAllBytes(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!MoveFileEx(temporary, path, MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
                throw new IOException($"Durable installation-trust replacement failed for {Path.GetFileName(path)}.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,
        WriteThrough = 0x8
    }

    private sealed class InstallationTrustCommitIntent
    {
        public string Schema { get; set; } = "DCS_WATCHER_INSTALLATION_TRUST_COMMIT_V1";
        public int Version { get; set; } = 1;
        public long PreviousGeneration { get; set; }
        public string PreviousDigestSha256 { get; set; } = string.Empty;
        public string PreviousBundleBase64 { get; set; } = string.Empty;
        public string TargetBundleBase64 { get; set; } = string.Empty;
        public string CreatedAtUtc { get; set; } = string.Empty;
    }

    private sealed class CommitTransactionLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static InstallationTrustResult Reject(string code, string message) => new(false, code, message);
}
