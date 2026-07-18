using System.Diagnostics;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ActiveConversationObservation(
    string ConversationId,
    string VisibilityState,
    string DocumentReadyState,
    int VisibleMessageCount);

public sealed record PreWakeSnapshotGateResult(
    bool Success,
    string ReasonCode,
    string Message,
    long DurationMilliseconds = 0);

public static class ChatGptPreWakeSnapshotService
{
    public static readonly TimeSpan MaximumSnapshotWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(30);

    public static async Task<PreWakeSnapshotGateResult> WaitForActiveConversationAsync(
        Func<CancellationToken, Task<ActiveConversationObservation>> observe,
        string expectedConversationId,
        TimeSpan maximumWait,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var last = new PreWakeSnapshotGateResult(
            false,
            "ACTIVE_CONVERSATION_READINESS_PENDING",
            "The active ChatGPT conversation readiness probe has not completed.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(maximumWait);

        try
        {
            while (true)
            {
                ActiveConversationObservation observation;
                try
                {
                    observation = await observe(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new PreWakeSnapshotGateResult(
                        false,
                        "ACTIVE_CONVERSATION_PROBE_TIMEOUT",
                        $"The active ChatGPT conversation readiness probe returned no state within {maximumWait.TotalSeconds:N0} seconds.",
                        stopwatch.ElapsedMilliseconds);
                }

                last = EvaluateObservation(observation, expectedConversationId) with
                {
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds
                };
                if (last.Success)
                    return last;

                await Task.Delay(pollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return last with
            {
                Message = $"Timed out after {maximumWait.TotalSeconds:N0} seconds; unresolved condition {last.ReasonCode}: {last.Message}",
                DurationMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
    }

    public static PreWakeSnapshotGateResult ValidateSnapshot(
        ConversationLineageSnapshot snapshot,
        string expectedConversationId,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ConversationId))
            return Fail("CONVERSATION_ID_MISSING", "The authenticated snapshot did not contain conversation_id.");
        if (!snapshot.ConversationId.Equals(expectedConversationId, StringComparison.OrdinalIgnoreCase))
            return Fail("CONVERSATION_ID_MISMATCH", "The authenticated snapshot belongs to a different conversation.");
        if (string.IsNullOrWhiteSpace(snapshot.CurrentNode))
            return Fail("CURRENT_NODE_MISSING", "The authenticated snapshot did not contain current_node.");
        if (snapshot.SnapshotTimestampUtc == default ||
            nowUtc - snapshot.SnapshotTimestampUtc > maximumAge ||
            snapshot.SnapshotTimestampUtc - nowUtc > TimeSpan.FromSeconds(5))
            return Fail("PRE_WAKE_SNAPSHOT_STALE", "The authenticated pre-wake snapshot timestamp is missing, stale, or in the future.");
        if (snapshot.BackendResponseTimestampUtc is null ||
            nowUtc - snapshot.BackendResponseTimestampUtc.Value > maximumAge ||
            snapshot.BackendResponseTimestampUtc.Value - nowUtc > TimeSpan.FromSeconds(5))
            return Fail("BACKEND_RESPONSE_STALE", "The authenticated backend response timestamp is missing, stale, or in the future.");
        if (!snapshot.DocumentVisibilityState.Equals("visible", StringComparison.OrdinalIgnoreCase))
            return Fail("ACTIVE_CONVERSATION_NOT_VISIBLE", $"The intended ChatGPT conversation tab is {snapshot.DocumentVisibilityState}; select that conversation tab before starting the pilot.");
        if (!snapshot.ApiVerified || snapshot.ApiStatusCode is < 200 or >= 300)
            return Fail("CONVERSATION_BACKEND_NOT_VERIFIED", "The authenticated conversation backend response was not successful.");
        if (!snapshot.Nodes.ContainsKey(snapshot.CurrentNode))
            return Fail("CURRENT_NODE_NOT_IN_MAPPING", "current_node is absent from the authenticated conversation mapping.");
        if (snapshot.CurrentPathMessageIds.Count == 0 ||
            !snapshot.CurrentPathMessageIds[^1].Equals(snapshot.CurrentNode, StringComparison.Ordinal))
            return Fail("CURRENT_PATH_MISSING_CURRENT_NODE", "The authenticated current-path ancestry does not terminate at current_node.");
        if (!snapshot.VisibleActiveBranchMessageIds.SequenceEqual(snapshot.CurrentPathMessageIds, StringComparer.Ordinal))
            return Fail("VISIBLE_BRANCH_LINEAGE_MISMATCH", "The browser-visible active branch is a sibling of the authenticated current path.");

        for (var index = 0; index < snapshot.CurrentPathMessageIds.Count; index++)
        {
            var messageId = snapshot.CurrentPathMessageIds[index];
            if (!snapshot.Nodes.TryGetValue(messageId, out var node))
                return Fail("CURRENT_PATH_NODE_MISSING", $"Current-path message {messageId} is absent from the authenticated mapping.");
            if (index > 0 && !node.ParentMessageId.Equals(snapshot.CurrentPathMessageIds[index - 1], StringComparison.Ordinal))
                return Fail("CURRENT_PATH_ANCESTRY_INVALID", $"Current-path message {messageId} does not descend from its recorded predecessor.");
        }

        if (!snapshot.BrowserBackendAgree ||
            !snapshot.BrowserVisibleMessageIds.Contains(snapshot.CurrentNode, StringComparer.Ordinal))
            return Fail("VISIBLE_BRANCH_BACKEND_MISMATCH", "The browser-visible branch and authenticated current_node do not agree.");

        return new PreWakeSnapshotGateResult(true, "OK", "The active visible conversation snapshot is current and backend-verified.");
    }

    private static PreWakeSnapshotGateResult EvaluateObservation(
        ActiveConversationObservation observation,
        string expectedConversationId)
    {
        if (string.IsNullOrWhiteSpace(observation.ConversationId))
            return Fail("CONVERSATION_ID_MISSING", "The active ChatGPT URL does not contain /c/{conversation_id}.");
        if (!observation.ConversationId.Equals(expectedConversationId, StringComparison.OrdinalIgnoreCase))
            return Fail("ACTIVE_CONVERSATION_ID_MISMATCH", "The selected ChatGPT tab is not the configured conversation.");
        if (!observation.DocumentReadyState.Equals("complete", StringComparison.OrdinalIgnoreCase))
            return Fail("ACTIVE_CONVERSATION_DOCUMENT_NOT_READY", $"The selected ChatGPT conversation document is {observation.DocumentReadyState}.");
        if (!observation.VisibilityState.Equals("visible", StringComparison.OrdinalIgnoreCase))
            return Fail("ACTIVE_CONVERSATION_NOT_VISIBLE", $"The intended ChatGPT conversation tab is {observation.VisibilityState}; select that conversation tab before starting the pilot.");
        if (observation.VisibleMessageCount <= 0)
            return Fail("ACTIVE_CONVERSATION_MESSAGES_NOT_RENDERED", "The selected conversation has no rendered message objects yet.");
        return new PreWakeSnapshotGateResult(true, "OK", "The configured ChatGPT conversation is selected, visible, and rendered.");
    }

    private static PreWakeSnapshotGateResult Fail(string reasonCode, string message) =>
        new(false, reasonCode, message);
}
