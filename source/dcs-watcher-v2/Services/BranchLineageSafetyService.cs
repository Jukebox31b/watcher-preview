using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class BranchLineageSafetyService
{
    public const string DivergenceWarning = "BRANCH DIVERGENCE DETECTED — AUTOMATIC DELIVERY STOPPED";
    public const string AuthorizedCaptureMethod = "BackendMessageObject";

    public SafetyValidationResult Validate(
        WakeTransactionRecord transaction,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response)
    {
        var required = new Dictionary<string, string>
        {
            ["conversation_id"] = transaction.ConversationId,
            ["current_node_before_wake"] = transaction.CurrentNodeBeforeWake,
            ["visible_parent_message_id"] = transaction.VisibleParentMessageId,
            ["browser_tab_identity"] = transaction.BrowserTabIdentity,
            ["wake_token"] = transaction.WakeToken,
            ["intended_source_report"] = transaction.IntendedSourceReport,
            ["intended_active_task"] = transaction.IntendedActiveTask,
            ["wake_message_id"] = transaction.WakeMessageId,
            ["wake_parent_message_id"] = transaction.WakeParentMessageId,
            ["response_message_id"] = response.MessageId,
            ["response_parent_message_id"] = response.ParentMessageId,
            ["current_node_after_response"] = snapshot.CurrentNode
        };
        var missing = required.Where(item => string.IsNullOrWhiteSpace(item.Value)).Select(item => item.Key).ToArray();
        if (missing.Length > 0)
        {
            return Reject($"Required lineage field(s) unknown: {string.Join(", ", missing)}.");
        }

        if (!transaction.HumanConfirmed)
        {
            return Reject("The wake transaction was not explicitly confirmed by the human operator.");
        }

        if (!snapshot.ApiVerified || snapshot.ApiStatusCode is < 200 or >= 300 || !response.ApiVerified)
        {
            return Reject($"Backend/API message-object verification failed (HTTP {snapshot.ApiStatusCode}).", divergence: true);
        }

        if (!snapshot.BrowserBackendAgree)
        {
            return Reject("Browser-visible branch and backend conversation state disagree.", divergence: true);
        }

        if (!snapshot.ConversationId.Equals(transaction.ConversationId, StringComparison.Ordinal))
        {
            return Reject("Conversation ID changed during the wake transaction.", divergence: true);
        }

        if (!snapshot.BrowserTabIdentity.Equals(transaction.BrowserTabIdentity, StringComparison.Ordinal))
        {
            return Reject("Browser tab identity changed during the wake transaction.", divergence: true);
        }

        if (!response.CaptureMethod.Equals(AuthorizedCaptureMethod, StringComparison.Ordinal) || response.FallbackBody)
        {
            return Reject("Only an exact backend message object is eligible; fallback/body/page capture is diagnostic-only.");
        }

        if (!response.Role.Equals("assistant", StringComparison.Ordinal) || !response.Complete)
        {
            return Reject("Response must be a complete assistant message object.");
        }

        if (!response.ParentMessageId.Equals(transaction.WakeMessageId, StringComparison.Ordinal))
        {
            return Reject("Assistant response parent does not equal the exact wake message ID.", divergence: true);
        }

        if (response.OnCurrentPath is not true)
        {
            return Reject("Assistant response onCurrentPath is false or unknown.", divergence: true);
        }

        if (!response.WakeToken.Equals(transaction.WakeToken, StringComparison.Ordinal))
        {
            return Reject("Wake token does not match the bound wake transaction.", divergence: true);
        }

        if (!snapshot.Nodes.TryGetValue(transaction.WakeMessageId, out var wakeNode) ||
            !snapshot.Nodes.TryGetValue(response.MessageId, out var responseNode))
        {
            return Reject("Wake or response message object is missing from the backend conversation mapping.", divergence: true);
        }

        if (!wakeNode.ParentMessageId.Equals(transaction.WakeParentMessageId, StringComparison.Ordinal) ||
            !wakeNode.ParentMessageId.Equals(transaction.VisibleParentMessageId, StringComparison.Ordinal))
        {
            return Reject("Wake message parent does not match the recorded visible parent.", divergence: true);
        }

        if (!response.ParentMessageId.Equals(transaction.WakeMessageId, StringComparison.Ordinal))
        {
            return Reject("Captured response logical parent does not match the exact wake parent.", divergence: true);
        }

        if (!HasUniqueAuthenticatedResponseChain(snapshot, wakeNode, responseNode))
        {
            return Reject("The wake has multiple or ambiguous response paths instead of one non-actionable assistant chain ending at the visible response.", divergence: true);
        }

        if (snapshot.Nodes.TryGetValue(transaction.VisibleParentMessageId, out var visibleParent) &&
            (visibleParent.ChildMessageIds.Count != 1 ||
             !visibleParent.ChildMessageIds[0].Equals(transaction.WakeMessageId, StringComparison.Ordinal)))
        {
            return Reject("A sibling branch exists at the recorded visible parent.", divergence: true);
        }

        var currentPath = BuildAncestry(snapshot, snapshot.CurrentNode);
        if (currentPath.Count == 0 ||
            !currentPath.Contains(transaction.WakeMessageId, StringComparer.Ordinal) ||
            !currentPath.Contains(response.MessageId, StringComparer.Ordinal))
        {
            return Reject("Wake and response are not both ancestors of current_node.", divergence: true);
        }

        if (transaction.VisibleBranchAncestry.Count == 0 ||
            !transaction.VisibleBranchAncestry.Contains(transaction.CurrentNodeBeforeWake, StringComparer.Ordinal) ||
            !wakeNode.ParentMessageId.Equals(transaction.CurrentNodeBeforeWake, StringComparison.Ordinal))
        {
            return Reject("Pre-wake current_node lineage cannot be re-established.", divergence: true);
        }

        if (!snapshot.BrowserVisibleMessageIds.Contains(transaction.WakeMessageId, StringComparer.Ordinal) ||
            !snapshot.BrowserVisibleMessageIds.Contains(response.MessageId, StringComparer.Ordinal))
        {
            return Reject("Browser-visible current branch does not contain both the wake and response.", divergence: true);
        }

        var envelopes = ChatGptEnvelopeCapture.ExtractEnvelopes(response.Content);
        if (envelopes.Count != 1)
        {
            return Reject($"The identified assistant message must contain exactly one complete task envelope; found {envelopes.Count}.");
        }

        var envelope = envelopes[0];
        if (!response.Content.Contains(ChatGptEnvelopeCapture.CloseMarker, StringComparison.Ordinal) ||
            Count(response.Content, ChatGptEnvelopeCapture.OpenMarker) != 1 ||
            Count(response.Content, ChatGptEnvelopeCapture.CloseMarker) != 1)
        {
            return Reject("The identified assistant message contains a truncated or additional task envelope.");
        }

        var sourceReport = ReadField(envelope, "source_report");
        var taskId = ReadField(envelope, "task_id");
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(sourceReport))
        {
            return Reject("Envelope task_id or source_report is missing.");
        }

        if (!sourceReport.Equals(transaction.IntendedSourceReport, StringComparison.OrdinalIgnoreCase) ||
            !sourceReport.Equals(response.SourceReport, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("Envelope source_report does not match the bound report transaction.");
        }

        var envelopeBytes = Encoding.UTF8.GetBytes(envelope);
        var hash = Convert.ToHexString(SHA256.HashData(envelopeBytes)).ToLowerInvariant();
        return new SafetyValidationResult(true, false, "Response is bound to the exact current wake lineage.", envelope, taskId, hash);
    }

    public InstructionProvenanceRecord BuildProvenance(
        WakeTransactionRecord transaction,
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response,
        SafetyValidationResult validation,
        AuthorizedInstructionDeliveryMode deliveryMode,
        string destination)
    {
        var currentPath = BuildAncestry(snapshot, snapshot.CurrentNode);
        return new InstructionProvenanceRecord
        {
            ConversationId = transaction.ConversationId,
            WakeToken = transaction.WakeToken,
            WakeMessageId = transaction.WakeMessageId,
            AssistantMessageId = response.MessageId,
            AssistantParentMessageId = response.ParentMessageId,
            CurrentNodeBeforeWake = transaction.CurrentNodeBeforeWake,
            CurrentNodeAfterResponse = snapshot.CurrentNode,
            FullBranchLineageDigest = HashUtf8(string.Join("\n", currentPath)),
            OnCurrentPath = response.OnCurrentPath,
            ResponseRole = response.Role,
            ResponseCreationTimestampUtc = response.CreatedAtUtc,
            CaptureMethod = response.CaptureMethod,
            FallbackBody = response.FallbackBody,
            ApiVerificationResult = response.ApiVerified,
            EnvelopeSha256 = validation.EnvelopeSha256,
            EnvelopeByteCount = Encoding.UTF8.GetByteCount(validation.EnvelopeText),
            SourceReport = response.SourceReport,
            ActiveTask = transaction.IntendedActiveTask,
            DeliveryMode = deliveryMode.ToString(),
            HumanConfirmationState = transaction.HumanConfirmed,
            DeliveryDestination = destination,
            IpcDeliveryId = string.Empty,
            RejectionReason = validation.Eligible ? string.Empty : validation.Reason
        };
    }

    public static IReadOnlyList<string> BuildAncestry(ConversationLineageSnapshot snapshot, string nodeId)
    {
        var reversed = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = nodeId;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            reversed.Add(current);
            current = snapshot.Nodes.TryGetValue(current, out var node) ? node.ParentMessageId : string.Empty;
        }

        reversed.Reverse();
        return reversed;
    }

    public static IReadOnlyList<string> BuildActionableAncestry(
        ConversationLineageSnapshot snapshot,
        string currentNode,
        string wakeMessageId,
        string responseMessageId)
    {
        var ancestry = BuildAncestry(snapshot, currentNode).ToList();
        var wakeIndex = ancestry.IndexOf(wakeMessageId);
        var responseIndex = ancestry.IndexOf(responseMessageId);
        if (wakeIndex >= 0 && responseIndex > wakeIndex + 1)
        {
            ancestry.RemoveRange(wakeIndex + 1, responseIndex - wakeIndex - 1);
        }
        return ancestry;
    }

    private static bool HasUniqueAuthenticatedResponseChain(
        ConversationLineageSnapshot snapshot,
        ConversationNodeRecord wakeNode,
        ConversationNodeRecord responseNode)
    {
        if (wakeNode.ChildMessageIds.Count != 1)
        {
            return false;
        }

        var nextId = wakeNode.ChildMessageIds[0];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(nextId) && visited.Add(nextId) &&
               snapshot.Nodes.TryGetValue(nextId, out var node))
        {
            if (!node.Complete)
            {
                return false;
            }
            if (node.MessageId.Equals(responseNode.MessageId, StringComparison.Ordinal))
            {
                return node.Role.Equals("assistant", StringComparison.Ordinal);
            }
            var allowedIntermediate = node.Role.Equals("assistant", StringComparison.Ordinal)
                ? IsNonActionableInternalAssistant(node)
                : IsAuthenticatedPlatformRebaseNode(node, wakeNode, responseNode);
            if (!allowedIntermediate || node.ChildMessageIds.Count != 1 ||
                snapshot.BrowserVisibleMessageIds.Contains(node.MessageId, StringComparer.Ordinal))
            {
                return false;
            }
            nextId = node.ChildMessageIds[0];
        }
        return false;
    }

    private static bool IsNonActionableInternalAssistant(ConversationNodeRecord node) =>
        node.ContentType.Equals("model_editable_context", StringComparison.Ordinal) ||
        node.ContentType.Equals("reasoning_recap", StringComparison.Ordinal) ||
        node.ContentType.Equals("thoughts", StringComparison.Ordinal);

    private static bool IsAuthenticatedPlatformRebaseNode(
        ConversationNodeRecord node,
        ConversationNodeRecord wakeNode,
        ConversationNodeRecord responseNode) =>
        node.Role.Equals("system", StringComparison.Ordinal) &&
        node.ContentType.Equals("text", StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(node.Content) &&
        node.IsVisuallyHidden &&
        node.IsTemporalTurn &&
        node.RebaseSystemMessage != node.RebaseDeveloperMessage &&
        !string.IsNullOrWhiteSpace(node.RequestId) &&
        !string.IsNullOrWhiteSpace(node.TurnExchangeId) &&
        node.RequestId.Equals(wakeNode.RequestId, StringComparison.Ordinal) &&
        node.RequestId.Equals(responseNode.RequestId, StringComparison.Ordinal) &&
        node.TurnExchangeId.Equals(wakeNode.TurnExchangeId, StringComparison.Ordinal) &&
        node.TurnExchangeId.Equals(responseNode.TurnExchangeId, StringComparison.Ordinal);

    internal static string ReadField(string text, string fieldName)
    {
        var match = Regex.Match(text, $@"(?im)^\s*{Regex.Escape(fieldName)}\s*:\s*(?<value>[^\r\n]+)\r?$");
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    internal static string HashUtf8(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }

        return count;
    }

    private static SafetyValidationResult Reject(string reason, bool divergence = false)
    {
        return new SafetyValidationResult(false, divergence, divergence ? $"{DivergenceWarning}: {reason}" : reason);
    }
}

