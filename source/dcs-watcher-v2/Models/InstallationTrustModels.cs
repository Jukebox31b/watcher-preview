using System.Text.Json.Serialization;

namespace DcsWatcherV2.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InstallationTrustBundleV1
{
    [JsonPropertyOrder(0), JsonPropertyName("schema")]
    public string Schema { get; set; } = "DCS_WATCHER_INSTALLATION_TRUST_V1";

    [JsonPropertyOrder(1), JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2), JsonPropertyName("installation_id")]
    public string InstallationId { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("generation")]
    public long Generation { get; set; } = 1;

    [JsonPropertyOrder(4), JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(5), JsonPropertyName("rotated_at_utc")]
    public string RotatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(6), JsonPropertyName("trust_root")]
    public InstallationPublicKeyV1 TrustRoot { get; set; } = new();

    [JsonPropertyOrder(7), JsonPropertyName("policy_signing_keys")]
    public List<InstallationPublicKeyV1> PolicySigningKeys { get; set; } = [];

    [JsonPropertyOrder(8), JsonPropertyName("approved_destinations")]
    public List<InstallationDestinationV1> ApprovedDestinations { get; set; } = [];

    [JsonPropertyOrder(9), JsonPropertyName("policy_counter_instance_id")]
    public string PolicyCounterInstanceId { get; set; } = string.Empty;

    [JsonPropertyOrder(10), JsonPropertyName("policy_counter_scope_identity")]
    public string PolicyCounterScopeIdentity { get; set; } = string.Empty;

    [JsonPropertyOrder(11), JsonPropertyName("bundle_digest_sha256")]
    public string BundleDigestSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(12), JsonPropertyName("signature_algorithm")]
    public string SignatureAlgorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;

    [JsonPropertyOrder(13), JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InstallationPublicKeyV1
{
    [JsonPropertyOrder(0), JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(1), JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = Stage2InstructionProvenanceV1.AlgorithmName;

    [JsonPropertyOrder(2), JsonPropertyName("public_key_spki_base64")]
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("public_key_fingerprint_sha256")]
    public string PublicKeyFingerprintSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(4), JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyOrder(5), JsonPropertyName("generation")]
    public long Generation { get; set; } = 1;

    [JsonPropertyOrder(6), JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(7), JsonPropertyName("activated_at_utc")]
    public string ActivatedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(8), JsonPropertyName("revoked_at_utc")]
    public string RevokedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(9), JsonPropertyName("revocation_reason")]
    public string RevocationReason { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InstallationDestinationV1
{
    [JsonPropertyOrder(0), JsonPropertyName("destination_id")]
    public string DestinationId { get; set; } = string.Empty;

    [JsonPropertyOrder(1), JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyOrder(2), JsonPropertyName("approved_at_utc")]
    public string ApprovedAtUtc { get; set; } = string.Empty;

    [JsonPropertyOrder(3), JsonPropertyName("revoked_at_utc")]
    public string RevokedAtUtc { get; set; } = string.Empty;
}

public sealed class InstallationTrustContext
{
    internal InstallationTrustContext(
        string securityRoot,
        string bundlePath,
        string counterScopePath,
        InstallationTrustBundleV1 bundle,
        Stage2PublicKeyRecord activePolicySigner)
    {
        SecurityRoot = securityRoot;
        BundlePath = bundlePath;
        CounterScopePath = counterScopePath;
        Bundle = bundle;
        ActivePolicySigner = activePolicySigner;
    }

    public string SecurityRoot { get; }
    public string BundlePath { get; }
    public string CounterScopePath { get; }
    public InstallationTrustBundleV1 Bundle { get; }
    public Stage2PublicKeyRecord ActivePolicySigner { get; }

    public string TrustRootFingerprintSha256 => Bundle.TrustRoot.PublicKeyFingerprintSha256;
    public string PolicyCounterInstanceId => Bundle.PolicyCounterInstanceId;
    public long MinimumPolicyGeneration => Math.Max(1, Bundle.Generation);

    public bool IsDestinationApproved(string destinationId) => Bundle.ApprovedDestinations.Any(destination =>
        destination.Status.Equals("active", StringComparison.Ordinal) &&
        destination.DestinationId.Equals(destinationId, StringComparison.Ordinal));
}

public sealed record InstallationTrustResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    InstallationTrustContext? Context = null);

public sealed class InstallationTrustProvisioningOptions
{
    public string? SecurityRoot { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public DateTimeOffset? NowUtc { get; set; }
}
