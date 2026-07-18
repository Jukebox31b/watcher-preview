using System.Security.Cryptography;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public interface IStage2ProvenanceSigner : IDisposable
{
    string KeyId { get; }
    string PublicKeyFingerprintSha256 { get; }
    byte[] PublicKeySpki { get; }
    byte[] Sign(byte[] canonicalUnsignedProvenance);
}

public interface IStage2PublicKeyResolver
{
    Stage2PublicKeyRecord? Find(string keyId);
}

public sealed class EphemeralStage2ProvenanceSigner : IStage2ProvenanceSigner
{
    private readonly ECDsa _key;

    public EphemeralStage2ProvenanceSigner(string? keyId = null)
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyId = keyId ?? $"test-{Guid.NewGuid():N}";
        PublicKeySpki = _key.ExportSubjectPublicKeyInfo();
        PublicKeyFingerprintSha256 = Stage2Crypto.Sha256Hex(PublicKeySpki);
    }

    public string KeyId { get; }
    public string PublicKeyFingerprintSha256 { get; }
    public byte[] PublicKeySpki { get; }

    public byte[] Sign(byte[] canonicalUnsignedProvenance)
    {
        return _key.SignData(canonicalUnsignedProvenance, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public void Dispose() => _key.Dispose();
}

public sealed class WindowsCngStage2ProvenanceSigner : IStage2ProvenanceSigner
{
    private readonly ECDsaCng _key;

    private WindowsCngStage2ProvenanceSigner(string keyId, ECDsaCng key)
    {
        ValidateProtectedKey(key.Key);
        KeyId = keyId;
        _key = key;
        PublicKeySpki = _key.ExportSubjectPublicKeyInfo();
        PublicKeyFingerprintSha256 = Stage2Crypto.Sha256Hex(PublicKeySpki);
    }

    public string KeyId { get; }
    public string PublicKeyFingerprintSha256 { get; }
    public byte[] PublicKeySpki { get; }

    public static WindowsCngStage2ProvenanceSigner OpenOrCreate(string keyId, string cngKeyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The protected production signer requires Windows CNG.");
        }

        CngKey cngKey;
        if (CngKey.Exists(cngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
        {
            cngKey = CngKey.Open(cngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
        }
        else
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = CngKeyCreationOptions.None
            };
            cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, cngKeyName, parameters);
        }

        return new WindowsCngStage2ProvenanceSigner(keyId, new ECDsaCng(cngKey));
    }

    public static WindowsCngStage2ProvenanceSigner OpenExisting(string keyId, string cngKeyName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The protected signer requires Windows CNG.");
        if (!CngKey.Exists(cngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
            throw new CryptographicException($"Required Windows CNG key does not exist: {cngKeyName}");
        var cngKey = CngKey.Open(cngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
        return new WindowsCngStage2ProvenanceSigner(keyId, new ECDsaCng(cngKey));
    }

    public byte[] Sign(byte[] canonicalUnsignedProvenance)
    {
        return _key.SignData(canonicalUnsignedProvenance, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public void Dispose() => _key.Dispose();

    private static void ValidateProtectedKey(CngKey key)
    {
        if (key.Algorithm != CngAlgorithm.ECDsaP256)
        {
            throw new CryptographicException("Existing CNG key is not ECDSA P-256.");
        }

        if ((key.KeyUsage & CngKeyUsages.Signing) == 0 || key.ExportPolicy != CngExportPolicies.None)
        {
            throw new CryptographicException("CNG key must be signing-enabled and private-key export must be disabled.");
        }
    }
}

public sealed class Stage2PublicKeyRegistry : IStage2PublicKeyResolver
{
    private readonly string _path;

    public Stage2PublicKeyRegistry(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public Stage2PublicKeyRegistryV1 Load()
    {
        if (!File.Exists(_path))
        {
            return new Stage2PublicKeyRegistryV1();
        }

        var bytes = File.ReadAllBytes(_path);
        return JsonSerializer.Deserialize<Stage2PublicKeyRegistryV1>(bytes, Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("Public-key registry is empty.");
    }

    public void AddOrUpdate(IStage2ProvenanceSigner signer, string cngKeyName = "", DateTimeOffset? now = null)
    {
        var document = Load();
        var matches = document.Keys.Where(key => key.KeyId.Equals(signer.KeyId, StringComparison.Ordinal)).ToList();
        if (matches.Count > 1)
        {
            throw new InvalidDataException($"Duplicate public-key registry ID: {signer.KeyId}");
        }

        var existing = matches.SingleOrDefault();
        if (existing is not null)
        {
            if (!existing.Status.Equals("active", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Revoked key ID cannot be reactivated: {signer.KeyId}");
            }

            if (!existing.PublicKeyFingerprintSha256.Equals(signer.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
                !existing.PublicKeySpkiBase64.Equals(Convert.ToBase64String(signer.PublicKeySpki), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Key ID cannot be rebound to different public material: {signer.KeyId}");
            }
        }

        var record = existing ?? new Stage2PublicKeyRecord { KeyId = signer.KeyId };
        record.Algorithm = Stage2InstructionProvenanceV1.AlgorithmName;
        record.PublicKeySpkiBase64 = Convert.ToBase64String(signer.PublicKeySpki);
        record.PublicKeyFingerprintSha256 = signer.PublicKeyFingerprintSha256;
        record.Status = existing?.Status ?? "active";
        record.CreatedAtUtc = existing?.CreatedAtUtc is { Length: > 0 } created ? created : (now ?? DateTimeOffset.UtcNow).ToString("O");
        record.RevokedAtUtc = existing?.RevokedAtUtc ?? string.Empty;
        record.CngKeyName = cngKeyName;
        if (existing is null)
        {
            document.Keys.Add(record);
        }

        Save(document);
    }

    public void Revoke(string keyId, DateTimeOffset? now = null)
    {
        var document = Load();
        var record = document.Keys.SingleOrDefault(key => key.KeyId.Equals(keyId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown key ID: {keyId}");
        record.Status = "revoked";
        record.RevokedAtUtc = (now ?? DateTimeOffset.UtcNow).ToString("O");
        Save(document);
    }

    public Stage2PublicKeyRecord? Find(string keyId)
    {
        var matches = Load().Keys.Where(key => key.KeyId.Equals(keyId, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private void Save(Stage2PublicKeyRegistryV1 document)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Stage2CanonicalJson.Options);
        Stage2AtomicFile.WriteAllBytes(_path, bytes);
    }
}

public static class Stage2Crypto
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static bool Verify(Stage2PublicKeyRecord record, byte[] data, byte[] signature)
    {
        try
        {
            if (!record.Algorithm.Equals(Stage2InstructionProvenanceV1.AlgorithmName, StringComparison.Ordinal))
            {
                return false;
            }

            var publicBytes = Convert.FromBase64String(record.PublicKeySpkiBase64);
            if (!Sha256Hex(publicBytes).Equals(record.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicBytes, out var consumed);
            var parameters = key.ExportParameters(false);
            return consumed == publicBytes.Length &&
                   key.KeySize == 256 &&
                   parameters.Curve.Oid.Value == "1.2.840.10045.3.1.7" &&
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal static class Stage2AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
