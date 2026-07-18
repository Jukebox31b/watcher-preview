using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class AuthenticatedSnapshotAcquisitionRecord
{
    public const string ObservedNetworkMethod = "cdp-observed-network-response";
    public const string InPageMethod = "in-page-authenticated-request";

    public string AcquisitionMethod { get; set; } = InPageMethod;
    public int MatchingTabCount { get; set; } = 1;
    public string TargetIdBefore { get; set; } = string.Empty;
    public string TargetIdAfter { get; set; } = string.Empty;
    public string FrameId { get; set; } = string.Empty;
    public string UrlBefore { get; set; } = string.Empty;
    public string UrlAfter { get; set; } = string.Empty;
    public string VisibilityBefore { get; set; } = "visible";
    public string VisibilityAfter { get; set; } = "visible";
    public string RequestMethod { get; set; } = "GET";
    public string EndpointPath { get; set; } = string.Empty;
    public string CredentialMode { get; set; } = "include";
    public List<string> HeaderNames { get; set; } = ["authorization"];
    public int SessionStatusCode { get; set; } = 200;
    public int ResponseStatusCode { get; set; } = 200;
    public string ResponseContentType { get; set; } = "application/json";
    public bool ResponseBodyAvailable { get; set; } = true;
    public bool ResponseMalformed { get; set; }
    public string CacheMode { get; set; } = "no-store";
    public bool CachedOnly { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public DateTimeOffset AbsoluteDeadlineUtc { get; set; }
    public DateTimeOffset CallerCompletedAtUtc { get; set; }
}

public static class ChatGptAuthenticatedSnapshotService
{
    public const string EndpointPrefix = "/backend-api/conversation/";
    public static readonly TimeSpan AbsoluteDeadline = TimeSpan.FromSeconds(30);

    public static PreWakeSnapshotGateResult ValidateAcquisition(
        AuthenticatedSnapshotAcquisitionRecord record,
        string expectedConversationId)
    {
        if (record.MatchingTabCount != 1)
            return Fail("AMBIGUOUS_CONVERSATION_TABS", $"Expected exactly one matching conversation tab; found {record.MatchingTabCount}.");
        if (!record.VisibilityBefore.Equals("visible", StringComparison.OrdinalIgnoreCase))
            return Fail("ACTIVE_CONVERSATION_NOT_VISIBLE", $"The intended ChatGPT conversation tab is {record.VisibilityBefore} before acquisition.");
        if (!record.VisibilityAfter.Equals("visible", StringComparison.OrdinalIgnoreCase))
            return Fail("TAB_BECAME_HIDDEN_DURING_ACQUISITION", $"The intended ChatGPT conversation tab became {record.VisibilityAfter} during acquisition.");
        if (string.IsNullOrWhiteSpace(record.TargetIdBefore) ||
            !record.TargetIdBefore.Equals(record.TargetIdAfter, StringComparison.Ordinal))
            return Fail("CONVERSATION_TARGET_CHANGED", "The CDP target changed during authenticated snapshot acquisition.");
        if (string.IsNullOrWhiteSpace(record.FrameId) ||
            !record.FrameId.Equals(record.TargetIdBefore, StringComparison.Ordinal))
            return Fail("CONVERSATION_FRAME_MISMATCH", "The authenticated snapshot did not originate from the selected main conversation frame.");
        if (!record.UrlBefore.Equals(record.UrlAfter, StringComparison.Ordinal))
            return Fail("NAVIGATION_DURING_ACQUISITION", "The selected ChatGPT tab navigated during authenticated snapshot acquisition.");
        var before = ChatGptUrlMatcher.Parse(record.UrlBefore);
        var after = ChatGptUrlMatcher.Parse(record.UrlAfter);
        if (!before.ConversationId.Equals(expectedConversationId, StringComparison.OrdinalIgnoreCase) ||
            !after.ConversationId.Equals(expectedConversationId, StringComparison.OrdinalIgnoreCase))
            return Fail("CONVERSATION_ID_MISMATCH", "The authenticated acquisition tab URL belongs to a different conversation.");
        if (!record.RequestMethod.Equals("GET", StringComparison.Ordinal))
            return Fail("AUTHENTICATED_REQUEST_METHOD_MISMATCH", "The observed conversation request was not GET.");
        var expectedPath = EndpointPrefix + expectedConversationId;
        if (!record.EndpointPath.Equals(expectedPath, StringComparison.Ordinal))
            return Fail("AUTHENTICATED_ENDPOINT_MISMATCH", "The authenticated response came from an unexpected endpoint.");
        if (!record.CredentialMode.Equals("include", StringComparison.Ordinal))
            return Fail("AUTHENTICATED_CREDENTIAL_MODE_MISMATCH", "The in-page request did not use browser-managed included credentials.");
        if (!record.HeaderNames.Contains("authorization", StringComparer.OrdinalIgnoreCase))
            return Fail("AUTHENTICATED_REQUIRED_HEADER_MISSING", "The evidence-backed authorization header was not applied inside the authenticated tab.");
        if (record.SessionStatusCode is < 200 or >= 300)
            return Fail($"AUTH_SESSION_HTTP_{record.SessionStatusCode}", $"The browser-managed authentication session returned HTTP {record.SessionStatusCode}.");
        if (record.ResponseStatusCode == 401)
            return Fail("CONVERSATION_BACKEND_HTTP_401", "The authenticated conversation request returned HTTP 401.");
        if (record.ResponseStatusCode == 403)
            return Fail("CONVERSATION_BACKEND_HTTP_403", "The authenticated conversation request returned HTTP 403.");
        if (record.ResponseStatusCode is < 200 or >= 300)
            return Fail("CONVERSATION_BACKEND_HTTP_ERROR", $"The authenticated conversation request returned HTTP {record.ResponseStatusCode}.");
        if (!record.ResponseContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return Fail("CONVERSATION_BACKEND_CONTENT_TYPE_INVALID", "The authenticated conversation response was not JSON.");
        if (!record.ResponseBodyAvailable)
            return Fail("CONVERSATION_BACKEND_BODY_UNAVAILABLE", "The authenticated conversation response body was unavailable.");
        if (record.ResponseMalformed)
            return Fail("CONVERSATION_BACKEND_RESPONSE_MALFORMED", "The authenticated conversation response was malformed.");
        if (!record.CacheMode.Equals("no-store", StringComparison.Ordinal))
            return Fail("CONVERSATION_BACKEND_CACHE_MODE_INVALID", "The authenticated conversation request did not prohibit cache reuse.");
        if (record.CachedOnly)
            return Fail("CONVERSATION_BACKEND_CACHED_ONLY", "The lineage response was available only from cache.");
        if (record.StartedAtUtc == default || record.CompletedAtUtc == default || record.AbsoluteDeadlineUtc == default ||
            record.CompletedAtUtc < record.StartedAtUtc || record.CompletedAtUtc > record.AbsoluteDeadlineUtc ||
            record.CompletedAtUtc - record.StartedAtUtc > AbsoluteDeadline)
            return Fail("AUTHENTICATED_REQUEST_DEADLINE_EXCEEDED", "The authenticated browser request exceeded the single absolute 30-second deadline.");
        if (record.CallerCompletedAtUtc == default || record.CallerCompletedAtUtc > record.AbsoluteDeadlineUtc)
            return Fail("AUTHENTICATED_SNAPSHOT_DEADLINE_EXCEEDED", "The CDP caller exceeded the same absolute 30-second deadline.");
        if (record.AcquisitionMethod is not (AuthenticatedSnapshotAcquisitionRecord.ObservedNetworkMethod or AuthenticatedSnapshotAcquisitionRecord.InPageMethod))
            return Fail("AUTHENTICATED_ACQUISITION_METHOD_INVALID", "The lineage acquisition method is not evidence-backed.");
        return new PreWakeSnapshotGateResult(true, "OK", "Authenticated lineage acquisition is structurally verified.");
    }

    public static string BuildRedactedDiagnostics(AuthenticatedSnapshotAcquisitionRecord record) =>
        $"method={record.AcquisitionMethod} endpoint={record.EndpointPath} request={record.RequestMethod} " +
        $"headers={string.Join(',', record.HeaderNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))} " +
        $"sessionStatus={record.SessionStatusCode} responseStatus={record.ResponseStatusCode} " +
        $"contentType={record.ResponseContentType} cache={record.CacheMode}";

    private static PreWakeSnapshotGateResult Fail(string code, string message) => new(false, code, message);
}
