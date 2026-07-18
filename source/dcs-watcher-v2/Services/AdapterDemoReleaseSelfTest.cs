using DcsWatcherV2.Demo;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record AdapterDemoReleaseSelfTestResult(int Passed, int Failed, IReadOnlyList<string> Messages)
{
    public bool Success => Failed == 0;
}

public static class AdapterDemoReleaseSelfTest
{
    public static AdapterDemoReleaseSelfTestResult Run()
    {
        var passed = 0;
        var failed = 0;
        var messages = new List<string>();

        Check("fixed adapter registry rejects unknown IDs", () =>
        {
            var registry = new AdapterRegistry();
            Assert(registry.All.Count == 14, "Fixed registry did not contain every declared adapter.");
            Assert(!registry.Resolve("delivery.untrusted").Accepted, "Unknown adapter ID was accepted.");
            Assert(registry.Resolve("delivery.untrusted").ReasonCode == "UNKNOWN_ADAPTER", "Unknown adapter reason was not stable.");
        });

        Check("experimental adapters carry visible labels", () =>
        {
            var experimental = new AdapterRegistry().All.Where(adapter => adapter.IsExperimental).ToArray();
            Assert(experimental.Length > 0, "Registry has no experimental metadata.");
            Assert(experimental.All(adapter => adapter.StatusLabel == "Experimental"), "Experimental status label was missing.");
        });

        Check("generic notification defaults follow-on off", () =>
        {
            var service = new PromptTemplateService();
            var request = new NotificationTemplateRequest("DEMO-REPORT.md", new string('a', 64), "Fixture complete.");
            var notification = service.BuildNotification(request);
            var followOn = service.BuildNotification(request, requestFollowOnInstruction: true);
            Assert(!notification.FollowOnRequested, "Default notification requested a follow-on instruction.");
            Assert(notification.Prompt.Contains("Notification only", StringComparison.Ordinal), "Default safety language is missing.");
            Assert(!notification.Prompt.Contains("explicitly requested", StringComparison.Ordinal), "Default prompt contains opt-in language.");
            Assert(followOn.FollowOnRequested && followOn.Prompt.Contains("explicitly requested", StringComparison.Ordinal), "Explicit follow-on opt-in was omitted.");
        });

        Check("demo composition rejects every non-demo selection", () =>
        {
            var rejected = false;
            try
            {
                using var unused = new SafeDemoComposition(
                    DemoAdapterSelection.IsolatedDefault with { DeliveryAdapterId = WatcherAdapterIds.DeliveryHashBoundFile },
                    new AdapterRegistry());
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("DEMO_ADAPTER_ISOLATION", StringComparison.Ordinal))
            {
                rejected = true;
            }
            Assert(rejected, "Demo composition accepted a non-demo delivery adapter.");
        });

        Check("fixtures are deterministic and sanitized", () =>
        {
            var first = DemoFixtureCatalog.CurrentPath();
            var second = DemoFixtureCatalog.CurrentPath();
            var sibling = DemoFixtureCatalog.SiblingBranch();
            Assert(first.Wake.TransactionId == second.Wake.TransactionId && first.ReportSha256 == second.ReportSha256, "Current-path fixture changed between reads.");
            Assert(first.Response.OnCurrentPath is true, "Current-path fixture is not on the current path.");
            Assert(sibling.Response.OnCurrentPath is false, "Sibling fixture is not marked off path.");
            var fixtureText = string.Join("|", first.ReportName, first.ReportSummary, first.Response.Content, sibling.Response.Content);
            Assert(fixtureText.Contains("repo: example/sanitized-repository", StringComparison.Ordinal), "Fixture repository is not synthetic.");
            Assert(!fixtureText.Contains(":\\", StringComparison.Ordinal), "Fixture contains a machine path.");
        });

        Check("valid fixture is accepted exactly once", () =>
        {
            using var demo = new SafeDemoComposition();
            var accepted = demo.RunCurrentPath();
            Assert(accepted.Accepted && accepted.Disposition == DemoDispositions.AcceptedOnce, "Valid current-path fixture was not accepted.");
            Assert(demo.Sink.AcceptedCount == 1 && demo.Sink.ReceiveCount == 1, "Test sink did not accept exactly once.");
            Assert(demo.SigningCount == 1 && demo.DeliveryAttemptCount == 1, "Valid fixture did not sign and deliver exactly once.");
            Assert(demo.Sink.Receipts.Single().NonActionable, "Test sink receipt was actionable.");
            Assert(accepted.Evidence.EnvelopeSha256.Length == 64 && accepted.Evidence.ProvenanceSha256.Length == 64, "Accepted evidence omitted integrity hashes.");
            Assert(accepted.Evidence.ConversationId == "demo-conversation-001", "Accepted evidence did not preserve the fixture conversation ID.");
            Assert(accepted.Evidence.TaskId == "DEMO-TASK-CURRENT-001", "Accepted evidence did not preserve the extracted task ID.");
        });

        Check("identical replay is rejected without redelivery", () =>
        {
            using var demo = new SafeDemoComposition();
            var accepted = demo.RunCurrentPath();
            var replay = demo.ReplayCurrentPath();
            Assert(accepted.Accepted && !replay.Accepted && replay.Disposition == DemoDispositions.RejectedReplay, "Identical replay was not rejected.");
            Assert(demo.Sink.AcceptedCount == 1 && demo.Sink.ReceiveCount == 1, "Replay reached the test sink.");
            Assert(demo.SigningCount == 1 && demo.DeliveryAttemptCount == 1, "Replay was signed or delivered.");
            Assert(replay.Evidence.FirstSeenUtc == DemoFixtureCatalog.FixtureTimeUtc, "Replay evidence omitted first-seen time.");
        });

        Check("sibling branch is rejected before signing and delivery", () =>
        {
            using var demo = new SafeDemoComposition();
            var rejected = demo.RunSiblingBranch();
            Assert(!rejected.Accepted && rejected.Disposition == DemoDispositions.RejectedBranchDivergence, "Sibling branch was not rejected.");
            Assert(demo.SigningCount == 0 && demo.DeliveryAttemptCount == 0 && demo.Sink.ReceiveCount == 0, "Sibling branch reached signing or delivery.");
            Assert(!rejected.Evidence.SignatureCreated && !rejected.Evidence.DeliveryAttempted, "Sibling evidence claimed a downstream effect.");
            Assert(string.IsNullOrEmpty(rejected.Evidence.EnvelopeSha256), "Sibling envelope was extracted before branch rejection.");
            Assert(string.IsNullOrEmpty(rejected.Evidence.TaskId), "Sibling task ID was projected before envelope extraction.");
        });

        Check("terminal outcomes produce activity and evidence", () =>
        {
            using var demo = new SafeDemoComposition();
            _ = demo.RunCurrentPath();
            _ = demo.ReplayCurrentPath();
            _ = demo.RunSiblingBranch();
            Assert(demo.Evidence.Count == 3, "Expected one evidence record per attempted fixture.");
            Assert(demo.Activity.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, demo.Activity.Count)), "Activity sequence is not deterministic.");
            Assert(demo.Evidence.Select(item => item.Disposition).SequenceEqual(new[]
            {
                DemoDispositions.AcceptedOnce,
                DemoDispositions.RejectedReplay,
                DemoDispositions.RejectedBranchDivergence
            }), "Terminal evidence order is incorrect.");
        });

        return new AdapterDemoReleaseSelfTestResult(passed, failed, messages);

        void Check(string name, Action test)
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
}
