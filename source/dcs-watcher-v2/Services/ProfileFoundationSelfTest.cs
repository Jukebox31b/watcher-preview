using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ProfileFoundationSelfTestResult(int Passed, int Failed, IReadOnlyList<string> Messages);

public static class ProfileFoundationSelfTest
{
    public static ProfileFoundationSelfTestResult Run()
    {
        var messages = new List<string>();
        var passed = 0;
        var failed = 0;
        var root = Path.Combine(Path.GetTempPath(), "DcsWatcherV2-profile-self-test-" + Guid.NewGuid().ToString("N"));

        RunTest("fresh profile is machine and project neutral", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Fresh");
            var json = System.Text.Encoding.UTF8.GetString(new ProfileService(root).SerializeCanonical(profile));
            Assert(string.IsNullOrEmpty(profile.Director.ConversationIdentity) && string.IsNullOrEmpty(profile.Destination.DestinationIdentity), "Fresh profile contains a destination identity default.");
            Assert(!json.Contains(Path.GetPathRoot(root)!, StringComparison.OrdinalIgnoreCase), "Fresh profile contains a machine path.");
            Assert(string.IsNullOrEmpty(profile.ReportSource.ExpectedRepository), "Fresh profile contains a repository default.");
            Assert(!profile.Enabled, "Fresh profile must start disabled.");
        });

        RunTest("canonical profile round trip", () =>
        {
            var service = new ProfileService(root);
            var profile = service.CreateFresh("Round trip");
            service.Save(profile);
            var loaded = service.Load(profile.Identity.ProfileId);
            Assert(service.SerializeCanonical(profile).AsSpan().SequenceEqual(service.SerializeCanonical(loaded)), "Canonical bytes changed across round trip.");
        });

        RunTest("legacy migration is disabled and manual", () =>
        {
            var legacy = new AppConfig
            {
                AutomaticWakeEnabled = true,
                AutomaticDeliveryEnabled = true,
                AutomaticInstructionDeliveryEnabled = true,
                AutoCaptureChatGptEnvelope = true,
                SubmitChatGptPrompt = true,
                SubmitCodexPrompt = true
            };
            var imported = new ProfileService(root).CreateImportedLegacyProfile(legacy);
            Assert(!imported.Enabled, "Legacy import preserved enabled state.");
            Assert(imported.AutomationPolicy.Kind == WatcherAutomationPolicyKind.ManualApproval, "Legacy import preserved automatic policy.");
            Assert(imported.AutomationPolicy.RequireVisibleHumanApproval, "Legacy import removed human approval.");
            Assert(!imported.Director.AllowFallbackBody && !imported.Director.AllowWholePageCapture, "Legacy import enabled unsafe capture.");
        });

        RunTest("unknown adapter is rejected", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Unknown adapter");
            profile.ReportSource.Adapter.AdapterId = "report.untrusted-plugin";
            Assert(new ProfileValidator().Validate(profile).Issues.Any(issue => issue.Code == "UNKNOWN_ADAPTER"), "Unknown adapter was accepted.");
        });

        RunTest("automatic policy requires bounded guardrails", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Automatic");
            profile.AutomationPolicy.Kind = WatcherAutomationPolicyKind.ContinuousAutomatic;
            var result = new ProfileValidator().Validate(profile);
            Assert(result.Issues.Any(issue => issue.Code == "AUTOMATIC_TASK_LIMIT_MISSING"), "Automatic policy omitted task bound.");
            Assert(result.Issues.Any(issue => issue.Code == "AUTOMATIC_TIME_LIMIT_MISSING"), "Automatic policy omitted time bound.");
        });

        RunTest("private secret fields are rejected", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Secret rejection");
            profile.ReportSource.Adapter.Settings["api_token"] = "not-a-real-token";
            Assert(new ProfileValidator().Validate(profile).Issues.Any(issue => issue.Code == "PRIVATE_SECRET_FIELD_FORBIDDEN"), "Private secret field was accepted.");
        });

        RunTest("manual Director adapters cannot use automatic policy", () =>
        {
            foreach (var adapterId in new[] { WatcherAdapterIds.DirectorManualEnvelope, WatcherAdapterIds.DirectorHashBoundFile })
            {
                var profile = CreateBoundedAutomaticProfile(root, "Manual Director rejection");
                profile.Director.Adapter.AdapterId = adapterId;
                Assert(new ProfileValidator().Validate(profile).Issues.Any(issue => issue.Code == "MANUAL_DIRECTOR_CANNOT_AUTOMATE"), $"Automatic policy accepted manual Director adapter {adapterId}.");
            }
        });

        RunTest("visible paste delivery requires visible manual approval", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Visible paste rejection");
            profile.ReportSource.Adapter.AdapterId = WatcherAdapterIds.ReportLocalFolder;
            profile.Destination.Adapter.AdapterId = WatcherAdapterIds.DeliveryManualVisiblePaste;
            profile.Destination.DestinationIdentity = "visible-destination";
            profile.AutomationPolicy.Kind = WatcherAutomationPolicyKind.AuditOnly;
            profile.AutomationPolicy.RequireVisibleHumanApproval = false;
            Assert(new ProfileValidator().Validate(profile).Issues.Any(issue => issue.Code == "VISIBLE_PASTE_REQUIRES_MANUAL_APPROVAL"), "Visible paste delivery was accepted without visible manual approval.");
        });

        RunTest("demo fixtures require disabled test sink", () =>
        {
            var profile = new ProfileService(root).CreateFresh("Demo confinement");
            profile.Enabled = true;
            profile.Destination.Adapter.AdapterId = WatcherAdapterIds.DeliveryCodexVerifiedIpc;
            profile.Destination.DestinationIdentity = "live-thread";
            Assert(new ProfileValidator().Validate(profile).Issues.Any(issue => issue.Code == "DEMO_FIXTURE_REQUIRES_DISABLED_TEST_SINK"), "Demo fixture was allowed to address a live destination.");
        });

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // A failed cleanup does not alter the deterministic test result.
        }

        return new ProfileFoundationSelfTestResult(passed, failed, messages);

        void RunTest(string name, Action test)
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
                messages.Add("FAIL: " + name + ": " + ex.Message);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static WatcherProfileV1 CreateBoundedAutomaticProfile(string root, string name)
    {
        var profile = new ProfileService(root).CreateFresh(name);
        profile.ReportSource.Adapter.AdapterId = WatcherAdapterIds.ReportLocalFolder;
        profile.Destination.Adapter.AdapterId = WatcherAdapterIds.DeliveryTestSink;
        profile.AutomationPolicy.Kind = WatcherAutomationPolicyKind.PlannedAutomatic;
        profile.AutomationPolicy.RequireVisibleHumanApproval = false;
        profile.Guardrails.MaximumTasksPerRun = 1;
        profile.Guardrails.MaximumElapsedMinutes = 10;
        profile.Guardrails.SummaryIntervalMinutes = 5;
        profile.SecurityBinding.SignerKeyId = "test-signer";
        profile.SecurityBinding.PublicKeyFingerprintSha256 = new string('a', 64);
        return profile;
    }
}
