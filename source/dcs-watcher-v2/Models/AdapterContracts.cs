namespace DcsWatcherV2.Models;

public static class WatcherAdapterIds
{
    public const string ReportGitRemote = "report.git-remote";
    public const string ReportGitHub = "report.github";
    public const string ReportLocalFolder = "report.local-folder";
    public const string ReportGitHubLocalFallback = "report.github-local-fallback";
    public const string ReportDemoFixture = "report.demo-fixture";

    public const string DirectorChatGptEdgeCdp = "director.chatgpt-edge-cdp";
    public const string DirectorManualEnvelope = "director.manual-envelope";
    public const string DirectorHashBoundFile = "director.hash-bound-file";
    public const string DirectorDemoFixture = "director.demo-fixture";

    public const string DeliveryCodexVerifiedIpc = "delivery.codex-verified-ipc";
    public const string DeliveryHashBoundFile = "delivery.hash-bound-file";
    public const string DeliveryManualVisiblePaste = "delivery.manual-visible-paste";
    public const string DeliveryTestSink = "delivery.test-sink";
    public const string DeliveryUiPasteFallback = "delivery.ui-paste-fallback";

    public static readonly IReadOnlySet<string> ReportSources = new HashSet<string>(StringComparer.Ordinal)
    {
        ReportGitRemote,
        ReportGitHub,
        ReportLocalFolder,
        ReportGitHubLocalFallback,
        ReportDemoFixture
    };

    public static readonly IReadOnlySet<string> Directors = new HashSet<string>(StringComparer.Ordinal)
    {
        DirectorChatGptEdgeCdp,
        DirectorManualEnvelope,
        DirectorHashBoundFile,
        DirectorDemoFixture
    };

    public static readonly IReadOnlySet<string> Deliveries = new HashSet<string>(StringComparer.Ordinal)
    {
        DeliveryCodexVerifiedIpc,
        DeliveryHashBoundFile,
        DeliveryManualVisiblePaste,
        DeliveryTestSink,
        DeliveryUiPasteFallback
    };
}

public enum WatcherAdapterRole
{
    ReportSource,
    Director,
    Delivery
}

public enum WatcherAdapterMaturity
{
    Preview,
    Experimental,
    DemoOnly
}

public sealed record WatcherAdapterMetadata(
    string AdapterId,
    WatcherAdapterRole Role,
    string DisplayName,
    WatcherAdapterMaturity Maturity,
    string Description)
{
    public bool IsExperimental => Maturity == WatcherAdapterMaturity.Experimental;

    public string StatusLabel => Maturity switch
    {
        WatcherAdapterMaturity.Experimental => "Experimental",
        WatcherAdapterMaturity.DemoOnly => "Demo only",
        _ => "Preview"
    };
}

public sealed record AdapterResolutionResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    WatcherAdapterMetadata? Metadata = null);

public enum WatcherAutomationPolicyKind
{
    ManualApproval,
    PlannedAutomatic,
    SupervisedAutomatic,
    ContinuousAutomatic,
    AuditOnly
}

public static class WatcherAutomationPolicyKindExtensions
{
    public static bool IsAutomatic(this WatcherAutomationPolicyKind policy) => policy is
        WatcherAutomationPolicyKind.PlannedAutomatic or
        WatcherAutomationPolicyKind.SupervisedAutomatic or
        WatcherAutomationPolicyKind.ContinuousAutomatic;
}
