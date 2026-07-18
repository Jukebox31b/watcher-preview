using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record HumanConfirmationUiSelfTestResult(int Passed, int Failed, IReadOnlyList<string> Messages);

public static class HumanConfirmationUiSelfTest
{
    public static HumanConfirmationUiSelfTestResult Run()
    {
        var results = new List<(string Name, bool Passed)>();
        var now = DateTimeOffset.UtcNow;
        var profile = CreateProfile();
        var report = CreateReport(now);
        const string prompt = "Synthetic full prompt.\r\nSecond exact line.";
        const string wakeToken = "DCS_WATCHER_V2_WAKE:offline:test";
        var prepared = PreparedHumanAction.Create(
            WatcherOrchestrator.WakeNewestReportAction,
            report,
            prompt,
            wakeToken,
            profile,
            now,
            TimeSpan.FromMinutes(5));
        var confirmation = prepared.CreateConfirmationRecord();

        Check("Prepared action freezes exact report bytes and prompt",
            prepared.HasValidIntegrity() &&
            prepared.Report.ContentBytes.SequenceEqual(report.ContentBytes) &&
            !ReferenceEquals(prepared.Report.ContentBytes, report.ContentBytes) &&
            prepared.Prompt == prompt &&
            prepared.PromptSha256 == HumanConfirmationRecord.ComputePromptSha256(prompt) &&
            confirmation.Nonce == prepared.Nonce &&
            confirmation.WakeToken == wakeToken);

        using (var form = new HumanConfirmationForm(prepared))
        {
            Check("Explicit confirmation starts unchecked and Confirm disabled",
                !form.ExplicitApprovalChecked && !form.ConfirmEnabled);
            form.SetExplicitApprovalForSelfTest(true);
            Check("Explicit confirmation checkbox enables Confirm",
                form.ExplicitApprovalChecked && form.ConfirmEnabled);
        }

        Check("Exact prepared action validates",
            Validate(prepared, confirmation, profile, now, reserve: false, out _));
        Check("Prepared prompt mutation is rejected",
            !Validate(prepared with { Prompt = prompt + "changed" }, confirmation, profile, now, reserve: false, out var promptReason) &&
            promptReason.Contains("MUTATED", StringComparison.Ordinal));
        Check("Prepared wake-token mutation is rejected",
            !Validate(prepared with { WakeToken = wakeToken + "-changed" }, confirmation, profile, now, reserve: false, out _));
        Check("Confirmation destination mutation is rejected",
            !Validate(prepared, confirmation with { DestinationIdentity = "other-destination" }, profile, now, reserve: false, out _));

        var switchedProfile = CreateProfile();
        switchedProfile.Identity.ProfileId = "other-profile";
        Check("Profile switch is rejected",
            !Validate(prepared, confirmation, switchedProfile, now, reserve: false, out _));
        var changedDestination = CreateProfile();
        changedDestination.Destination.DestinationIdentity = "other-destination";
        Check("Destination change is rejected",
            !Validate(prepared, confirmation, changedDestination, now, reserve: false, out _));
        var changedDirector = CreateProfile();
        changedDirector.Director.ConversationIdentity = "other-director";
        Check("Director destination change is rejected",
            !Validate(prepared, confirmation, changedDirector, now, reserve: false, out _));
        var changedPolicy = CreateProfile();
        changedPolicy.AutomationPolicy.PolicyGeneration++;
        Check("Policy change is rejected",
            !Validate(prepared, confirmation, changedPolicy, now, reserve: false, out _));
        Check("Expired prepared action is rejected",
            !Validate(prepared, confirmation, profile, prepared.ExpiresAtUtc, reserve: false, out var expiryReason) &&
            expiryReason.Contains("STALE", StringComparison.Ordinal));

        var replayNonces = new HashSet<string>(StringComparer.Ordinal);
        var replayGate = new object();
        var firstUse = WatcherOrchestrator.ValidatePreparedHumanAction(
            prepared, confirmation, profile, now, replayNonces, replayGate, true, out _);
        var replay = WatcherOrchestrator.ValidatePreparedHumanAction(
            prepared, confirmation, profile, now, replayNonces, replayGate, true, out var replayReason);
        Check("Confirmation replay is rejected",
            firstUse && !replay && replayReason.Contains("REPLAYED", StringComparison.Ordinal));

        var coordinator = new PreparedHumanActionCoordinator();
        var firstPrepared = coordinator.TryPrepare(prepared.Nonce, out _);
        var concurrentPrepared = coordinator.TryPrepare(new string('a', 64), out var concurrentReason);
        var firstExecution = coordinator.TryBegin(prepared.Nonce, out _);
        var concurrentExecution = coordinator.TryBegin(prepared.Nonce, out _);
        coordinator.Complete(prepared.Nonce);
        Check("Concurrent second preparation and execution are rejected",
            firstPrepared && !concurrentPrepared &&
            concurrentReason.Contains("CONCURRENT", StringComparison.Ordinal) &&
            firstExecution && !concurrentExecution);

        var cancellation = new PreparedHumanActionCoordinator();
        _ = cancellation.TryPrepare(prepared.Nonce, out _);
        var cancelled = cancellation.Cancel(prepared.Nonce);
        Check("Cancellation releases the action without permitting execution",
            cancelled && !cancellation.TryBegin(prepared.Nonce, out _));

        var messages = results
            .Select(result => $"Human confirmation UI self-test: {(result.Passed ? "PASS" : "FAIL")} - {result.Name}")
            .ToArray();
        return new HumanConfirmationUiSelfTestResult(
            results.Count(result => result.Passed),
            results.Count(result => !result.Passed),
            messages);

        void Check(string name, bool passed) => results.Add((name, passed));
    }

