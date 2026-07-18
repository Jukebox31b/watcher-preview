using System.Security.Cryptography;
using System.Text;

namespace DcsWatcherV2.Models;

public sealed record HumanConfirmationRecord
{
    public const string SchemaName = "DCS_WATCHER_HUMAN_CONFIRMATION_V1";

    public string Schema { get; init; } = SchemaName;
    public string Action { get; init; } = string.Empty;
    public string SourceReportFingerprint { get; init; } = string.Empty;
    public string SourceReportPath { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string PromptSha256 { get; init; } = string.Empty;
    public string WakeToken { get; init; } = string.Empty;
    public string DirectorIdentity { get; init; } = string.Empty;
    public string DestinationAdapterId { get; init; } = string.Empty;
    public string DestinationIdentity { get; init; } = string.Empty;
    public WatcherAutomationPolicyKind PolicyKind { get; init; }
    public int PolicyGeneration { get; init; }
    public bool RequireVisibleHumanApproval { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string Nonce { get; init; } = string.Empty;

    public static HumanConfirmationRecord Create(
        string action,
        ReportCandidate report,
        string profileId,
        string exactPrompt,
        DateTimeOffset issuedAtUtc,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Confirmation lifetime must be positive.");
        }

        return new HumanConfirmationRecord
        {
            Action = action,
            SourceReportFingerprint = report.Fingerprint,
            SourceReportPath = report.RelativePath,
            ProfileId = profileId,
            PromptSha256 = ComputePromptSha256(exactPrompt),
            IssuedAtUtc = issuedAtUtc.ToUniversalTime(),
            ExpiresAtUtc = issuedAtUtc.ToUniversalTime().Add(lifetime),
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
    }

    public static string ComputePromptSha256(string exactPrompt)
    {
        ArgumentNullException.ThrowIfNull(exactPrompt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactPrompt))).ToLowerInvariant();
    }

    public static HumanConfirmationRecord Create(PreparedHumanAction prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return new HumanConfirmationRecord
        {
            Action = prepared.Action,
            SourceReportFingerprint = prepared.Report.Fingerprint,
            SourceReportPath = prepared.Report.RelativePath,
            ProfileId = prepared.ProfileId,
            PromptSha256 = prepared.PromptSha256,
            WakeToken = prepared.WakeToken,
            DirectorIdentity = prepared.DirectorIdentity,
            DestinationAdapterId = prepared.DestinationAdapterId,
            DestinationIdentity = prepared.DestinationIdentity,
            PolicyKind = prepared.PolicyKind,
            PolicyGeneration = prepared.PolicyGeneration,
            RequireVisibleHumanApproval = prepared.RequireVisibleHumanApproval,
            IssuedAtUtc = prepared.IssuedAtUtc,
            ExpiresAtUtc = prepared.ExpiresAtUtc,
            Nonce = prepared.Nonce
        };
    }
}