public sealed class CodexProvenanceValidator
{
    public SafetyValidationResult Validate(InstructionProvenanceRecord? provenance, string envelopeText, ActiveTaskLockRecord activeLock)
    {
        if (provenance is null)
        {
            return new SafetyValidationResult(false, false, "Codex rejected instruction: provenance is missing.");
        }

        if (!provenance.SchemaVersion.Equals("watcher-instruction-provenance-v1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(provenance.WakeMessageId) ||
            string.IsNullOrWhiteSpace(provenance.AssistantMessageId) ||
            !provenance.AssistantParentMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal) ||
            provenance.OnCurrentPath is not true ||
            provenance.FallbackBody is not false ||
            provenance.ApiVerificationResult is not true ||
            provenance.HumanConfirmationState is not true ||
            string.IsNullOrWhiteSpace(provenance.FullBranchLineageDigest))
        {
            return new SafetyValidationResult(false, false, "Codex rejected instruction: required authenticated lineage provenance is absent or invalid.");
        }

        var actualHash = BranchLineageSafetyService.HashUtf8(envelopeText);
        if (!actualHash.Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyValidationResult(false, false, "Codex rejected instruction: envelope hash does not match provenance.");
        }

        if (activeLock.IsActive)
        {
            return new SafetyValidationResult(false, false, $"Codex rejected instruction: active-task lock is held by {activeLock.ActiveTaskId}.");
        }

        return new SafetyValidationResult(true, false, "Codex provenance contract is valid.", envelopeText, EnvelopeSha256: actualHash);
    }
}
