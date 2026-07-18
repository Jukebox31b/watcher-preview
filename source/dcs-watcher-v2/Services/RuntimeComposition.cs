using System.Globalization;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;

namespace DcsWatcherV2.Services;

public sealed record RuntimeCompositionResult(
    bool Accepted,
    string ReasonCode,
    string Message,
    RuntimeComposition? Composition = null);

public sealed class RuntimeComposition
{
    private RuntimeComposition(
        WatcherProfileV1 profile,
        AppConfig config,
        string profileRoot,
        bool offlineOnly,
        InstallationTrustContext? trustContext)
    {
        Profile = profile;
        Config = config;
        ProfileRoot = profileRoot;
        OfflineOnly = offlineOnly;
        TrustContext = trustContext;
    }

    public WatcherProfileV1 Profile { get; }
    public AppConfig Config { get; }
    public string ProfileRoot { get; }
    public bool OfflineOnly { get; }
    public InstallationTrustContext? TrustContext { get; }

    public static RuntimeCompositionResult TryCreate(
        ConfigService configService,
        AppConfig installationConfig,
        WatcherProfileV1 profile,
        ProfileValidator? validator = null,
        AdapterRegistry? adapters = null,
        ProfileProvisioningService? provisioning = null)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(installationConfig);
        ArgumentNullException.ThrowIfNull(profile);
        validator ??= new ProfileValidator();
        adapters ??= new AdapterRegistry();
        provisioning ??= new ProfileProvisioningService();

        try
        {
            ConfigService.ValidateProfileId(profile.Identity.ProfileId);
        }
        catch (ArgumentException ex)
        {
            return Reject("PROFILE_ID_UNSAFE", ex.Message);
        }

        var validation = validator.Validate(profile);
        if (!validation.IsValid)
        {
            return Reject("PROFILE_INVALID", string.Join("; ", validation.Issues.Select(issue => issue.Code)));
        }

        var report = adapters.Resolve(profile.ReportSource.Adapter.AdapterId);
        var director = adapters.Resolve(profile.Director.Adapter.AdapterId);
        var destination = adapters.Resolve(profile.Destination.Adapter.AdapterId);
        var rejected = new[] { report, director, destination }.FirstOrDefault(result => !result.Accepted);
        if (rejected is not null) return Reject(rejected.ReasonCode, rejected.Message);

        var usesDemoFixture = report.Metadata!.Maturity == WatcherAdapterMaturity.DemoOnly ||
                              director.Metadata!.Maturity == WatcherAdapterMaturity.DemoOnly;
        var offlineOnly = usesDemoFixture || destination.Metadata!.Maturity == WatcherAdapterMaturity.DemoOnly;
        var completeDemo = profile.ReportSource.Adapter.AdapterId == WatcherAdapterIds.ReportDemoFixture &&
                           profile.Director.Adapter.AdapterId == WatcherAdapterIds.DirectorDemoFixture &&
                           profile.Destination.Adapter.AdapterId == WatcherAdapterIds.DeliveryTestSink;
        if (usesDemoFixture && (!completeDemo || profile.Enabled))
        {
            return Reject("DEMO_ISOLATION_REQUIRED", "Demo adapters compose only as one disabled fixture-to-test-sink pipeline.");
        }
        if (!usesDemoFixture && !profile.Enabled)
        {
            return Reject("PROFILE_DISABLED", "A live-capable runtime cannot be composed from a disabled profile.");
        }
        if (profile.AutomationPolicy.Kind.IsAutomatic())
        {
            return Reject("BOUNDED_GRANT_REQUIRED", "Automatic policies remain unavailable until an explicit signed bounded grant is provisioned and validated.");
        }

