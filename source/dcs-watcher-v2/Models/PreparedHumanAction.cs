using System.Security.Cryptography;
using System.Text.Json;

namespace DcsWatcherV2.Models;

public sealed record PreparedHumanAction
{
    public const string SchemaName = "DCS_WATCHER_PREPARED_HUMAN_ACTION_V1";

    public string Schema { get; init; } = SchemaName;
    public string Action { get; init; } = string.Empty;
    public ReportCandidate Report { get; init; } = null!;
    public string Prompt { get; init; } = string.Empty;
    public string PromptSha256 { get; init; } = string.Empty;
    public string WakeToken { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string DirectorIdentity { get; init; } = string.Empty;
    public string DestinationAdapterId { get; init; } = string.Empty;
    public string DestinationIdentity { get; init; } = string.Empty;
    public WatcherAutomationPolicyKind PolicyKind { get; init; }
    public int PolicyGeneration { get; init; }
    public bool RequireVisibleHumanApproval { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public string IntegritySha256 { get; init; } = string.Empty;

    public string PolicyDisplay =>
        $"{PolicyKind}; generation={PolicyGeneration}; visible-approval={RequireVisibleHumanApproval}";

    public string DestinationDisplay =>
        string.IsNullOrWhiteSpace(DestinationIdentity)
            ? DestinationAdapterId
            : $"{DestinationAdapterId}: {DestinationIdentity}";

    public static PreparedHumanAction Create(
        string action,
        ReportCandidate report,
        string exactPrompt,
        string wakeToken,
        WatcherProfileV1 profile,
        DateTimeOffset issuedAtUtc,
        TimeSpan lifetime,
        string? runtimeDirectorIdentity = null,
        string? runtimeDestinationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(wakeToken);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Prepared action lifetime must be between zero and 15 minutes.");
        }

        var prepared = new PreparedHumanAction
        {
            Action = action,
            Report = FreezeReport(report),
            Prompt = exactPrompt,
            PromptSha256 = HumanConfirmationRecord.ComputePromptSha256(exactPrompt),
            WakeToken = wakeToken,
            ProfileId = profile.Identity.ProfileId,
            DirectorIdentity = runtimeDirectorIdentity ?? ResolveDirectorIdentity(profile),
            DestinationAdapterId = profile.Destination.Adapter.AdapterId,
            DestinationIdentity = runtimeDestinationIdentity ?? profile.Destination.DestinationIdentity,
            PolicyKind = profile.AutomationPolicy.Kind,
            PolicyGeneration = profile.AutomationPolicy.PolicyGeneration,
            RequireVisibleHumanApproval = profile.AutomationPolicy.RequireVisibleHumanApproval,
            IssuedAtUtc = issuedAtUtc.ToUniversalTime(),
            ExpiresAtUtc = issuedAtUtc.ToUniversalTime().Add(lifetime),
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
        return prepared with { IntegritySha256 = prepared.ComputeIntegritySha256() };
    }

    public HumanConfirmationRecord CreateConfirmationRecord() => HumanConfirmationRecord.Create(this);

    public bool HasValidIntegrity() =>
        Schema.Equals(SchemaName, StringComparison.Ordinal) &&
        Report is not null &&
        PromptSha256.Equals(HumanConfirmationRecord.ComputePromptSha256(Prompt), StringComparison.OrdinalIgnoreCase) &&
        IntegritySha256.Equals(ComputeIntegritySha256(), StringComparison.OrdinalIgnoreCase);

    public bool MatchesProfile(WatcherProfileV1 profile) =>
        profile.Identity.ProfileId.Equals(ProfileId, StringComparison.Ordinal) &&
        ResolveDirectorIdentity(profile).Equals(DirectorIdentity, StringComparison.Ordinal) &&
        profile.Destination.Adapter.AdapterId.Equals(DestinationAdapterId, StringComparison.Ordinal) &&
        profile.Destination.DestinationIdentity.Equals(DestinationIdentity, StringComparison.Ordinal) &&
        profile.AutomationPolicy.Kind == PolicyKind &&
        profile.AutomationPolicy.PolicyGeneration == PolicyGeneration &&
        profile.AutomationPolicy.RequireVisibleHumanApproval == RequireVisibleHumanApproval;

    private static string ResolveDirectorIdentity(WatcherProfileV1 profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Director.ConversationIdentity))
        {
            return profile.Director.ConversationIdentity;
        }
        return profile.Director.Adapter.Settings.TryGetValue("conversation_url", out var configured)
            ? configured
            : string.Empty;
    }

