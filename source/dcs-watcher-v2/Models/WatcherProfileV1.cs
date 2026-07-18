using System.Text.Json.Serialization;

namespace DcsWatcherV2.Models;

public sealed class WatcherProfileV1
{
    public const string SchemaName = "DCS_WATCHER_PROFILE_V1";

    [JsonPropertyOrder(0)]
    public string Schema { get; set; } = SchemaName;

    [JsonPropertyOrder(1)]
    public int Version { get; set; } = 1;

    [JsonPropertyOrder(2)]
    public bool Enabled { get; set; }

    [JsonPropertyOrder(3)]
    public ProfileIdentityV1 Identity { get; set; } = new();

    [JsonPropertyOrder(4)]
    public ReportSourceProfileV1 ReportSource { get; set; } = new();

    [JsonPropertyOrder(5)]
    public DirectorProfileV1 Director { get; set; } = new();

    [JsonPropertyOrder(6)]
    public ProtocolProfileV1 Protocol { get; set; } = new();

    [JsonPropertyOrder(7)]
    public DestinationProfileV1 Destination { get; set; } = new();

    [JsonPropertyOrder(8)]
    public AutomationPolicyProfileV1 AutomationPolicy { get; set; } = new();

    [JsonPropertyOrder(9)]
    public GuardrailsProfileV1 Guardrails { get; set; } = new();

    [JsonPropertyOrder(10)]
    public PublicSecurityBindingV1 SecurityBinding { get; set; } = new();
}

public sealed class ProfileIdentityV1
{
    [JsonPropertyOrder(0)]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Description { get; set; } = string.Empty;
}

public sealed class AdapterConfigurationV1
{
    [JsonPropertyOrder(0)]
    public string AdapterId { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public SortedDictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ReportSourceProfileV1
{
    [JsonPropertyOrder(0)]
    public AdapterConfigurationV1 Adapter { get; set; } = new();

    [JsonPropertyOrder(1)]
    public string ExpectedRepository { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string ExpectedBranch { get; set; } = string.Empty;
}

public sealed class DirectorProfileV1
{
    [JsonPropertyOrder(0)]
    public AdapterConfigurationV1 Adapter { get; set; } = new();

    [JsonPropertyOrder(1)]
    public string ConversationIdentity { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public bool RequireDirectParent { get; set; } = true;

    [JsonPropertyOrder(3)]
    public bool RequireCurrentPath { get; set; } = true;

    [JsonPropertyOrder(4)]
    public bool RequireBackendMessageObject { get; set; } = true;

    [JsonPropertyOrder(5)]
    public bool AllowFallbackBody { get; set; }

    [JsonPropertyOrder(6)]
    public bool AllowWholePageCapture { get; set; }
}

public sealed class ProtocolProfileV1
{
    [JsonPropertyOrder(0)]
    public string ReportFilePattern { get; set; } = "CGPT-REPORT-*.md";

    [JsonPropertyOrder(1)]
    public string TaskIdPattern { get; set; } = "^[A-Za-z][A-Za-z0-9._-]{0,127}$";

    [JsonPropertyOrder(2)]
    public string EnvelopeSchema { get; set; } = "DCS_CODEX_TASK_V1";

    [JsonPropertyOrder(3)]
    public int MaximumEnvelopeBytes { get; set; } = 500_000;
}

public sealed class DestinationProfileV1
{
    [JsonPropertyOrder(0)]
    public AdapterConfigurationV1 Adapter { get; set; } = new();

    [JsonPropertyOrder(1)]
    public string DestinationIdentity { get; set; } = string.Empty;
}

public sealed class AutomationPolicyProfileV1
{
    [JsonPropertyOrder(0)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WatcherAutomationPolicyKind Kind { get; set; } = WatcherAutomationPolicyKind.ManualApproval;

    [JsonPropertyOrder(1)]
    public int PolicyGeneration { get; set; }

    [JsonPropertyOrder(2)]
    public bool RequireVisibleHumanApproval { get; set; } = true;
}

public sealed class GuardrailsProfileV1
{
    [JsonPropertyOrder(0)]
    public int MaximumTasksPerRun { get; set; }

    [JsonPropertyOrder(1)]
    public int MaximumElapsedMinutes { get; set; }

    [JsonPropertyOrder(2)]
    public int SummaryIntervalMinutes { get; set; }

    [JsonPropertyOrder(3)]
    public bool StopOnFailure { get; set; } = true;

    [JsonPropertyOrder(4)]
    public bool StopOnBranchDivergence { get; set; } = true;

    [JsonPropertyOrder(5)]
    public bool PauseAfterCurrentTask { get; set; }

    [JsonPropertyOrder(6)]
    public List<string> PermittedEffects { get; set; } = [];
}

public sealed class PublicSecurityBindingV1
{
    [JsonPropertyOrder(0)]
    public string SignerKeyId { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string PublicKeyFingerprintSha256 { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string TrustStoreIdentity { get; set; } = string.Empty;
}