        InstallationTrustContext? trustContext = null;
        var hasExperimentalAdapter = new[] { report.Metadata, director.Metadata, destination.Metadata }
            .Any(metadata => metadata!.IsExperimental);
        if (hasExperimentalAdapter)
        {
            var trust = provisioning.LoadInstallation(installationConfig.InstallationSecurityRoot);
            if (!trust.Accepted || trust.Context is null)
            {
                return Reject(trust.ReasonCode, "Experimental adapters require validated local installation trust. " + trust.Message);
            }
            trustContext = trust.Context;
            if (!profile.SecurityBinding.TrustStoreIdentity.Equals(trustContext.Bundle.InstallationId, StringComparison.Ordinal) ||
                !profile.SecurityBinding.SignerKeyId.Equals(trustContext.ActivePolicySigner.KeyId, StringComparison.Ordinal) ||
                !profile.SecurityBinding.PublicKeyFingerprintSha256.Equals(trustContext.ActivePolicySigner.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Reject("EXPERIMENTAL_ADAPTER_NOT_LOCALLY_VALIDATED", "The profile is not bound to the active installation trust authority.");
            }
        }
        if (profile.Destination.Adapter.AdapterId == WatcherAdapterIds.DeliveryCodexVerifiedIpc &&
            (trustContext is null || !trustContext.IsDestinationApproved(profile.Destination.DestinationIdentity)))
        {
            return Reject("DESTINATION_NOT_APPROVED", "Verified IPC destination is not approved by installation trust.");
        }

        var runtimeRoot = configService.GetProfileRuntimeRoot(profile.Identity.ProfileId);
        var runtime = configService.CreatePortableDefaults();
        runtime.ActiveProfileId = profile.Identity.ProfileId;
        runtime.RuntimeProfileId = profile.Identity.ProfileId;
        runtime.RuntimeProfileRoot = runtimeRoot;
        runtime.RuntimeComposedFromProfile = true;
        runtime.ProfileConfigurationSha256 = new ProfileService(configService.GetProfileDirectory(runtime)).ComputeSha256(profile);
        runtime.LedgerRoot = runtimeRoot;
        runtime.ApprovedInstructionDirectory = Path.Combine(runtimeRoot, "approved-instructions");
        runtime.Stage3OutboundReplayLedgerDirectory = Path.Combine(runtimeRoot, "replay", "outbound");
        runtime.Stage3IntakeReplayLedgerDirectory = Path.Combine(runtimeRoot, "replay", "intake");
        runtime.OperatingStage = nameof(WatcherOperatingStage.Stage1DetectOnly);
        runtime.ExpectedRepo = profile.ReportSource.ExpectedRepository;
        runtime.ReportRepoFullName = profile.ReportSource.ExpectedRepository;
        runtime.ReportBranch = profile.ReportSource.ExpectedBranch;
        runtime.AllowedBranch = profile.ReportSource.ExpectedBranch;
        runtime.BranchLockEnabled = false;
        runtime.MaxEnvelopeChars = profile.Protocol.MaximumEnvelopeBytes;
        runtime.CodexThreadId = profile.Destination.DestinationIdentity;
        runtime.CodexDirectorThreadId = profile.Destination.DestinationIdentity;
        runtime.ChatGptDirectorUrl = profile.Director.ConversationIdentity;
        runtime.ChatGptCaptureScope = "BackendMessageObject";
        runtime.ChatGptOpenIfMissing = false;
        runtime.RequireSingleTaskEnvelope = true;
        runtime.CaptureNewestEnvelopeOnly = true;
        runtime.AllowRepoMismatch = false;
        runtime.StartWatcherOnLaunch = false;
        runtime.AutomaticWakeEnabled = false;
        runtime.AutomaticDeliveryEnabled = false;
        runtime.AutomaticInstructionDeliveryEnabled = false;
        runtime.LiveManualPilotAuthorized = false;
        runtime.LiveCodexIntakeEnabled = false;
        runtime.Stage4Authorized = false;
        runtime.Stage5Authorized = false;
        runtime.SubmitChatGptPrompt = false;
        runtime.AutoCaptureChatGptEnvelope = false;
        runtime.SubmitCodexPrompt = false;
        runtime.AutoSendCapturedTaskToCodex = false;
        runtime.UiBridgeToCodex = false;
        runtime.CodexUiPasteFallbackEnabled = false;
        runtime.CodexUseClipboardFallback = false;

        ProjectReportSource(runtime, profile.ReportSource);
        ProjectDirector(runtime, profile.Director);
        ProjectDestination(runtime, profile.Destination);
        return new RuntimeCompositionResult(true, "OK", offlineOnly
            ? "Offline demo runtime composed; no live adapter may be constructed."
            : "Validated profile runtime composed in detect-only/manual-safe mode.",
            new RuntimeComposition(profile, runtime, runtimeRoot, offlineOnly, trustContext));
    }

