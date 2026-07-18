using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public enum PollActionLane
{
    None,
    GitHubReport,
    ChatGptEnvelope,
    PendingCodexTask
}

public sealed record PollDecision(
    PollActionLane Lane,
    bool ShouldWakeChatGpt,
    bool ShouldCaptureEnvelope,
    bool ShouldSendCodex,
    string Reason);

public static class PollCycleDecision
{
    public static PollDecision Decide(
        AppConfig config,
        AppState state,
        ReportCandidate? newReport,
        CapturedTaskEnvelope? envelope,
        string? envelopeFailureReason = null)
    {
        if (state.HasUnsentPendingCodexTask() &&
            (config.SubmitCodexPrompt || config.AutoSendCapturedTaskToCodex))
        {
            return new PollDecision(
                PollActionLane.PendingCodexTask,
                ShouldWakeChatGpt: false,
                ShouldCaptureEnvelope: false,
                ShouldSendCodex: true,
                Reason: $"Pending Codex task {state.PendingCodexTaskId} is ready.");
        }

        if (envelope is not null)
        {
            if (state.IsTaskAlreadyCapturedOrPending(envelope.TaskId) || state.IsCodexTaskSent(envelope.TaskId))
            {
                return new PollDecision(
                    PollActionLane.None,
                    ShouldWakeChatGpt: false,
                    ShouldCaptureEnvelope: false,
                    ShouldSendCodex: false,
                    Reason: $"Duplicate ChatGPT task {envelope.TaskId} suppressed.");
            }

            var actionability = EvaluateEnvelopeActionability(envelope, newReport, state);
            if (actionability.IsActionable)
            {
                return new PollDecision(
                    PollActionLane.ChatGptEnvelope,
                    ShouldWakeChatGpt: false,
                    ShouldCaptureEnvelope: true,
                    ShouldSendCodex: config.SubmitCodexPrompt || config.AutoSendCapturedTaskToCodex,
                    Reason: actionability.Reason);
            }

            if (newReport is null)
            {
                return new PollDecision(
                    PollActionLane.None,
                    ShouldWakeChatGpt: false,
                    ShouldCaptureEnvelope: false,
                    ShouldSendCodex: false,
                    Reason: actionability.Reason);
            }
        }

        if (newReport is not null)
        {
            return new PollDecision(
                PollActionLane.GitHubReport,
                ShouldWakeChatGpt: true,
                ShouldCaptureEnvelope: false,
                ShouldSendCodex: false,
                Reason: string.IsNullOrWhiteSpace(envelopeFailureReason)
                    ? $"New GitHub report {newReport.FileName} needs ChatGPT wake."
                    : $"New GitHub report {newReport.FileName} needs ChatGPT wake; envelope lane not actionable: {envelopeFailureReason}");
        }

        return new PollDecision(
            PollActionLane.None,
            ShouldWakeChatGpt: false,
            ShouldCaptureEnvelope: false,
            ShouldSendCodex: false,
            Reason: string.IsNullOrWhiteSpace(envelopeFailureReason)
                ? "No actionable GitHub report, ChatGPT envelope, or pending Codex task."
                : $"No actionable item. Envelope lane: {envelopeFailureReason}");
    }

    public static (bool IsActionable, string Reason) EvaluateEnvelopeActionability(
        CapturedTaskEnvelope envelope,
        ReportCandidate? latestReport,
        AppState state)
    {
        if (string.IsNullOrWhiteSpace(envelope.SourceReport))
        {
            return (false, "Envelope source_report is missing.");
        }

        var sourceMatchesLatestReport = latestReport is not null &&
            (envelope.SourceReport.Equals(latestReport.FileName, StringComparison.OrdinalIgnoreCase) ||
             envelope.SourceReport.Equals(latestReport.RelativePath, StringComparison.OrdinalIgnoreCase) ||
             latestReport.RelativePath.EndsWith(envelope.SourceReport, StringComparison.OrdinalIgnoreCase));

        var sourceMatchesLastWokenReport =
            !string.IsNullOrWhiteSpace(state.LastCompletedReportFileName) &&
            envelope.SourceReport.Equals(state.LastCompletedReportFileName, StringComparison.OrdinalIgnoreCase);

        var envelopeWorkItem = WorkItemIdParser.Parse(envelope.TaskId);
        var reportWorkItem = WorkItemIdParser.Parse(latestReport?.FileName ?? state.LastCompletedReportFileName);

        if (latestReport is null && string.IsNullOrWhiteSpace(state.LastCompletedReportFileName))
        {
            return (true, $"ChatGPT envelope {envelope.TaskId} is valid and no newer GitHub report is known.");
        }

        if (envelopeWorkItem is not null && reportWorkItem is not null)
        {
            if (envelopeWorkItem.Family.Equals(reportWorkItem.Family, StringComparison.OrdinalIgnoreCase))
            {
                return WorkItemIdParser.Compare(envelopeWorkItem, reportWorkItem) > 0
                    ? (true, $"ChatGPT envelope {envelopeWorkItem} is newer than report work item {reportWorkItem}.")
                    : (false, $"ChatGPT envelope {envelopeWorkItem} is not newer than report work item {reportWorkItem}.");
            }

            return sourceMatchesLatestReport || sourceMatchesLastWokenReport
                ? (true, $"Cross-family ChatGPT envelope {envelopeWorkItem} allowed because source_report matches the latest/woken report.")
                : (false, $"Cross-family ChatGPT envelope {envelopeWorkItem} requires source_report to match the latest/woken report.");
        }

        if (sourceMatchesLatestReport || sourceMatchesLastWokenReport)
        {
            return (true, "ChatGPT envelope allowed by source_report match.");
        }

        return latestReport is null
            ? (true, $"ChatGPT envelope {envelope.TaskId} is valid and no GitHub report candidate is available for comparison.")
            : (false, "ChatGPT envelope could not be matched to the latest GitHub report.");
    }

}
