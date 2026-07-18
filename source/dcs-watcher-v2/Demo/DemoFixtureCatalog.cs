using System.Text;
using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Demo;

public static class DemoFixtureCatalog
{
    public static readonly DateTimeOffset FixtureTimeUtc = new(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);

    public static DemoFixture CurrentPath() => Create(onCurrentPath: true);

    public static DemoFixture SiblingBranch() => Create(onCurrentPath: false);

    private static DemoFixture Create(bool onCurrentPath)
    {
        const string reportName = "DEMO-REPORT-0001.md";
        const string reportSummary = "Sanitized fixture completed without external effects.";
        const string conversationId = "demo-conversation-001";
        const string rootId = "demo-root-001";
        const string parentId = "demo-parent-001";
        var suffix = onCurrentPath ? "current" : "sibling";
        var wakeId = $"demo-wake-{suffix}-001";
        var responseId = $"demo-assistant-{suffix}-001";
        var visibleResponseId = onCurrentPath ? responseId : "demo-assistant-visible-001";
        var reportBytes = Encoding.UTF8.GetBytes("fixture=demo\nresult=pass\neffects=none\n");
        var envelope = BuildEnvelope(reportName, suffix);

        var wake = new WakeTransactionRecord
        {
            TransactionId = $"demo-transaction-{suffix}-001",
            Nonce = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes($"demo-nonce-{suffix}-001")),
            ConversationId = conversationId,
            CurrentNodeBeforeWake = parentId,
            VisibleBranchAncestry = [rootId, parentId],
            VisibleParentMessageId = parentId,
            BrowserTabIdentity = "demo-tab-001",
            WakeToken = $"demo-wake-token-{suffix}-001",
            IntendedSourceReport = reportName,
            IntendedActiveTask = "demo-active-task-001",
            WakeMessageId = wakeId,
            WakeParentMessageId = parentId,
            WakeCreatedAtUtc = FixtureTimeUtc.AddSeconds(-2),
            Status = "synthetic-fixture",
            HumanConfirmed = true
        };

        var response = new AssistantResponseObservation
        {
            MessageId = responseId,
            ParentMessageId = wakeId,
            Role = "assistant",
            Content = envelope,
            Complete = true,
            OnCurrentPath = onCurrentPath,
            WakeToken = wake.WakeToken,
            SourceReport = reportName,
            CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
            FallbackBody = false,
            ApiVerified = true,
            SelectedAssistantIndex = 0,
            AssistantSelectionAmbiguous = false,
            WholePageCaptureUsed = false,
            CurrentNodeAtCapture = visibleResponseId,
            CreatedAtUtc = FixtureTimeUtc.AddSeconds(-1)
        };

        var nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
        {
            [rootId] = Node(rootId, string.Empty, "system", [parentId]),
            [parentId] = Node(parentId, rootId, "user", [wakeId]),
            [wakeId] = Node(wakeId, parentId, "user", [responseId]),
            [responseId] = Node(responseId, wakeId, "assistant", [])
        };
        if (!onCurrentPath)
        {
            nodes[wakeId].ChildMessageIds = [responseId, visibleResponseId];
            nodes[visibleResponseId] = Node(visibleResponseId, wakeId, "assistant", []);
        }

        var snapshot = new ConversationLineageSnapshot
        {
            ConversationId = conversationId,
            CurrentNode = visibleResponseId,
            BrowserTabIdentity = wake.BrowserTabIdentity,
            SnapshotTimestampUtc = FixtureTimeUtc,
            ApiVerified = true,
            ApiStatusCode = 200,
            BrowserBackendAgree = true,
            BrowserVisibleMessageIds = [wakeId, visibleResponseId],
            CurrentPathMessageIds = [rootId, parentId, wakeId, visibleResponseId],
            VisibleActiveBranchMessageIds = [rootId, parentId, wakeId, visibleResponseId],
            Nodes = nodes
        };

        return new DemoFixture(
            $"{suffix}-path-fixture",
            FixtureTimeUtc,
            reportName,
            Stage2Crypto.Sha256Hex(reportBytes),
            reportSummary,
            wake,
            snapshot,
            response);
    }

    private static string BuildEnvelope(string reportName, string suffix) => string.Join("\n", new[]
    {
        ChatGptEnvelopeCapture.OpenMarker,
        $"task_id: DEMO-TASK-{suffix.ToUpperInvariant()}-001",
        "origin: demo-fixture",
        "repo: example/sanitized-repository",
        "target: test-sink",
        "mode: demo",
        "created_at: 2026-07-18T16:00:00Z",
        $"source_report: {reportName}",
        string.Empty,
        "BEGIN_INSTRUCTION",
        "Record this synthetic fixture as non-actionable demo evidence.",
        "END_INSTRUCTION",
        ChatGptEnvelopeCapture.CloseMarker
    });

    private static ConversationNodeRecord Node(
        string id,
        string parentId,
        string role,
        List<string> children) => new()
    {
        MessageId = id,
        ParentMessageId = parentId,
        Role = role,
        ContentType = "text",
        Content = string.Empty,
        Complete = true,
        ChildMessageIds = children
    };
}