    private static void ProjectReportSource(AppConfig config, ReportSourceProfileV1 source)
    {
        config.ReportPollMode = source.Adapter.AdapterId switch
        {
            WatcherAdapterIds.ReportGitRemote => "GitRemote",
            WatcherAdapterIds.ReportGitHub => "GitHub",
            WatcherAdapterIds.ReportLocalFolder => "LocalFolder",
            WatcherAdapterIds.ReportGitHubLocalFallback => "GitHubThenLocalFallback",
            _ => "DemoFixture"
        };
        config.ReportGitRoot = Setting(source.Adapter, "git_root");
        config.LocalRepoPath = config.ReportGitRoot;
        config.WorkspaceRoot = config.ReportGitRoot;
        config.ReportRemote = Setting(source.Adapter, "remote", "origin");
        config.ReportFolder = Setting(source.Adapter, "folder");
        config.GitHubReportsFolder = config.ReportFolder;
        config.LocalReportRoot = Setting(source.Adapter, "local_root");
        config.ReportGitHubBlobBase = Setting(source.Adapter, "blob_base");
        config.GitHubBlobBase = config.ReportGitHubBlobBase;
        config.BranchLockEnabled = !string.IsNullOrWhiteSpace(config.ReportGitRoot) && !string.IsNullOrWhiteSpace(config.ReportBranch);
    }

    private static void ProjectDirector(AppConfig config, DirectorProfileV1 director)
    {
        config.ChatGptMode = director.Adapter.AdapterId switch
        {
            WatcherAdapterIds.DirectorChatGptEdgeCdp => "Edge CDP",
            WatcherAdapterIds.DirectorManualEnvelope => "ManualEnvelope",
            WatcherAdapterIds.DirectorHashBoundFile => "HashBoundFile",
            _ => "DemoFixture"
        };
        config.ChatGptDirectorUrl = FirstNonBlank(director.ConversationIdentity, Setting(director.Adapter, "conversation_url"));
        config.ChatGptCdpHost = Setting(director.Adapter, "cdp_host", "127.0.0.1");
        if (int.TryParse(Setting(director.Adapter, "cdp_port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
            config.ChatGptCdpPort = port;
    }

    private static void ProjectDestination(AppConfig config, DestinationProfileV1 destination)
    {
        switch (destination.Adapter.AdapterId)
        {
            case WatcherAdapterIds.DeliveryCodexVerifiedIpc:
                config.CodexDeliveryMode = "VerbatimIpc";
                config.CodexDeliveryTransport = "IpcOnly";
                config.CodexIpcPipeName = Setting(destination.Adapter, "pipe_name", CodexIpcClient.DefaultPipeName);
                break;
            case WatcherAdapterIds.DeliveryHashBoundFile:
                config.CodexDeliveryMode = "FileHandoff";
                config.CodexDeliveryTransport = "ManualFile";
                break;
            case WatcherAdapterIds.DeliveryManualVisiblePaste:
                config.CodexDeliveryMode = "FullPaste";
                config.CodexDeliveryTransport = "ManualVisiblePaste";
                break;
            case WatcherAdapterIds.DeliveryUiPasteFallback:
                config.CodexDeliveryMode = "FullPaste";
                config.CodexDeliveryTransport = "UiFallbackBlockedByDefault";
                break;
            default:
                config.CodexDeliveryMode = "TestSink";
                config.CodexDeliveryTransport = "TestSink";
                break;
        }
    }

    private static string Setting(AdapterConfigurationV1 adapter, string name, string fallback = "") =>
        adapter.Settings.TryGetValue(name, out var value) ? value.Trim() : fallback;

    private static string FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static RuntimeCompositionResult Reject(string code, string message) => new(false, code, message);
}
