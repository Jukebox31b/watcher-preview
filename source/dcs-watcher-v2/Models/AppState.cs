namespace DcsWatcherV2.Models;

public sealed class AppState
{
    public string OperatingStage { get; set; } = nameof(WatcherOperatingStage.Stage1DetectOnly);
    public ReportIngestionRecord? LastReportIngestion { get; set; }
    public List<ReportIngestionRecord> ReportIngestionHistory { get; set; } = [];
    public ActiveTaskLockRecord ActiveTaskLock { get; set; } = new();
    public WakeTransactionRecord? WakeTransaction { get; set; }
    public InstructionProvenanceRecord? LatestInstructionProvenance { get; set; }
    public TransactionAuditState TransactionAudit { get; set; } = new();
    public List<string> ConsumedReportSha256 { get; set; } = [];
    public List<string> UsedWakeTokens { get; set; } = [];
    public List<string> RecordedManualDeliveryHashes { get; set; } = [];
    public List<SupersessionRecord> InstructionSupersessions { get; set; } = [];
    public bool BranchDivergenceDetected { get; set; }
    public bool Stage3ManualPilotAttempted { get; set; }
    public string Stage3ManualPilotTransactionId { get; set; } = string.Empty;
    public string Stage3ManualPilotTerminalResult { get; set; } = string.Empty;
    public bool Stage4BootstrapConsumed { get; set; }
    public string Stage4BootstrapConsumedSha256 { get; set; } = string.Empty;
    public bool Stage4Halted { get; set; }
    public string Stage4LastResult { get; set; } = string.Empty;
    public string Stage4LastTransactionId { get; set; } = string.Empty;
    public int Stage4DeliveredCount { get; set; }
    public DateTimeOffset? Stage4LastProgressAtUtc { get; set; }
    public bool WatcherRunning { get; set; }
    public string CurrentBranch { get; set; } = "(unknown)";
    public string? LastReportSeen { get; set; }
    public string? LastCompletedReportPath { get; set; }
    public string? LastCompletedReportFingerprint { get; set; }
    public string? LastCompletedReportFileName { get; set; }
    public DateTimeOffset? LastCompletedReportSeenAtUtc { get; set; }
    public string LastChatGptWakePrompt { get; set; } = string.Empty;
    public DateTimeOffset? LastChatGptWakePromptBuiltAtUtc { get; set; }
    public DateTimeOffset? LastChatGptWakeSentAtUtc { get; set; }
    public DateTimeOffset? LastChatGptConversationRefreshAtUtc { get; set; }
    public string LastChatGptWakeToken { get; set; } = string.Empty;
    public string LastChatGptWakeResult { get; set; } = string.Empty;
    public string LastChatGptTargetUrl { get; set; } = string.Empty;
    public int LastReportWakeCount { get; set; }
    public string LastDetectedGitHubReportTaskId { get; set; } = string.Empty;
    public string LastDetectedGitHubReportWorkItem { get; set; } = string.Empty;
    public string LastDetectedChatGptEnvelopeTaskId { get; set; } = string.Empty;
    public string LastDetectedChatGptEnvelopeWorkItem { get; set; } = string.Empty;
    public string LastActionableLane { get; set; } = "None";
    public DateTimeOffset? LastPollCycleAtUtc { get; set; }
    public string LastPollCycleSummary { get; set; } = string.Empty;
    public bool ReportWakePending { get; set; }
    public bool CapturePending { get; set; }
    public bool CodexDeliveryPending { get; set; }
    public DateTimeOffset? LastCaptureAttemptAtUtc { get; set; }
    public string LastCaptureResult { get; set; } = string.Empty;
    public string LastCapturedTaskId { get; set; } = string.Empty;
    public string LastCapturedSourceReport { get; set; } = string.Empty;
    public string LastCapturedEnvelopePath { get; set; } = string.Empty;
    public string LastCapturedInstructionPath { get; set; } = string.Empty;
    public DateTimeOffset? LastCapturedTaskCreatedAt { get; set; }
    public string PendingCodexTaskPath { get; set; } = string.Empty;
    public string PendingCodexTaskId { get; set; } = string.Empty;
    public DateTimeOffset? LastChatGptWakeAt { get; set; }
    public DateTimeOffset? LastTaskCapturedAt { get; set; }
    public DateTimeOffset? LastCodexSendAt { get; set; }
    public DateTimeOffset? LastCodexSendAttemptAtUtc { get; set; }
    public DateTimeOffset? LastCodexSendSucceededAtUtc { get; set; }
    public string LastCodexSendResult { get; set; } = string.Empty;
    public string LastCodexDeliveredTaskId { get; set; } = string.Empty;
    public string LastCodexDeliveryMode { get; set; } = string.Empty;
    public DateTimeOffset? LastCodexIpcAttemptAtUtc { get; set; }
    public string LastCodexIpcResult { get; set; } = string.Empty;
    public DateTimeOffset? LastCodexIpcConfirmedAtUtc { get; set; }
    public string LastCodexIpcError { get; set; } = string.Empty;
    public string LastCodexDeliveryTransportUsed { get; set; } = string.Empty;
    public string LastCodexThreadId { get; set; } = string.Empty;
    public bool LastCodexFallbackUsed { get; set; }
    public string LastCodexHandoffPrompt { get; set; } = string.Empty;
    public string LastCodexHandoffPromptPath { get; set; } = string.Empty;
    public string HighestCodexDeliveredWorkItem { get; set; } = string.Empty;
    public string LastCodexDeliveredSourceReport { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public string? LatestTaskId { get; set; }
    public string? LatestTaskFilePath { get; set; }
    public string? LatestEnvelopePath { get; set; }
    public string? LatestReportLink { get; set; }
    public string HighestReportSortKeySeen { get; set; } = string.Empty;
    public string HighestReportFileNameSeen { get; set; } = string.Empty;
    public string HighestReportWorkItemSeen { get; set; } = string.Empty;
    public string LastReportWokenFileName { get; set; } = string.Empty;
    public string LastReportWokenSortKey { get; set; } = string.Empty;
    public DateTimeOffset? LastReportWokenAtUtc { get; set; }
    public List<string> ProcessedReports { get; set; } = [];
    public List<string> WokenReportFileNames { get; set; } = [];
    public List<string> SuppressedStaleReportFileNames { get; set; } = [];
    public List<string> QuarantinedReportFileNames { get; set; } = [];
    public List<string> ReportWakeHistory { get; set; } = [];
    public List<string> SentTaskIds { get; set; } = [];
    public Dictionary<string, CompletedReportRecord> CompletedReports { get; set; } = [];
    public Dictionary<string, DateTimeOffset> BusyReportsFirstSeenAtUtc { get; set; } = [];
    public Dictionary<string, CapturedTaskRecord> CapturedTasks { get; set; } = [];
    public List<string> CaptureFailures { get; set; } = [];
    public int CaptureFailureCount { get; set; }
    public Dictionary<string, ReportTaskStageRecord> ReportStages { get; set; } = [];
    public Dictionary<string, CodexSentTaskRecord> SentCodexTasks { get; set; } = [];
    public List<string> SentCodexSourceReports { get; set; } = [];
    public List<string> SuppressedStaleCodexTaskIds { get; set; } = [];
    public List<string> CodexDeliveryHistory { get; set; } = [];
    public List<string> CodexDeliveryFailures { get; set; } = [];
    public int CodexDeliveryFailureCount { get; set; }

    public void RememberReport(string reportKey)
    {
        if (!ProcessedReports.Contains(reportKey, StringComparer.OrdinalIgnoreCase))
        {
            ProcessedReports.Add(reportKey);
        }
    }

    public bool IsReportCompleted(string relativePath, string fingerprint)
    {
        return CompletedReports.ContainsKey(fingerprint)
            || CompletedReports.ContainsKey(relativePath)
            || ProcessedReports.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
            || ProcessedReports.Contains(fingerprint, StringComparer.OrdinalIgnoreCase);
    }

    public bool WasReportWoken(string fileName)
    {
        return WokenReportFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    public bool ShouldRetryWokenReport(ReportCandidate report, TimeSpan retryAfter, out string reason)
    {
        reason = string.Empty;
        if (!ReportStages.TryGetValue(report.Fingerprint, out var stage))
        {
            return false;
        }

        if (!stage.ReportPath.EndsWith(report.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(stage.CapturedTaskId))
        {
            return false;
        }

        if (HasCapturedTaskForReport(report))
        {
            return false;
        }

        if (!stage.CaptureStatus.Equals("capture_failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var completedRecord = CompletedReports.TryGetValue(report.Fingerprint, out var byFingerprint)
            ? byFingerprint
            : CompletedReports.TryGetValue(report.RelativePath, out var byPath)
                ? byPath
                : null;
        if ((completedRecord?.WakeCount ?? 0) >= 1)
        {
            reason = "capture failed after a confirmed wake; automatic re-wake is suppressed and capture sweeps will continue";
            return false;
        }

        if (stage.WakeSentAtUtc is null)
        {
            reason = "previous wake has no sent timestamp and capture failed";
            return true;
        }

        var retryAt = stage.WakeSentAtUtc.Value.Add(retryAfter);
        if (DateTimeOffset.UtcNow < retryAt)
        {
            reason = $"capture failed but retry cooldown has not elapsed; retry after {retryAt:O}";
            return false;
        }

        reason = $"capture failed after previous wake at {stage.WakeSentAtUtc:O} and no task was captured from this source_report";
        return true;
    }

    public bool HasCapturedTaskForReport(ReportCandidate report)
    {
        return CapturedTasks.Values.Any(captured => IsSourceReportMatch(report, captured.SourceReport));
    }

    public bool HasFailedCaptureAwaitingTask()
    {
        return !string.IsNullOrWhiteSpace(LastCompletedReportFingerprint) &&
               ReportStages.TryGetValue(LastCompletedReportFingerprint, out var stage) &&
               stage.CaptureStatus.Equals("capture_failed", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(stage.CapturedTaskId);
    }

    public bool ShouldRefreshChatGptBeforeCapture(TimeSpan minimumAge)
    {
        if (string.IsNullOrWhiteSpace(LastCompletedReportFingerprint) ||
            !ReportStages.TryGetValue(LastCompletedReportFingerprint, out var stage) ||
            !stage.CaptureStatus.Equals("capture_failed", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(stage.CapturedTaskId) ||
            stage.WakeSentAtUtc is null)
        {
            return false;
        }

        var lastSyncAt = LastChatGptConversationRefreshAtUtc is { } refreshedAt && refreshedAt > stage.WakeSentAtUtc.Value
            ? refreshedAt
            : stage.WakeSentAtUtc.Value;
        return DateTimeOffset.UtcNow - lastSyncAt >= minimumAge;
    }

    public bool IsSourceReportMatch(ReportCandidate report, string sourceReport)
    {
        if (string.IsNullOrWhiteSpace(sourceReport))
        {
            return false;
        }

        return sourceReport.Equals(report.FileName, StringComparison.OrdinalIgnoreCase) ||
               sourceReport.Equals(report.RelativePath, StringComparison.OrdinalIgnoreCase) ||
               report.RelativePath.EndsWith(sourceReport, StringComparison.OrdinalIgnoreCase);
    }

    public void MarkReportBaselineSeen(ReportCandidate report, DateTimeOffset seenAtUtc)
    {
        var identity = ReportIdentity.FromCandidate(report);
        LastReportSeen = report.GitHubBlobUrl;
        LatestReportLink = report.GitHubBlobUrl;
        UpdateReportHighWater(identity);
        AddUnique(SuppressedStaleReportFileNames, report.FileName);
        AddReportWakeHistory($"BASELINE {seenAtUtc:O} {report.FileName} {identity.SortKey}");
    }

    public void MarkReportSuppressed(ReportCandidate report, string reason)
    {
        AddUnique(SuppressedStaleReportFileNames, report.FileName);
        AddReportWakeHistory($"SUPPRESSED {DateTimeOffset.UtcNow:O} {report.FileName} {reason}");
    }

    public void MarkReportQuarantined(ReportCandidate report, string reason)
    {
        AddUnique(QuarantinedReportFileNames, report.FileName);
        AddReportWakeHistory($"QUARANTINED {DateTimeOffset.UtcNow:O} {report.FileName} {reason}");
    }

    public void MarkReportWoken(ReportCandidate report, DateTimeOffset wokenAtUtc)
    {
        var identity = ReportIdentity.FromCandidate(report);
        AddUnique(WokenReportFileNames, report.FileName);
        LastReportWokenFileName = report.FileName;
        LastReportWokenSortKey = identity.SortKey;
        LastReportWokenAtUtc = wokenAtUtc;
        UpdateReportHighWater(identity);
        AddReportWakeHistory($"WOKEN {wokenAtUtc:O} {report.FileName} {identity.SortKey}");
    }

    public CompletedReportRecord MarkReportCompleted(
        ReportCandidate report,
        string wakePrompt,
        DateTimeOffset seenAtUtc,
        bool isResend,
        string wakeToken)
    {
        var existing = CompletedReports.TryGetValue(report.Fingerprint, out var byFingerprint)
            ? byFingerprint
            : CompletedReports.TryGetValue(report.RelativePath, out var byPath)
                ? byPath
                : null;

        var wakeCount = existing?.WakeCount ?? 0;
        wakeCount = isResend ? wakeCount + 1 : Math.Max(1, wakeCount + 1);

        var record = new CompletedReportRecord
        {
            RelativePath = report.RelativePath,
            FileName = report.FileName,
            Fingerprint = report.Fingerprint,
            GitHubBlobUrl = report.GitHubBlobUrl,
            FirstSeenAtUtc = existing?.FirstSeenAtUtc ?? seenAtUtc,
            LastSeenAtUtc = seenAtUtc,
            LastWakePromptBuiltAtUtc = seenAtUtc,
            LastWakeSentAtUtc = LastChatGptWakeSentAtUtc ?? seenAtUtc,
            LastWakeToken = wakeToken,
            WakeCount = wakeCount
        };

        CompletedReports[report.Fingerprint] = record;
        CompletedReports[report.RelativePath] = record;
        RememberReport(report.RelativePath);
        RememberReport(report.Fingerprint);

        LastReportSeen = report.GitHubBlobUrl;
        LatestReportLink = report.GitHubBlobUrl;
        LastCompletedReportPath = report.RelativePath;
        LastCompletedReportFingerprint = report.Fingerprint;
        LastCompletedReportFileName = report.FileName;
        LastCompletedReportSeenAtUtc = seenAtUtc;
        LastChatGptWakePrompt = wakePrompt;
        LastChatGptWakePromptBuiltAtUtc = seenAtUtc;
        LastChatGptWakeAt = seenAtUtc;
        LastChatGptWakeToken = wakeToken;
        LastReportWakeCount = wakeCount;
        MarkReportWoken(report, seenAtUtc);
        BusyReportsFirstSeenAtUtc.Remove(report.Fingerprint);
        ReportStages[report.Fingerprint] = new ReportTaskStageRecord
        {
            ReportPath = report.RelativePath,
            Fingerprint = report.Fingerprint,
            WakeSentAtUtc = LastChatGptWakeSentAtUtc ?? seenAtUtc,
            CaptureStatus = "wake_sent"
        };

        return record;
    }

    public void RecordWakePromptBuilt(ReportCandidate report, string wakePrompt, string wakeToken, DateTimeOffset builtAtUtc)
    {
        LastReportSeen = report.GitHubBlobUrl;
        LatestReportLink = report.GitHubBlobUrl;
        LastChatGptWakePrompt = wakePrompt;
        LastChatGptWakePromptBuiltAtUtc = builtAtUtc;
        LastChatGptWakeAt = builtAtUtc;
        LastChatGptWakeToken = wakeToken;
        LastChatGptWakeResult = "Prompt built.";
    }

    public void ClearChatGptWakePromptPreview(string reason)
    {
        LastChatGptWakePrompt = string.Empty;
        LastChatGptWakePromptBuiltAtUtc = null;
        LastChatGptWakeToken = string.Empty;
        LastChatGptWakeResult = reason;
    }

    public TimeSpan RecordReportBusy(ReportCandidate report, DateTimeOffset busyAtUtc)
    {
        if (!BusyReportsFirstSeenAtUtc.TryGetValue(report.Fingerprint, out var firstBusyAtUtc))
        {
            firstBusyAtUtc = busyAtUtc;
            BusyReportsFirstSeenAtUtc[report.Fingerprint] = firstBusyAtUtc;
        }

        return busyAtUtc - firstBusyAtUtc;
    }

    private void UpdateReportHighWater(ReportIdentity identity)
    {
        if (string.CompareOrdinal(identity.SortKey, HighestReportSortKeySeen) > 0)
        {
            HighestReportSortKeySeen = identity.SortKey;
            HighestReportFileNameSeen = identity.FileName;
            HighestReportWorkItemSeen = string.IsNullOrWhiteSpace(identity.WorkItemFamily) || identity.WorkItemNumber is null
                ? string.Empty
                : $"{identity.WorkItemFamily}{identity.WorkItemNumber}";
        }
    }

    private void AddReportWakeHistory(string entry)
    {
        ReportWakeHistory.Add(entry);
        if (ReportWakeHistory.Count > 200)
        {
            ReportWakeHistory.RemoveRange(0, ReportWakeHistory.Count - 200);
        }
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }

    public bool IsTaskAlreadyCapturedOrPending(string taskId)
    {
        return CapturedTasks.ContainsKey(taskId)
            || (!IsCodexTaskSent(taskId) &&
                PendingCodexTaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasUnsentPendingCodexTask()
    {
        return !string.IsNullOrWhiteSpace(PendingCodexTaskPath)
            && !string.IsNullOrWhiteSpace(PendingCodexTaskId)
            && !IsCodexTaskSent(PendingCodexTaskId);
    }

    public void ReconcileCodexDeliveryPending()
    {
        RecoverDefinitelyRejectedCodexDelivery();

        if (!string.IsNullOrWhiteSpace(PendingCodexTaskId) &&
            IsCodexTaskSent(PendingCodexTaskId))
        {
            PendingCodexTaskId = string.Empty;
            PendingCodexTaskPath = string.Empty;
        }

        CodexDeliveryPending = HasUnsentPendingCodexTask();
    }

    public bool RecoverDefinitelyRejectedCodexDelivery()
    {
        var taskId = LastCapturedTaskId;
        if (string.IsNullOrWhiteSpace(taskId) ||
            !CapturedTasks.TryGetValue(taskId, out var captured) ||
            !SentCodexTasks.TryGetValue(taskId, out var sent) ||
            !IsDefinitelyRejectedCodexDelivery(sent.Result))
        {
            return false;
        }

        SentCodexTasks.Remove(taskId);
        SentTaskIds.RemoveAll(existing => existing.Equals(taskId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(captured.SourceReport))
        {
            SentCodexSourceReports.RemoveAll(existing => existing.Equals(captured.SourceReport, StringComparison.OrdinalIgnoreCase));
        }

        PendingCodexTaskId = taskId;
        PendingCodexTaskPath = captured.InstructionPath;
        CodexDeliveryPending = true;
        LastCodexSendResult = $"Recovered definitely rejected Codex delivery for automatic retry: {taskId}.";
        AddCodexDeliveryHistory($"AUTO_RETRY_RECOVERED {DateTimeOffset.UtcNow:O} {taskId}");
        return true;
    }

    private static bool IsDefinitelyRejectedCodexDelivery(string result)
    {
        return result.Contains("could not find an owner", StringComparison.OrdinalIgnoreCase) ||
               result.Contains("no-client-found", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasActiveChatGptCaptureWindow(TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(LastCompletedReportFingerprint) ||
            !ReportStages.TryGetValue(LastCompletedReportFingerprint, out var stage) ||
            !string.IsNullOrWhiteSpace(stage.CapturedTaskId) ||
            stage.WakeSentAtUtc is null)
        {
            return false;
        }

        if (stage.CaptureStatus.Equals("task_captured", StringComparison.OrdinalIgnoreCase) ||
            stage.CaptureStatus.Equals("codex_delivery_pending", StringComparison.OrdinalIgnoreCase) ||
            stage.CaptureStatus.Equals("codex_delivery_complete", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - stage.WakeSentAtUtc.Value <= timeout;
    }

    public CapturedTaskRecord MarkTaskCaptured(
        CapturedTaskEnvelope envelope,
        string envelopePath,
        string instructionPath,
        string metadataPath,
        DateTimeOffset capturedAtUtc)
    {
        var record = new CapturedTaskRecord
        {
            TaskId = envelope.TaskId,
            SourceReport = envelope.SourceReport,
            CreatedAt = envelope.CreatedAt,
            CapturedAtUtc = capturedAtUtc,
            EnvelopePath = envelopePath,
            InstructionPath = instructionPath,
            MetadataPath = metadataPath,
            Repo = envelope.Repo,
            Target = envelope.Target,
            Mode = envelope.Mode
        };

        CapturedTasks[envelope.TaskId] = record;
        LastCaptureAttemptAtUtc = capturedAtUtc;
        LastCaptureResult = $"Captured task {envelope.TaskId}.";
        LastCapturedTaskId = envelope.TaskId;
        LastCapturedSourceReport = envelope.SourceReport;
        LastCapturedEnvelopePath = envelopePath;
        LastCapturedInstructionPath = instructionPath;
        LastCapturedTaskCreatedAt = envelope.CreatedAt;
        PendingCodexTaskPath = instructionPath;
        PendingCodexTaskId = envelope.TaskId;
        LatestTaskId = envelope.TaskId;
        LatestTaskFilePath = instructionPath;
        LatestEnvelopePath = envelopePath;
        LastTaskCapturedAt = capturedAtUtc;
        CapturePending = false;
        CodexDeliveryPending = true;

        var stage = ReportStages.Values.FirstOrDefault(existing =>
            existing.ReportPath.EndsWith(envelope.SourceReport, StringComparison.OrdinalIgnoreCase));
        if (stage is not null)
        {
            stage.CaptureStatus = "task_captured";
            stage.CapturedTaskId = envelope.TaskId;
        }

        return record;
    }

    public void MarkCapturePending()
    {
        LastCaptureAttemptAtUtc = DateTimeOffset.UtcNow;
        LastCaptureResult = "Capture pending.";
        CapturePending = true;
        if (!string.IsNullOrWhiteSpace(LastCompletedReportFingerprint) &&
            ReportStages.TryGetValue(LastCompletedReportFingerprint, out var stage))
        {
            if (!stage.CaptureStatus.Equals("capture_failed", StringComparison.OrdinalIgnoreCase))
            {
                stage.CaptureStatus = "capture_pending";
            }
        }
    }

    public void MarkCaptureFailed(string reason)
    {
        LastCaptureAttemptAtUtc = DateTimeOffset.UtcNow;
        LastCaptureResult = reason;
        CapturePending = false;
        CaptureFailureCount++;
        CaptureFailures.Add($"{DateTimeOffset.UtcNow:O} {reason}");
        if (CaptureFailures.Count > 100)
        {
            CaptureFailures.RemoveRange(0, CaptureFailures.Count - 100);
        }

        if (!string.IsNullOrWhiteSpace(LastCompletedReportFingerprint) &&
            ReportStages.TryGetValue(LastCompletedReportFingerprint, out var stage))
        {
            if (stage.CaptureStatus.Equals("task_captured", StringComparison.OrdinalIgnoreCase) ||
                stage.CaptureStatus.Equals("codex_delivery_pending", StringComparison.OrdinalIgnoreCase) ||
                stage.CaptureStatus.Equals("codex_delivery_complete", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(stage.CapturedTaskId))
            {
                return;
            }

            stage.CaptureStatus = "capture_failed";
        }
    }

    public void RememberSentTask(string taskId)
    {
        if (!SentTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase))
        {
            SentTaskIds.Add(taskId);
        }
    }

    public bool IsCodexTaskSent(string taskId)
    {
        return SentCodexTasks.ContainsKey(taskId) ||
               SentTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryPrepareManualCodexRetryFromLastUnconfirmed(out string message)
    {
        message = string.Empty;
        var taskId = LastCapturedTaskId;
        if (string.IsNullOrWhiteSpace(taskId) ||
            !CapturedTasks.TryGetValue(taskId, out var captured))
        {
            message = "No captured task is available for manual Codex retry.";
            return false;
        }

        if (!SentCodexTasks.TryGetValue(taskId, out var sent) ||
            !sent.Result.StartsWith("DELIVERY_UNCONFIRMED_SUPPRESSED:", StringComparison.OrdinalIgnoreCase))
        {
            message = $"Latest captured task {taskId} is not an unconfirmed Codex delivery.";
            return false;
        }

        SentCodexTasks.Remove(taskId);
        SentTaskIds.RemoveAll(existing => existing.Equals(taskId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(captured.SourceReport))
        {
            SentCodexSourceReports.RemoveAll(existing => existing.Equals(captured.SourceReport, StringComparison.OrdinalIgnoreCase));
        }

        PendingCodexTaskId = taskId;
        PendingCodexTaskPath = captured.InstructionPath;
        CodexDeliveryPending = true;
        LastCodexSendResult = $"Manual retry prepared for unconfirmed Codex task {taskId}.";
        AddCodexDeliveryHistory($"MANUAL_RETRY_PREPARED {DateTimeOffset.UtcNow:O} {taskId}");
        message = $"Manual retry prepared for unconfirmed Codex task {taskId}.";
        return true;
    }

    public bool WasCodexSourceReportSent(string sourceReport)
    {
        if (string.IsNullOrWhiteSpace(sourceReport))
        {
            return false;
        }

        if (SentCodexSourceReports.Contains(sourceReport, StringComparer.OrdinalIgnoreCase) ||
            SentCodexTasks.Values.Any(sent => sent.SourceReport.Equals(sourceReport, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return SentCodexTasks.Keys.Any(sentTaskId =>
            CapturedTasks.TryGetValue(sentTaskId, out var captured) &&
            captured.SourceReport.Equals(sourceReport, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsStaleCodexTask(CapturedTaskRecord? task, out string reason)
    {
        reason = string.Empty;
        if (task is null)
        {
            return false;
        }

        var taskWorkItem = global::DcsWatcherV2.Services.WorkItemIdParser.Parse(task.TaskId);
        if (taskWorkItem is null)
        {
            return false;
        }

        var highestWorkItem = GetHighestCodexDeliveredWorkItem(taskWorkItem.Family);
        if (highestWorkItem is null)
        {
            return false;
        }

        if (taskWorkItem.Family.Equals(highestWorkItem.Family, StringComparison.OrdinalIgnoreCase) &&
            global::DcsWatcherV2.Services.WorkItemIdParser.Compare(taskWorkItem, highestWorkItem) < 0)
        {
            reason = $"task work item {taskWorkItem} is older than delivered high-water {highestWorkItem}";
            return true;
        }

        return false;
    }

    public void MarkCodexTaskSuppressed(string taskId, string reason)
    {
        AddUnique(SuppressedStaleCodexTaskIds, taskId);
        AddCodexDeliveryHistory($"SUPPRESSED {DateTimeOffset.UtcNow:O} {taskId} {reason}");
    }

    public void MarkCodexDeliveryAttempt(
        string taskId,
        string mode,
        string handoffPrompt,
        string handoffPromptPath,
        string result)
    {
        LastCodexSendAttemptAtUtc = DateTimeOffset.UtcNow;
        LastCodexSendResult = result;
        LastCodexDeliveredTaskId = taskId;
        LastCodexDeliveryMode = mode;
        LastCodexHandoffPrompt = handoffPrompt;
        LastCodexHandoffPromptPath = handoffPromptPath;
        LastCodexSendAt = LastCodexSendAttemptAtUtc;
        CodexDeliveryPending = true;
    }

    public void MarkCodexIpcAttempt(string threadId, string transport)
    {
        LastCodexIpcAttemptAtUtc = DateTimeOffset.UtcNow;
        LastCodexIpcResult = "Connecting";
        LastCodexIpcError = string.Empty;
        LastCodexDeliveryTransportUsed = transport;
        LastCodexThreadId = threadId;
        LastCodexFallbackUsed = false;
    }

    public void MarkCodexIpcResult(bool confirmed, string result, string error)
    {
        LastCodexIpcResult = result;
        LastCodexIpcError = error;
        if (confirmed)
        {
            LastCodexIpcConfirmedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkCodexFallbackUsed(bool fallbackUsed)
    {
        LastCodexFallbackUsed = fallbackUsed;
    }

    public void MarkCodexDeliverySucceeded(string taskId, string mode, string handoffPromptPath, string result)
    {
        var succeededAt = DateTimeOffset.UtcNow;
        CapturedTasks.TryGetValue(taskId, out var captured);
        LastCodexSendSucceededAtUtc = succeededAt;
        LastCodexSendAt = succeededAt;
        LastCodexSendResult = result;
        LastCodexDeliveredTaskId = taskId;
        LastCodexDeliveryMode = mode;
        LastCodexHandoffPromptPath = handoffPromptPath;
        LastCodexDeliveredSourceReport = captured?.SourceReport ?? LastCodexDeliveredSourceReport;
        AddUnique(SentCodexSourceReports, captured?.SourceReport ?? string.Empty);
        UpdateCodexDeliveredHighWater(taskId);
        RememberSentTask(taskId);
        AddCodexDeliveryHistory($"SENT {succeededAt:O} {taskId} source_report={captured?.SourceReport ?? string.Empty}");
        SentCodexTasks[taskId] = new CodexSentTaskRecord
        {
            TaskId = taskId,
            SentAtUtc = succeededAt,
            DeliveryMode = mode,
            HandoffPromptPath = handoffPromptPath,
            Result = result,
            SourceReport = captured?.SourceReport ?? string.Empty,
            WorkItem = global::DcsWatcherV2.Services.WorkItemIdParser.Parse(taskId)?.ToString() ?? string.Empty
        };

        if (PendingCodexTaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
        {
            PendingCodexTaskId = string.Empty;
            PendingCodexTaskPath = string.Empty;
        }

        if (captured is not null)
        {
            var stage = ReportStages.Values.FirstOrDefault(existing =>
                existing.ReportPath.EndsWith(captured.SourceReport, StringComparison.OrdinalIgnoreCase));
            if (stage is not null)
            {
                stage.CaptureStatus = "codex_delivery_complete";
                stage.CapturedTaskId = taskId;
            }
        }

        ReconcileCodexDeliveryPending();
    }

    public void MarkCodexDeliveryRequestSentUnconfirmed(string taskId, string mode, string handoffPromptPath, string result)
    {
        var sentAt = DateTimeOffset.UtcNow;
        CapturedTasks.TryGetValue(taskId, out var captured);
        LastCodexSendAt = sentAt;
        LastCodexSendResult = result;
        LastCodexDeliveredTaskId = taskId;
        LastCodexDeliveryMode = mode;
        LastCodexHandoffPromptPath = handoffPromptPath;
        LastCodexDeliveredSourceReport = captured?.SourceReport ?? LastCodexDeliveredSourceReport;
        AddUnique(SentCodexSourceReports, captured?.SourceReport ?? string.Empty);
        UpdateCodexDeliveredHighWater(taskId);
        RememberSentTask(taskId);
        AddCodexDeliveryHistory($"SENT_UNCONFIRMED {sentAt:O} {taskId} source_report={captured?.SourceReport ?? string.Empty}");
        SentCodexTasks[taskId] = new CodexSentTaskRecord
        {
            TaskId = taskId,
            SentAtUtc = sentAt,
            DeliveryMode = mode,
            HandoffPromptPath = handoffPromptPath,
            Result = result,
            SourceReport = captured?.SourceReport ?? string.Empty,
            WorkItem = global::DcsWatcherV2.Services.WorkItemIdParser.Parse(taskId)?.ToString() ?? string.Empty
        };

        if (PendingCodexTaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
        {
            PendingCodexTaskId = string.Empty;
            PendingCodexTaskPath = string.Empty;
        }

        if (captured is not null)
        {
            var stage = ReportStages.Values.FirstOrDefault(existing =>
                existing.ReportPath.EndsWith(captured.SourceReport, StringComparison.OrdinalIgnoreCase));
            if (stage is not null)
            {
                stage.CaptureStatus = "codex_delivery_unconfirmed_request_sent";
                stage.CapturedTaskId = taskId;
            }
        }

        ReconcileCodexDeliveryPending();
    }

    public void MarkCodexDeliveryFailed(string taskId, string mode, string handoffPromptPath, string result)
    {
        LastCodexSendAttemptAtUtc = DateTimeOffset.UtcNow;
        LastCodexSendAt = LastCodexSendAttemptAtUtc;
        LastCodexSendResult = result;
        LastCodexDeliveredTaskId = taskId;
        LastCodexDeliveryMode = mode;
        LastCodexHandoffPromptPath = handoffPromptPath;
        CodexDeliveryPending = true;
        CodexDeliveryFailureCount++;
        CodexDeliveryFailures.Add($"{DateTimeOffset.UtcNow:O} {taskId} {result}");
        if (CodexDeliveryFailures.Count > 100)
        {
            CodexDeliveryFailures.RemoveRange(0, CodexDeliveryFailures.Count - 100);
        }
    }

    private void UpdateCodexDeliveredHighWater(string taskId)
    {
        var taskWorkItem = global::DcsWatcherV2.Services.WorkItemIdParser.Parse(taskId);
        var highestWorkItem = GetHighestCodexDeliveredWorkItem(taskWorkItem?.Family ?? string.Empty);
        if (taskWorkItem is null)
        {
            return;
        }

        if (highestWorkItem is null ||
            !taskWorkItem.Family.Equals(highestWorkItem.Family, StringComparison.OrdinalIgnoreCase) ||
            global::DcsWatcherV2.Services.WorkItemIdParser.Compare(taskWorkItem, highestWorkItem) > 0)
        {
            HighestCodexDeliveredWorkItem = taskWorkItem.ToString();
        }
    }

    private WorkItemId? GetHighestCodexDeliveredWorkItem(string family)
    {
        WorkItemId? highest = null;
        Consider(HighestCodexDeliveredWorkItem);

        foreach (var taskId in SentTaskIds)
        {
            Consider(taskId);
        }

        foreach (var sent in SentCodexTasks)
        {
            Consider(sent.Key);
            Consider(sent.Value.TaskId);
            Consider(sent.Value.WorkItem);
        }

        return highest;

        void Consider(string? value)
        {
            var parsed = global::DcsWatcherV2.Services.WorkItemIdParser.Parse(value);
            if (parsed is null ||
                !parsed.Family.Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (highest is null || global::DcsWatcherV2.Services.WorkItemIdParser.Compare(parsed, highest) > 0)
            {
                highest = parsed;
            }
        }
    }

    private void AddCodexDeliveryHistory(string entry)
    {
        CodexDeliveryHistory.Add(entry);
        if (CodexDeliveryHistory.Count > 200)
        {
            CodexDeliveryHistory.RemoveRange(0, CodexDeliveryHistory.Count - 200);
        }
    }
}

public sealed class CompletedReportRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string GitHubBlobUrl { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset LastWakePromptBuiltAtUtc { get; set; }
    public DateTimeOffset? LastWakeSentAtUtc { get; set; }
    public string LastWakeToken { get; set; } = string.Empty;
    public int WakeCount { get; set; }
}

public sealed class CapturedTaskRecord
{
    public string TaskId { get; set; } = string.Empty;
    public string SourceReport { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string EnvelopePath { get; set; } = string.Empty;
    public string InstructionPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

public sealed class ReportTaskStageRecord
{
    public string ReportPath { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset? WakeSentAtUtc { get; set; }
    public string CaptureStatus { get; set; } = string.Empty;
    public string CapturedTaskId { get; set; } = string.Empty;
}

public sealed class CodexSentTaskRecord
{
    public string TaskId { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; }
    public string DeliveryMode { get; set; } = string.Empty;
    public string HandoffPromptPath { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string SourceReport { get; set; } = string.Empty;
    public string WorkItem { get; set; } = string.Empty;
}
