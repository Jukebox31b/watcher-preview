using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;

namespace DcsWatcherV2.Services;

public sealed record RuntimeCompositionReleaseSelfTestResult(int Passed, int Failed, IReadOnlyList<string> Messages);

public static class RuntimeCompositionReleaseSelfTest
{
    public static RuntimeCompositionReleaseSelfTestResult Run()
    {
        var passed = 0;
        var failed = 0;
        var messages = new List<string>();
        Run("Fresh defaults are neutral and inactive", TestNeutralDefaults);
        Run("Legacy configuration imports disabled without activation", TestLegacyImport);
        Run("Profile authority projects into runtime", TestProfileProjection);
        Run("Profile state roots are isolated", TestProfileIsolation);
        Run("Unsafe profile IDs cannot escape storage", TestPathTraversal);
        Run("Demo adapters remain hard-isolated", TestDemoIsolation);
        Run("Experimental runtime rejects missing trust", TestMissingTrust);
        Run("Verified IPC destination is installation-bound", TestDestinationBinding);
        Run("Report verification honors configured branch", TestConfiguredBranch);
        Run("Automatic profile cannot activate without bounded grant", TestAutomaticActivationBlocked);
        return new RuntimeCompositionReleaseSelfTestResult(passed, failed, messages);

        void Run(string name, Action test)
        {
            try
            {
                test();
                passed++;
                messages.Add("PASS: " + name);
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"FAIL: {name}: {ex.Message}");
            }
        }
    }

    private static void TestNeutralDefaults()
    {
        using var root = new TemporaryDirectory();
        var config = new ConfigService(root.Path).CreatePortableDefaults();
        var identityDefaults = new[]
        {
            config.ReportRepoFullName, config.ReportGitHubBlobBase, config.GitHubBlobBase,
            config.ChatGptDirectorUrl, config.ChatGptTitle, config.CodexThreadId,
            config.CodexDirectorThreadId, config.CodexDirectorTitle, config.ExpectedRepo
        };
        if (identityDefaults.Any(value => !string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException("Fresh config contains a deployment identity.");
        if (config.StartWatcherOnLaunch || config.SubmitChatGptPrompt || config.SubmitCodexPrompt ||
            config.AutoCaptureChatGptEnvelope || config.AutomaticWakeEnabled || config.AutomaticDeliveryEnabled ||
            config.LiveCodexIntakeEnabled || config.Stage4Authorized || !string.IsNullOrWhiteSpace(config.CodexThreadId))
            throw new InvalidOperationException("Fresh config is not manual/detect-only.");
    }

    private static void TestLegacyImport()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var evidence = Path.Combine(root.Path, "legacy.json");
        var legacy = new AppConfig
        {
            ReportPollMode = "LocalFolder",
            LocalReportRoot = Path.Combine(root.Path, "reports"),
            ReportRepoFullName = "example/private",
            ReportBranch = "release",
            ReportFolder = "reports",
            ChatGptDirectorUrl = "https://chatgpt.com/c/example",
            CodexThreadId = "legacy-thread",
            AutomaticWakeEnabled = true,
            AutomaticDeliveryEnabled = true,
            Stage4Authorized = true
        };
        File.WriteAllText(evidence, JsonSerializer.Serialize(legacy));
        var before = SHA256.HashData(File.ReadAllBytes(evidence));
        var imported = configs.EnsureLegacyProfileImported(legacy) ?? throw new InvalidOperationException("Legacy profile was not imported.");
        var after = SHA256.HashData(File.ReadAllBytes(evidence));
        if (!before.AsSpan().SequenceEqual(after) || imported.Enabled ||
            imported.AutomationPolicy.Kind != WatcherAutomationPolicyKind.ManualApproval ||
            imported.AutomationPolicy.PolicyGeneration != 0 || !string.IsNullOrWhiteSpace(configs.CreatePortableDefaults().ActiveProfileId))
            throw new InvalidOperationException("Legacy evidence changed or imported profile became active.");
    }

    private static void TestProfileProjection()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var profile = ManualLocalProfile("profile-a", root.Path, "release/preview");
        var result = RuntimeComposition.TryCreate(configs, configs.CreatePortableDefaults(), profile);
        var runtime = result.Composition?.Config ?? throw new InvalidOperationException(result.ReasonCode + ": " + result.Message);
        if (!runtime.RuntimeComposedFromProfile || runtime.RuntimeProfileId != "profile-a" ||
            runtime.ReportBranch != "release/preview" || runtime.ReportRepoFullName != "example/project" ||
            runtime.ReportPollMode != "LocalFolder" || runtime.Stage4Authorized || runtime.StartWatcherOnLaunch ||
            runtime.CodexDeliveryTransport != "TestSink")
            throw new InvalidOperationException("Runtime projection did not preserve profile authority and safe flags.");
    }

    private static void TestProfileIsolation()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var one = configs.GetProfileRuntimeRoot("one");
        var two = configs.GetProfileRuntimeRoot("two");
        if (one.Equals(two, StringComparison.OrdinalIgnoreCase) ||
            !one.StartsWith(Path.Combine(root.Path, "profiles"), StringComparison.OrdinalIgnoreCase) ||
            !two.StartsWith(Path.Combine(root.Path, "profiles"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile state roots are not isolated under local application data.");
    }

    private static void TestPathTraversal()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        foreach (var id in new[] { "../escape", "..", "a/b", "a\\b", " C:" })
        {
            try { _ = configs.GetProfileRuntimeRoot(id); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Unsafe profile ID was accepted: " + id);
        }
    }

    private static void TestDemoIsolation()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var profile = new ProfileService(configs.GetProfileDirectory(configs.CreatePortableDefaults())).CreateFresh("Demo");
        profile.ReportSource.Adapter.AdapterId = WatcherAdapterIds.ReportDemoFixture;
        profile.Director.Adapter.AdapterId = WatcherAdapterIds.DirectorDemoFixture;
        profile.Destination.Adapter.AdapterId = WatcherAdapterIds.DeliveryTestSink;
        profile.Enabled = false;
        var accepted = RuntimeComposition.TryCreate(configs, configs.CreatePortableDefaults(), profile);
        if (!accepted.Accepted || accepted.Composition?.OfflineOnly != true)
            throw new InvalidOperationException("Complete disabled demo did not compose offline.");
        profile.Enabled = true;
        var rejected = RuntimeComposition.TryCreate(configs, configs.CreatePortableDefaults(), profile);
        if (rejected.Accepted || rejected.ReasonCode != "PROFILE_INVALID")
            throw new InvalidOperationException("Enabled demo profile was not rejected fail-closed.");
    }

    private static void TestMissingTrust()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var profile = ManualLocalProfile("missing-trust", root.Path, "main");
        profile.Director.Adapter.AdapterId = WatcherAdapterIds.DirectorChatGptEdgeCdp;
        profile.Director.ConversationIdentity = "https://chatgpt.com/c/synthetic";
        profile.SecurityBinding = new PublicSecurityBindingV1 { SignerKeyId = "missing", PublicKeyFingerprintSha256 = new string('a', 64), TrustStoreIdentity = "missing" };
        var result = RuntimeComposition.TryCreate(configs, configs.CreatePortableDefaults(), profile);
        if (result.Accepted || result.ReasonCode != "INSTALLATION_TRUST_MISSING")
            throw new InvalidOperationException("Experimental adapter did not reject missing installation trust: " + result.ReasonCode);
    }

    private static void TestDestinationBinding()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var trustService = new InstallationTrustAnchorService();
        InstallationTrustContext? context = null;
        try
        {
            var provisioned = trustService.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = Path.Combine(root.Path, "security"),
                DestinationId = "approved-thread"
            });
            context = provisioned.Context ?? throw new InvalidOperationException(provisioned.ReasonCode);
            var profile = ManualLocalProfile("destination-binding", root.Path, "main");
            profile.Destination.Adapter.AdapterId = WatcherAdapterIds.DeliveryCodexVerifiedIpc;
            profile.Destination.DestinationIdentity = "other-thread";
            profile.SecurityBinding = new PublicSecurityBindingV1
            {
                SignerKeyId = context.ActivePolicySigner.KeyId,
                PublicKeyFingerprintSha256 = context.ActivePolicySigner.PublicKeyFingerprintSha256,
                TrustStoreIdentity = context.Bundle.InstallationId
            };
            var installation = configs.CreatePortableDefaults();
            installation.InstallationSecurityRoot = context.SecurityRoot;
            var result = RuntimeComposition.TryCreate(configs, installation, profile);
            if (result.Accepted || result.ReasonCode != "DESTINATION_NOT_APPROVED")
                throw new InvalidOperationException("Unapproved destination was not rejected: " + result.ReasonCode);
        }
        finally
        {
            if (context is not null) trustService.DeleteForOfflineTests(context);
        }
    }

    private static void TestConfiguredBranch()
    {
        var bytes = Encoding.UTF8.GetBytes("terminal report");
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var delivered = DateTimeOffset.UtcNow.AddMinutes(-5);
        var config = new AppConfig { ReportRepoFullName = "example/project", ReportBranch = "release/preview", ReportFolder = "reports" };
        var state = new AppState
        {
            ActiveTaskLock = new ActiveTaskLockRecord
            {
                IsActive = true,
                ActiveTaskId = "SC100",
                SourceReport = "source.md",
                DeliveryTimestampUtc = delivered
            }
        };
        var candidate = new ReportCandidate("reports/SC100.md", "remote:SC100", "SC100.md", fingerprint,
            DateTime.UtcNow, "https://example.invalid/report", DateTime.UtcNow)
        {
            Repository = "example/project",
            Branch = "release/preview",
            Commit = new string('a', 40),
            BlobIdentity = new string('b', 40),
            ContentBytes = bytes,
            ReportTaskId = "SC100",
            SourceReport = "source.md",
            IsTerminal = true
        };
        var record = new ReportIngestionVerifier().Verify(config, state, candidate, DateTimeOffset.UtcNow);
        if (!record.Eligible) throw new InvalidOperationException(record.RejectionReason);
    }

    private static void TestAutomaticActivationBlocked()
    {
        using var root = new TemporaryDirectory();
        var configs = new ConfigService(root.Path);
        var profile = ManualLocalProfile("automatic", root.Path, "main");
        profile.Director.Adapter.AdapterId = WatcherAdapterIds.DirectorChatGptEdgeCdp;
        profile.Director.ConversationIdentity = "https://chatgpt.com/c/synthetic";
        profile.AutomationPolicy.Kind = WatcherAutomationPolicyKind.PlannedAutomatic;
        profile.AutomationPolicy.RequireVisibleHumanApproval = false;
        profile.Guardrails.MaximumTasksPerRun = 1;
        profile.Guardrails.MaximumElapsedMinutes = 10;
        profile.Guardrails.SummaryIntervalMinutes = 5;
        profile.SecurityBinding = new PublicSecurityBindingV1 { SignerKeyId = "synthetic", PublicKeyFingerprintSha256 = new string('a', 64), TrustStoreIdentity = "synthetic" };
        var result = RuntimeComposition.TryCreate(configs, configs.CreatePortableDefaults(), profile);
        if (result.Accepted || result.ReasonCode != "BOUNDED_GRANT_REQUIRED")
            throw new InvalidOperationException("Automatic profile activated without a bounded grant: " + result.ReasonCode);
    }

    private static WatcherProfileV1 ManualLocalProfile(string id, string root, string branch) => new()
    {
        Enabled = true,
        Identity = new ProfileIdentityV1 { ProfileId = id, Name = id },
        ReportSource = new ReportSourceProfileV1
        {
            Adapter = new AdapterConfigurationV1
            {
                AdapterId = WatcherAdapterIds.ReportLocalFolder,
                Settings = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["local_root"] = Path.Combine(root, "reports"),
                    ["folder"] = "reports"
                }
            },
            ExpectedRepository = "example/project",
            ExpectedBranch = branch
        },
        Director = new DirectorProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DirectorManualEnvelope } },
        Destination = new DestinationProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DeliveryTestSink } },
        AutomationPolicy = new AutomationPolicyProfileV1 { Kind = WatcherAutomationPolicyKind.ManualApproval, RequireVisibleHumanApproval = true },
        Guardrails = new GuardrailsProfileV1 { StopOnFailure = true, StopOnBranchDivergence = true }
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DcsWatcherV2-Runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }
}