    private static bool Validate(
        PreparedHumanAction prepared,
        HumanConfirmationRecord confirmation,
        WatcherProfileV1 profile,
        DateTimeOffset now,
        bool reserve,
        out string reason) => WatcherOrchestrator.ValidatePreparedHumanAction(
            prepared,
            confirmation,
            profile,
            now,
            new HashSet<string>(StringComparer.Ordinal),
            new object(),
            reserve,
            out reason);

    private static WatcherProfileV1 CreateProfile() => new()
    {
        Enabled = true,
        Identity = new ProfileIdentityV1 { ProfileId = "human-confirmation-test", Name = "Human confirmation test" },
        ReportSource = new ReportSourceProfileV1
        {
            Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.ReportDemoFixture },
            ExpectedRepository = "offline/example",
            ExpectedBranch = "test"
        },
        Director = new DirectorProfileV1
        {
            Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DirectorDemoFixture },
            ConversationIdentity = "offline-director"
        },
        Destination = new DestinationProfileV1
        {
            Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DeliveryTestSink },
            DestinationIdentity = "offline-test-sink"
        },
        AutomationPolicy = new AutomationPolicyProfileV1
        {
            Kind = WatcherAutomationPolicyKind.ManualApproval,
            PolicyGeneration = 7,
            RequireVisibleHumanApproval = true
        }
    };

    private static ReportCandidate CreateReport(DateTimeOffset now)
    {
        var bytes = Encoding.UTF8.GetBytes("Result: PASS\nOffline report body.\n");
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ReportCandidate(
            "reports/CGPT-REPORT-OFFLINE.md",
            "fixture:CGPT-REPORT-OFFLINE.md",
            "CGPT-REPORT-OFFLINE.md",
            fingerprint,
            now.UtcDateTime,
            "https://example.invalid/report",
            now.UtcDateTime)
        {
            Repository = "offline/example",
            Branch = "test",
            Commit = new string('b', 40),
            BlobIdentity = new string('c', 40),
            ContentBytes = bytes
        };
    }
}
