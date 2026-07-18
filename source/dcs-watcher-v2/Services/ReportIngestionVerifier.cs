using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class ReportIngestionVerifier
{
    public ReportIngestionRecord VerifyStage4Bootstrap(AppConfig config, AppState state, ReportCandidate candidate, DateTimeOffset discoveredAtUtc)
    {
        var record = new ReportIngestionRecord
        {
            Repository = candidate.Repository,
            Branch = candidate.Branch,
            ReportPath = candidate.RelativePath,
            ReportTaskId = candidate.ReportTaskId,
            SourceReport = candidate.SourceReport,
            ReportCommit = candidate.Commit,
            ReportBlobIdentity = candidate.BlobIdentity,
            ReportSha256 = candidate.Fingerprint,
            DiscoveryTimeUtc = discoveredAtUtc,
            VerificationTimeUtc = DateTimeOffset.UtcNow
        };
        if (state.Stage4BootstrapConsumed)
            record.RejectionReason = "The one-time Stage 4 bootstrap report was already consumed.";
        else if (string.IsNullOrWhiteSpace(config.Stage4BootstrapReportPath) || string.IsNullOrWhiteSpace(config.Stage4BootstrapReportSha256))
            record.RejectionReason = "Stage 4 bootstrap path and SHA-256 are not configured.";
        else if (!candidate.RelativePath.Equals(config.Stage4BootstrapReportPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                 !candidate.Fingerprint.Equals(config.Stage4BootstrapReportSha256, StringComparison.OrdinalIgnoreCase))
            record.RejectionReason = "Report does not match the explicitly authorized Stage 4 bootstrap path and SHA-256.";
        else if (!candidate.Repository.Equals(config.ReportRepoFullName, StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(config.ReportBranch) ||
                 !candidate.Branch.Equals(config.ReportBranch, StringComparison.OrdinalIgnoreCase) ||
                 !Regex.IsMatch(candidate.Commit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase) ||
                 !Regex.IsMatch(candidate.BlobIdentity, "^[0-9a-f]{40,64}$", RegexOptions.IgnoreCase))
            record.RejectionReason = $"Bootstrap report is not authenticated on configured branch '{config.ReportBranch}'.";
        else if (candidate.ContentBytes.Length == 0 ||
                 !Convert.ToHexString(SHA256.HashData(candidate.ContentBytes)).Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
            record.RejectionReason = "Bootstrap report bytes do not match the authorized SHA-256.";
        else if (!candidate.IsTerminal)
            record.RejectionReason = "Bootstrap report is not terminal.";
        else
            record.Eligible = true;
        return record;
    }

    public ReportIngestionRecord Verify(AppConfig config, AppState state, ReportCandidate candidate, DateTimeOffset discoveredAtUtc)
    {
        var record = new ReportIngestionRecord
        {
            Repository = candidate.Repository,
            Branch = candidate.Branch,
            ReportPath = candidate.RelativePath,
            ReportTaskId = candidate.ReportTaskId,
            ActiveTaskId = state.ActiveTaskLock.ActiveTaskId,
            SourceReport = candidate.SourceReport,
            ReportCommit = candidate.Commit,
            ReportBlobIdentity = candidate.BlobIdentity,
            ReportSha256 = candidate.Fingerprint,
            DiscoveryTimeUtc = discoveredAtUtc,
            VerificationTimeUtc = DateTimeOffset.UtcNow,
            Duplicate = state.ConsumedReportSha256.Contains(candidate.Fingerprint, StringComparer.OrdinalIgnoreCase)
        };

        record.RejectionReason = FindRejection(config, state, candidate, record.Duplicate);
        record.Eligible = string.IsNullOrWhiteSpace(record.RejectionReason);
        return record;
    }

    public bool TryCloseActiveTask(AppState state, ReportIngestionRecord record)
    {
        if (!record.Eligible || !state.ActiveTaskLock.IsActive)
        {
            return false;
        }

        state.ActiveTaskLock.IsActive = false;
        state.ActiveTaskLock.TaskStatus = "completed_verified";
        state.ActiveTaskLock.CompletionReportCommit = record.ReportCommit;
        state.ActiveTaskLock.CompletionReportSha256 = record.ReportSha256;
        if (!state.ConsumedReportSha256.Contains(record.ReportSha256, StringComparer.OrdinalIgnoreCase))
        {
            state.ConsumedReportSha256.Add(record.ReportSha256);
        }

        return true;
    }

    private static string FindRejection(AppConfig config, AppState state, ReportCandidate candidate, bool duplicate)
    {
        var expectedFolder = config.ReportFolder.Replace('\\', '/').Trim('/');
        if (!candidate.RelativePath.Replace('\\', '/').StartsWith(expectedFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "Report is outside the configured canonical reports folder.";
        }

        if (!candidate.Repository.Equals(config.ReportRepoFullName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(config.ReportBranch) ||
            !candidate.Branch.Equals(config.ReportBranch, StringComparison.OrdinalIgnoreCase))
        {
            return $"Report is not authenticated on configured branch '{config.ReportBranch}'.";
        }

        if (!Regex.IsMatch(candidate.Commit, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(candidate.BlobIdentity, "^[0-9a-f]{40,64}$", RegexOptions.IgnoreCase))
        {
            return "Report commit or remote blob identity is missing or invalid.";
        }

        if (candidate.ContentBytes.Length == 0 ||
            !Convert.ToHexString(SHA256.HashData(candidate.ContentBytes)).Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return "Fetched remote blob bytes do not match the report SHA-256.";
        }

        if (duplicate)
        {
            return "Report SHA-256 was already consumed.";
        }

        if (!state.ActiveTaskLock.IsActive)
        {
            return "No durable active-task authorization exists for this report.";
        }

        if (!SameWorkItem(candidate.ReportTaskId, state.ActiveTaskLock.ActiveTaskId))
        {
            return "Report task ID does not match the active authorized task.";
        }

        if (!string.IsNullOrWhiteSpace(candidate.SourceReport) &&
            !candidate.SourceReport.Equals(state.ActiveTaskLock.SourceReport, StringComparison.OrdinalIgnoreCase))
        {
            return "Report source-report chain does not match the active task.";
        }

        if (state.ActiveTaskLock.DeliveryTimestampUtc is null ||
            new DateTimeOffset(candidate.LastWriteTimeUtc, TimeSpan.Zero) <= state.ActiveTaskLock.DeliveryTimestampUtc.Value)
        {
            return "Report is not newer than the active task authorization.";
        }

        if (!candidate.IsTerminal)
        {
            return "Report is not a verified terminal report.";
        }

        if (!string.IsNullOrWhiteSpace(state.ActiveTaskLock.TerminalReportExpectedPath) &&
            !candidate.RelativePath.Equals(state.ActiveTaskLock.TerminalReportExpectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return "Report path does not match the active task terminal-report expectation.";
        }

        return string.Empty;
    }

    internal static bool SameWorkItem(string left, string right)
    {
        var leftWorkItem = WorkItemIdParser.Parse(left);
        var rightWorkItem = WorkItemIdParser.Parse(right);
        return leftWorkItem is not null && rightWorkItem is not null &&
               WorkItemIdParser.Compare(leftWorkItem, rightWorkItem) == 0;
    }
}

public sealed class ActiveTaskLockService
{
    public SafetyValidationResult TryActivate(
        AppState state,
        string taskId,
        string instructionSha256,
        string sourceReport,
        string directorThreadId,
        string terminalReportExpectedPath,
        DateTimeOffset deliveredAtUtc)
    {
        if (state.ActiveTaskLock.IsActive)
        {
            return new SafetyValidationResult(false, false, $"Active-task lock is already held by {state.ActiveTaskLock.ActiveTaskId}.");
        }

        if (new[] { taskId, instructionSha256, sourceReport, directorThreadId }.Any(string.IsNullOrWhiteSpace))
        {
            return new SafetyValidationResult(false, false, "Active-task lock requires task, hash, source report, and destination.");
        }

        state.ActiveTaskLock = new ActiveTaskLockRecord
        {
            IsActive = true,
            ActiveTaskId = taskId,
            ActiveInstructionSha256 = instructionSha256,
            SourceReport = sourceReport,
            DeliveryTimestampUtc = deliveredAtUtc,
            DirectorThreadId = directorThreadId,
            TerminalReportExpectedPath = terminalReportExpectedPath,
            TaskStatus = "active"
        };
        return new SafetyValidationResult(true, false, "Active-task lock acquired.");
    }
}

public sealed class TransactionReplayGuardService
{
    public SafetyValidationResult TryReserveWakeToken(AppState state, string wakeToken)
    {
        if (string.IsNullOrWhiteSpace(wakeToken))
        {
            return new SafetyValidationResult(false, false, "Wake token is required.");
        }

        if (state.UsedWakeTokens.Contains(wakeToken, StringComparer.Ordinal))
        {
            return new SafetyValidationResult(false, false, "Duplicate wake token rejected.");
        }

        state.UsedWakeTokens.Add(wakeToken);
        return new SafetyValidationResult(true, false, "Wake token reserved.");
    }

    public SafetyValidationResult TryRecordManualDelivery(AppState state, string envelopeSha256)
    {
        if (string.IsNullOrWhiteSpace(envelopeSha256))
        {
            return new SafetyValidationResult(false, false, "Envelope SHA-256 is required.");
        }

        if (state.RecordedManualDeliveryHashes.Contains(envelopeSha256, StringComparer.OrdinalIgnoreCase))
        {
            return new SafetyValidationResult(false, false, "Duplicate instruction delivery rejected.");
        }

        state.RecordedManualDeliveryHashes.Add(envelopeSha256);
        return new SafetyValidationResult(true, false, "Manual delivery receipt recorded.");
    }
}