    private string ComputeIntegritySha256()
    {
        var report = Report;
        var payload = new
        {
            Schema,
            Action,
            Report = report is null ? null : new
            {
                report.RelativePath,
                report.FullPath,
                report.FileName,
                report.Fingerprint,
                report.LastWriteTimeUtc,
                report.GitHubBlobUrl,
                report.SortTimestampUtc,
                report.Repository,
                report.Branch,
                report.Commit,
                report.BlobIdentity,
                report.ContentBytes,
                report.ReportTaskId,
                report.SourceReport,
                report.IsTerminal
            },
            Prompt,
            PromptSha256,
            WakeToken,
            ProfileId,
            DirectorIdentity,
            DestinationAdapterId,
            DestinationIdentity,
            PolicyKind,
            PolicyGeneration,
            RequireVisibleHumanApproval,
            IssuedAtUtc,
            ExpiresAtUtc,
            Nonce
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload))).ToLowerInvariant();
    }

    private static ReportCandidate FreezeReport(ReportCandidate report) => new(
        report.RelativePath,
        report.FullPath,
        report.FileName,
        report.Fingerprint,
        report.LastWriteTimeUtc,
        report.GitHubBlobUrl,
        report.SortTimestampUtc)
    {
        Repository = report.Repository,
        Branch = report.Branch,
        Commit = report.Commit,
        BlobIdentity = report.BlobIdentity,
        ContentBytes = [.. report.ContentBytes],
        ReportTaskId = report.ReportTaskId,
        SourceReport = report.SourceReport,
        IsTerminal = report.IsTerminal
    };
}

public sealed record PreparedHumanActionResult(bool Prepared, PreparedHumanAction? Action, string Message);

internal sealed class PreparedHumanActionCoordinator
{
    private readonly object _gate = new();
    private string? _activeNonce;
    private bool _executing;

    public bool TryPrepare(string nonce, out string reason)
    {
        lock (_gate)
        {
            if (_activeNonce is not null)
            {
                reason = "HUMAN_ACTION_CONCURRENT: another prepared action is awaiting a decision.";
                return false;
            }
            _activeNonce = nonce;
            _executing = false;
            reason = "Prepared action reserved for one human decision.";
            return true;
        }
    }

    public bool TryBegin(string nonce, out string reason)
    {
        lock (_gate)
        {
            if (_activeNonce is null || !_activeNonce.Equals(nonce, StringComparison.Ordinal))
            {
                reason = "HUMAN_ACTION_NOT_ACTIVE: the prepared action is no longer active.";
                return false;
            }
            if (_executing)
            {
                reason = "HUMAN_ACTION_CONCURRENT: this prepared action is already executing.";
                return false;
            }
            _executing = true;
            reason = "Prepared action execution lease acquired.";
            return true;
        }
    }

    public bool Cancel(string nonce)
    {
        lock (_gate)
        {
            if (_executing || _activeNonce is null || !_activeNonce.Equals(nonce, StringComparison.Ordinal))
            {
                return false;
            }
            _activeNonce = null;
            return true;
        }
    }

    public void Complete(string nonce)
    {
        lock (_gate)
        {
            if (_activeNonce?.Equals(nonce, StringComparison.Ordinal) == true)
            {
                _activeNonce = null;
                _executing = false;
            }
        }
    }
}
