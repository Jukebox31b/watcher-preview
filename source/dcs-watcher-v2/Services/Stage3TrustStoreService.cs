using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record Stage3TrustResult(bool Accepted, string ReasonCode, string Message, long Generation = 0, Stage3TrustedKeyRecord? Key = null);

public sealed class Stage3TrustStoreService : IStage2PublicKeyResolver
{
    private readonly string _trustStorePath;
    private readonly string _trustRootPath;
    private readonly string _anchorPath;
    private readonly IStage2ProvenanceSigner? _rootSigner;
    private readonly DateTimeOffset _evaluationTimeUtc;
    private readonly Stage3WindowsMonotonicCounterStore _externalCounter = new();

    public Stage3TrustStoreService(
        string trustStorePath,
        string trustRootPath,
        string anchorPath,
        IStage2ProvenanceSigner? rootSigner,
        DateTimeOffset? evaluationTimeUtc = null)
    {
        _trustStorePath = Path.GetFullPath(trustStorePath);
        _trustRootPath = Path.GetFullPath(trustRootPath);
        _anchorPath = Path.GetFullPath(anchorPath);
        _rootSigner = rootSigner;
        _evaluationTimeUtc = (evaluationTimeUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
    }

    public string TrustStorePath => _trustStorePath;
    public string TrustRootPath => _trustRootPath;
    public string AnchorPath => _anchorPath;

    public Stage3TrustResult Initialize(
        string trustStoreInstanceId,
        IReadOnlyList<Stage3TrustedKeyRecord> initialKeys,
        DateTimeOffset nowUtc)
    {
        if (_rootSigner is null)
            return Reject("TRUST_ROOT_PRIVATE_KEY_UNAVAILABLE", "Trust-store initialization requires the separate trust-root signer.");
        if (File.Exists(_trustStorePath) || File.Exists(_anchorPath))
            return Validate(nowUtc);
        if (initialKeys.GroupBy(key => key.KeyId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return Reject("KEY_ID_COLLISION", "Initial trust store contains duplicate key IDs.");

        var root = new Stage3TrustRootPublicV1
        {
            KeyId = _rootSigner.KeyId,
            PublicKeySpkiBase64 = Convert.ToBase64String(_rootSigner.PublicKeySpki),
            PublicKeyFingerprintSha256 = _rootSigner.PublicKeyFingerprintSha256
        };
        WriteCanonicalAtomic(_trustRootPath, root);

        var store = new Stage3TrustStoreV1
        {
            TrustStoreInstanceId = trustStoreInstanceId,
            TrustGeneration = 1,
            Keys = initialKeys.Select(Clone).ToList(),
            RootSignerKeyId = _rootSigner.KeyId
        };
        var prospective = ValidateKeyRecords(store);
        if (!prospective.Accepted) return prospective;
        SignStore(store);
        WriteCanonicalAtomic(_trustStorePath, store);
        var anchorWrite = WriteAnchor(store, nowUtc);
        if (!anchorWrite.Accepted) return anchorWrite;
        return Validate(nowUtc);
    }

    public Stage3TrustResult AddPendingKey(
        Stage3TrustedKeyRecord pendingKey,
        DateTimeOffset nowUtc)
    {
        if (!pendingKey.Status.Equals("pending", StringComparison.Ordinal))
            return Reject("KEY_STATUS_INVALID", "New keys must enter the trust store as pending.");
        return Mutate(nowUtc, store =>
        {
            if (store.Keys.Any(key => key.KeyId.Equals(pendingKey.KeyId, StringComparison.Ordinal)))
                return "KEY_ID_COLLISION";
            if (store.Keys.Any(key => key.PublicKeyFingerprintSha256.Equals(pendingKey.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase)))
                return "KEY_FINGERPRINT_COLLISION";
            store.Keys.Add(Clone(pendingKey));
            return string.Empty;
        });
    }

    public Stage3TrustResult TransitionKey(
        string keyId,
        string nextStatus,
        DateTimeOffset nowUtc,
        string revocationReason = "",
        DateTimeOffset? retiringOverlapEndsUtc = null)
    {
        return Mutate(nowUtc, store =>
        {
            var matches = store.Keys.Where(key => key.KeyId.Equals(keyId, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1) return "KEY_UNKNOWN_OR_DUPLICATE";
            var key = matches[0];
            var legal = key.Status switch
            {
                "pending" => nextStatus is "active" or "revoked",
                "active" => nextStatus is "retiring" or "revoked",
                "retiring" => nextStatus is "revoked",
                "revoked" => false,
                _ => false
            };
            if (!legal) return "KEY_STATUS_TRANSITION_INVALID";
            if (nextStatus == "revoked" && string.IsNullOrWhiteSpace(revocationReason)) return "REVOCATION_REASON_REQUIRED";
            if (nextStatus == "retiring" && retiringOverlapEndsUtc is null) return "RETIRING_OVERLAP_REQUIRED";
            key.Status = nextStatus;
            key.RevocationReason = nextStatus == "revoked" ? revocationReason : key.RevocationReason;
            key.RetiringOverlapEndsUtc = nextStatus == "retiring" ? retiringOverlapEndsUtc!.Value.ToUniversalTime().ToString("O") : key.RetiringOverlapEndsUtc;
            return string.Empty;
        });
    }

    public Stage3TrustResult EmergencyRevoke(string keyId, string reason, DateTimeOffset nowUtc)
    {
        return TransitionKey(keyId, "revoked", nowUtc, reason);
    }

    public Stage3TrustResult Validate(DateTimeOffset nowUtc)
    {
        if (!File.Exists(_trustRootPath)) return Reject("TRUST_ROOT_MISSING", "Pinned trust-root public material is missing.");
        if (!File.Exists(_trustStorePath)) return Reject("TRUST_STORE_MISSING", "Trust store is missing.");
        if (!File.Exists(_anchorPath)) return Reject("TRUST_ANCHOR_MISSING", "Trust-generation anchor is missing.");

        var rootBytes = File.ReadAllBytes(_trustRootPath);
        var root = Deserialize<Stage3TrustRootPublicV1>(rootBytes);
        if (root is null || !rootBytes.AsSpan().SequenceEqual(Serialize(root)) ||
            !root.Schema.Equals("DCS_WATCHER_TRUST_ROOT_PUBLIC_V1", StringComparison.Ordinal) ||
            !root.Algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal) ||
            !PublicMaterialValid(root.PublicKeySpkiBase64, root.PublicKeyFingerprintSha256))
            return Reject("TRUST_ROOT_INVALID", "Pinned trust-root material is invalid or noncanonical.");

        var bytes = File.ReadAllBytes(_trustStorePath);
        var store = Deserialize<Stage3TrustStoreV1>(bytes);
        if (store is null || !bytes.AsSpan().SequenceEqual(Serialize(store)))
            return Reject("TRUST_STORE_NONCANONICAL", "Trust store is invalid or noncanonical.");
        if (!store.Schema.Equals("DCS_WATCHER_CODEX_TRUST_STORE_V1", StringComparison.Ordinal) || store.Version != 1 || store.TrustGeneration < 1)
            return Reject("TRUST_STORE_SCHEMA_INVALID", "Trust-store schema, version, or generation is invalid.");
        if (!store.RootSignerKeyId.Equals(root.KeyId, StringComparison.Ordinal))
            return Reject("TRUST_ROOT_ID_MISMATCH", "Trust store is signed by another root ID.");
        if (store.Keys.GroupBy(key => key.KeyId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return Reject("KEY_ID_COLLISION", "Trust store has duplicate key IDs.");
        if (store.Keys.GroupBy(key => key.PublicKeyFingerprintSha256, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            return Reject("KEY_FINGERPRINT_COLLISION", "Trust store has duplicate public-key fingerprints.");

        var expectedDigest = ComputeStoreDigest(store);
        if (!expectedDigest.Equals(store.TrustStoreDigest, StringComparison.OrdinalIgnoreCase))
            return Reject("TRUST_STORE_DIGEST_INVALID", "Trust-store digest is invalid.");
        var rootRecord = new Stage2PublicKeyRecord
        {
            KeyId = root.KeyId,
            Algorithm = root.Algorithm,
            PublicKeySpkiBase64 = root.PublicKeySpkiBase64,
            PublicKeyFingerprintSha256 = root.PublicKeyFingerprintSha256,
            Status = "active"
        };
        if (!Verify(rootRecord, SerializeUnsigned(store), store.Signature))
            return Reject("TRUST_STORE_SIGNATURE_INVALID", "Trust-store signature is invalid.");

        var anchorBytes = File.ReadAllBytes(_anchorPath);
        var anchor = Deserialize<Stage3MonotonicAnchorV1>(anchorBytes);
        if (anchor is null || !anchorBytes.AsSpan().SequenceEqual(Serialize(anchor)) ||
            !anchor.AnchorPurpose.Equals("trust-store", StringComparison.Ordinal) ||
            !anchor.ObjectInstanceId.Equals(store.TrustStoreInstanceId, StringComparison.Ordinal))
            return Reject("TRUST_ANCHOR_INVALID", "Trust-generation anchor is invalid.");
        if (!Verify(rootRecord, SerializeUnsigned(anchor), anchor.Signature))
            return Reject("TRUST_ANCHOR_SIGNATURE_INVALID", "Trust-generation anchor signature is invalid.");
        if (anchor.MaximumGeneration > store.TrustGeneration)
            return Reject("TRUST_GENERATION_ROLLBACK", "Trust store is older than the signed monotonic anchor.");
        if (anchor.MaximumGeneration < store.TrustGeneration)
            return Reject("TRUST_ANCHOR_STALE", "Trust store is newer than its anchor; recovery is required.");
        if (!anchor.ObjectDigest.Equals(Stage2Crypto.Sha256Hex(bytes), StringComparison.OrdinalIgnoreCase))
            return Reject("TRUST_ANCHOR_DIGEST_MISMATCH", "Trust anchor does not bind the exact trust-store bytes.");
        var external = _externalCounter.Validate("trust-store", store.TrustStoreInstanceId, _anchorPath,
            store.TrustGeneration, anchor.ObjectDigest);
        if (!external.Accepted) return Reject(external.ReasonCode, external.Message);
        var keyValidation = ValidateKeyRecords(store);
        if (!keyValidation.Accepted) return keyValidation;
        _ = nowUtc;
        return new Stage3TrustResult(true, "OK", "Trust store, root signature, and rollback anchor are valid.", store.TrustGeneration);
    }

    public Stage3TrustResult EvaluateKey(string keyId, string purpose, long watcherBuildGeneration, DateTimeOffset nowUtc)
    {
        var valid = Validate(nowUtc);
        if (!valid.Accepted) return valid;
        var store = ReadStore();
        var matches = store.Keys.Where(key => key.KeyId.Equals(keyId, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) return Reject("TRUSTED_KEY_UNKNOWN", "Key ID is not uniquely trusted.");
        var key = matches[0];
        if (!key.Purpose.Equals(purpose, StringComparison.Ordinal)) return Reject("KEY_PURPOSE_INVALID", "Key is not authorized for this purpose.");
        if (key.MinimumAcceptedWatcherBuildGeneration > watcherBuildGeneration) return Reject("BUILD_GENERATION_BELOW_KEY_POLICY", "Watcher build generation is below key policy.");
        if (key.Status == "pending") return Reject("KEY_PENDING", "Pending key is not accepted.");
        if (key.Status == "revoked") return Reject("KEY_REVOKED", "Revoked key is not accepted.");
        if (!string.IsNullOrWhiteSpace(key.ExpirationUtc) &&
            (!DateTimeOffset.TryParse(key.ExpirationUtc, out var expiration) || nowUtc.ToUniversalTime() > expiration.ToUniversalTime()))
            return Reject("KEY_EXPIRED", "Trusted key has expired.");
        if (!DateTimeOffset.TryParse(key.ActivationUtc, out var activation))
            return Reject("TRUSTED_KEY_TIME_INVALID", "Trusted key activation timestamp is malformed.");
        if (nowUtc.ToUniversalTime() < activation.ToUniversalTime())
            return Reject("KEY_NOT_ACTIVE_YET", "Trusted key activation time has not arrived.");
        if (key.Status == "retiring")
        {
            if (!DateTimeOffset.TryParse(key.RetiringOverlapEndsUtc, out var overlap) || nowUtc.ToUniversalTime() > overlap.ToUniversalTime())
                return Reject("KEY_RETIRED", "Retiring-key overlap has ended.");
        }
        return new Stage3TrustResult(true, "OK", "Trusted key is eligible.", store.TrustGeneration, Clone(key));
    }

    public Stage2PublicKeyRecord? Find(string keyId)
    {
        var result = EvaluateKey(keyId, "provenance", long.MaxValue, _evaluationTimeUtc);
        if (!result.Accepted || result.Key is null) return null;
        return new Stage2PublicKeyRecord
        {
            KeyId = result.Key.KeyId,
            Algorithm = result.Key.Algorithm,
            PublicKeySpkiBase64 = result.Key.PublicKeySpkiBase64,
            PublicKeyFingerprintSha256 = result.Key.PublicKeyFingerprintSha256,
            Status = "active",
            CreatedAtUtc = result.Key.ActivationUtc
        };
    }

    public Stage2PublicKeyRecord? FindForPurpose(string keyId, string purpose, long buildGeneration, DateTimeOffset nowUtc)
    {
        var result = EvaluateKey(keyId, purpose, buildGeneration, nowUtc);
        if (!result.Accepted || result.Key is null) return null;
        return new Stage2PublicKeyRecord
        {
            KeyId = result.Key.KeyId,
            Algorithm = result.Key.Algorithm,
            PublicKeySpkiBase64 = result.Key.PublicKeySpkiBase64,
            PublicKeyFingerprintSha256 = result.Key.PublicKeyFingerprintSha256,
            Status = "active",
            CreatedAtUtc = result.Key.ActivationUtc
        };
    }

    public Stage3TrustStoreV1 ReadVerifiedStore(DateTimeOffset nowUtc)
    {
        var result = Validate(nowUtc);
        if (!result.Accepted) throw new InvalidDataException($"{result.ReasonCode}: {result.Message}");
        return ReadStore();
    }

    private Stage3TrustResult Mutate(DateTimeOffset nowUtc, Func<Stage3TrustStoreV1, string> mutation)
    {
        if (_rootSigner is null) return Reject("TRUST_ROOT_PRIVATE_KEY_UNAVAILABLE", "Trust-store change requires the separate trust-root signer.");
        var valid = Validate(nowUtc);
        if (!valid.Accepted) return valid;
        var store = ReadStore();
        var previousDigest = store.TrustStoreDigest;
        var error = mutation(store);
        if (error.Length > 0) return Reject(error, "Trust-store mutation was rejected.");
        store.TrustGeneration++;
        store.PreviousTrustStoreDigest = previousDigest;
        var prospective = ValidateKeyRecords(store);
        if (!prospective.Accepted) return prospective;
        SignStore(store);
        WriteCanonicalAtomic(_trustStorePath, store);
        var anchorWrite = WriteAnchor(store, nowUtc);
        if (!anchorWrite.Accepted) return anchorWrite;
        return Validate(nowUtc);
    }

    private void SignStore(Stage3TrustStoreV1 store)
    {
        store.TrustStoreDigest = ComputeStoreDigest(store);
        store.Signature = string.Empty;
        store.Signature = Convert.ToBase64String(_rootSigner!.Sign(SerializeUnsigned(store)));
    }

    private Stage3TrustResult WriteAnchor(Stage3TrustStoreV1 store, DateTimeOffset nowUtc)
    {
        var storeBytes = Serialize(store);
        var anchor = new Stage3MonotonicAnchorV1
        {
            AnchorPurpose = "trust-store",
            ObjectInstanceId = store.TrustStoreInstanceId,
            MaximumGeneration = store.TrustGeneration,
            ObjectDigest = Stage2Crypto.Sha256Hex(storeBytes),
            IssuedAtUtc = nowUtc.ToUniversalTime().ToString("O"),
            SignerKeyId = _rootSigner!.KeyId
        };
        anchor.Signature = Convert.ToBase64String(_rootSigner.Sign(SerializeUnsigned(anchor)));
        WriteCanonicalAtomic(_anchorPath, anchor);
        var external = _externalCounter.Advance("trust-store", store.TrustStoreInstanceId, _anchorPath,
            store.TrustGeneration, anchor.ObjectDigest, nowUtc);
        return external.Accepted
            ? new Stage3TrustResult(true, "OK", "Trust anchor and external monotonic counter were committed.", store.TrustGeneration)
            : Reject(external.ReasonCode, external.Message);
    }

    private static Stage3TrustResult ValidateKeyRecords(Stage3TrustStoreV1 store)
    {
        if (store.Keys.GroupBy(key => key.KeyId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return Reject("KEY_ID_COLLISION", "Trust store has duplicate key IDs.");
        if (store.Keys.GroupBy(key => key.PublicKeyFingerprintSha256, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            return Reject("KEY_FINGERPRINT_COLLISION", "Trust store has duplicate public-key fingerprints.");
        foreach (var key in store.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.Purpose) ||
                !key.Algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal) ||
                !PublicMaterialValid(key.PublicKeySpkiBase64, key.PublicKeyFingerprintSha256) ||
                key.Status is not ("pending" or "active" or "retiring" or "revoked"))
                return Reject("TRUSTED_KEY_INVALID", $"Trusted key record is invalid: {key.KeyId}");
            if (key.Status == "revoked" && string.IsNullOrWhiteSpace(key.RevocationReason))
                return Reject("REVOCATION_RECORD_INVALID", $"Revoked key lacks a reason: {key.KeyId}");
            if (!DateTimeOffset.TryParse(key.ActivationUtc, out _) ||
                (!string.IsNullOrWhiteSpace(key.ExpirationUtc) && !DateTimeOffset.TryParse(key.ExpirationUtc, out _)) ||
                (key.Status == "retiring" && !DateTimeOffset.TryParse(key.RetiringOverlapEndsUtc, out _)))
                return Reject("TRUSTED_KEY_TIME_INVALID", $"Trusted key has a malformed lifecycle timestamp: {key.KeyId}");
        }
        return new Stage3TrustResult(true, "OK", "Trusted key records are structurally valid.", store.TrustGeneration);
    }

    private static string ComputeStoreDigest(Stage3TrustStoreV1 store)
    {
        var clone = Clone(store);
        clone.TrustStoreDigest = string.Empty;
        clone.Signature = string.Empty;
        return Stage2Crypto.Sha256Hex(Serialize(clone));
    }

    private Stage3TrustStoreV1 ReadStore() => Deserialize<Stage3TrustStoreV1>(File.ReadAllBytes(_trustStorePath))!;

    private static bool Verify(Stage2PublicKeyRecord key, byte[] bytes, string signature)
    {
        try { return Stage2Crypto.Verify(key, bytes, Convert.FromBase64String(signature)); }
        catch (FormatException) { return false; }
    }

    private static byte[] SerializeUnsigned(Stage3TrustStoreV1 value)
    {
        var clone = Clone(value);
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static byte[] SerializeUnsigned(Stage3MonotonicAnchorV1 value)
    {
        var clone = Clone(value);
        clone.Signature = string.Empty;
        return Serialize(clone);
    }

    private static void WriteCanonicalAtomic<T>(string path, T value)
    {
        var bytes = Serialize(value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stage2AtomicFile.WriteAllBytes(path, bytes);
    }

    private static T Clone<T>(T value) => Deserialize<T>(Serialize(value))!;
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Stage2CanonicalJson.Options);
    private static T? Deserialize<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Stage2CanonicalJson.Options); }
        catch (JsonException) { return default; }
        catch (FormatException) { return default; }
    }
    private static bool PublicMaterialValid(string encoded, string fingerprint)
    {
        try
        {
            return Stage2Crypto.Sha256Hex(Convert.FromBase64String(encoded)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
    private static Stage3TrustResult Reject(string code, string message) => new(false, code, message);
}

public sealed class Stage3PurposeKeyResolver : IStage2PublicKeyResolver
{
    private readonly Stage3TrustStoreService _trustStore;
    private readonly string _purpose;
    private readonly long _buildGeneration;
    private readonly DateTimeOffset _nowUtc;

    public Stage3PurposeKeyResolver(Stage3TrustStoreService trustStore, string purpose, long buildGeneration, DateTimeOffset nowUtc)
    {
        _trustStore = trustStore;
        _purpose = purpose;
        _buildGeneration = buildGeneration;
        _nowUtc = nowUtc;
    }

    public Stage2PublicKeyRecord? Find(string keyId) => _trustStore.FindForPurpose(keyId, _purpose, _buildGeneration, _nowUtc);
}
