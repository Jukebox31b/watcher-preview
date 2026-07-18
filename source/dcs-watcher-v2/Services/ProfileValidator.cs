using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ProfileValidationIssue(string Code, string Path, string Message);

public sealed record ProfileValidationResult(IReadOnlyList<ProfileValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class ProfileValidator
{
    private static readonly string[] SecretKeyFragments =
    [
        "password", "passwd", "privatekey", "private_key", "secret", "api_key", "apikey",
        "token", "bearer", "cookie", "credential"
    ];

    private static readonly string[] SecretValueMarkers =
    [
        "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", "-----BEGIN EC PRIVATE KEY-----",
        "Bearer ", "sk-proj-", "ghp_", "github_pat_"
    ];

    public ProfileValidationResult Validate(WatcherProfileV1 profile)
    {
        var issues = new List<ProfileValidationIssue>();
        if (!string.Equals(profile.Schema, WatcherProfileV1.SchemaName, StringComparison.Ordinal))
        {
            Add("PROFILE_SCHEMA_INVALID", "Schema", "Profile schema must be DCS_WATCHER_PROFILE_V1.");
        }
        if (profile.Version != 1) Add("PROFILE_VERSION_UNSUPPORTED", "Version", "Only profile version 1 is supported.");
        if (string.IsNullOrWhiteSpace(profile.Identity.ProfileId)) Add("PROFILE_ID_MISSING", "Identity.ProfileId", "Profile ID is required.");
        if (string.IsNullOrWhiteSpace(profile.Identity.Name)) Add("PROFILE_NAME_MISSING", "Identity.Name", "Profile name is required.");

        CheckAdapter(profile.ReportSource.Adapter, WatcherAdapterIds.ReportSources, "ReportSource.Adapter");
        CheckAdapter(profile.Director.Adapter, WatcherAdapterIds.Directors, "Director.Adapter");
        CheckAdapter(profile.Destination.Adapter, WatcherAdapterIds.Deliveries, "Destination.Adapter");

        if (!profile.Director.RequireDirectParent || !profile.Director.RequireCurrentPath ||
            !profile.Director.RequireBackendMessageObject || profile.Director.AllowFallbackBody ||
            profile.Director.AllowWholePageCapture)
        {
            Add("UNSAFE_CAPTURE_CONFIGURATION", "Director", "Actionable capture requires exact parentage, current-path membership, and one backend message object; fallback and whole-page capture are forbidden.");
        }

        if (!string.Equals(profile.Destination.Adapter.AdapterId, WatcherAdapterIds.DeliveryTestSink, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(profile.Destination.DestinationIdentity))
        {
            Add("DESTINATION_IDENTITY_MISSING", "Destination.DestinationIdentity", "The selected delivery adapter requires an explicit destination identity.");
        }

        if (profile.Destination.Adapter.AdapterId is WatcherAdapterIds.DeliveryUiPasteFallback or WatcherAdapterIds.DeliveryManualVisiblePaste &&
            (profile.AutomationPolicy.Kind != WatcherAutomationPolicyKind.ManualApproval ||
             !profile.AutomationPolicy.RequireVisibleHumanApproval))
        {
            Add("VISIBLE_PASTE_REQUIRES_MANUAL_APPROVAL", "Destination.Adapter.AdapterId", "Visible paste delivery is permitted only with visible manual approval.");
        }

        var usesDemoFixture = profile.ReportSource.Adapter.AdapterId == WatcherAdapterIds.ReportDemoFixture ||
                              profile.Director.Adapter.AdapterId == WatcherAdapterIds.DirectorDemoFixture;
        if (usesDemoFixture && (profile.Enabled || profile.Destination.Adapter.AdapterId != WatcherAdapterIds.DeliveryTestSink))
        {
            Add("DEMO_FIXTURE_REQUIRES_DISABLED_TEST_SINK", "$", "Demo fixtures require a disabled profile whose destination is the test sink.");
        }

        if (profile.Protocol.MaximumEnvelopeBytes is < 100 or > 1_000_000)
        {
            Add("ENVELOPE_LIMIT_INVALID", "Protocol.MaximumEnvelopeBytes", "Maximum envelope bytes must be between 100 and 1,000,000.");
        }
        if (string.IsNullOrWhiteSpace(profile.Protocol.EnvelopeSchema)) Add("ENVELOPE_SCHEMA_MISSING", "Protocol.EnvelopeSchema", "Envelope schema is required.");
        if (string.IsNullOrWhiteSpace(profile.Protocol.ReportFilePattern)) Add("REPORT_PATTERN_MISSING", "Protocol.ReportFilePattern", "Report file pattern is required.");
        if (string.IsNullOrWhiteSpace(profile.Protocol.TaskIdPattern)) Add("TASK_PATTERN_MISSING", "Protocol.TaskIdPattern", "Task ID pattern is required.");

        if (profile.AutomationPolicy.Kind.IsAutomatic())
        {
            if (profile.Director.Adapter.AdapterId is WatcherAdapterIds.DirectorManualEnvelope or WatcherAdapterIds.DirectorHashBoundFile)
            {
                Add("MANUAL_DIRECTOR_CANNOT_AUTOMATE", "Director.Adapter.AdapterId", "Manual envelope and hash-bound file intake cannot manufacture automatic provenance.");
            }
            if (profile.Guardrails.MaximumTasksPerRun <= 0) Add("AUTOMATIC_TASK_LIMIT_MISSING", "Guardrails.MaximumTasksPerRun", "Automatic policies require a positive task limit.");
            if (profile.Guardrails.MaximumElapsedMinutes <= 0) Add("AUTOMATIC_TIME_LIMIT_MISSING", "Guardrails.MaximumElapsedMinutes", "Automatic policies require a positive elapsed-time limit.");
            if (profile.Guardrails.SummaryIntervalMinutes <= 0 ||
                profile.Guardrails.SummaryIntervalMinutes > profile.Guardrails.MaximumElapsedMinutes)
            {
                Add("AUTOMATIC_SUMMARY_LIMIT_INVALID", "Guardrails.SummaryIntervalMinutes", "Automatic policies require a summary interval within the elapsed-time limit.");
            }
            if (!profile.Guardrails.StopOnFailure) Add("AUTOMATIC_STOP_ON_FAILURE_REQUIRED", "Guardrails.StopOnFailure", "Automatic policies must stop on failure.");
            if (!profile.Guardrails.StopOnBranchDivergence) Add("AUTOMATIC_DIVERGENCE_STOP_REQUIRED", "Guardrails.StopOnBranchDivergence", "Automatic policies must stop on branch divergence.");
            if (string.IsNullOrWhiteSpace(profile.SecurityBinding.SignerKeyId) ||
                string.IsNullOrWhiteSpace(profile.SecurityBinding.PublicKeyFingerprintSha256))
            {
                Add("AUTOMATIC_SECURITY_BINDING_MISSING", "SecurityBinding", "Automatic policies require a public signer identity and key fingerprint.");
            }
        }

        ScanSettings(profile.ReportSource.Adapter.Settings, "ReportSource.Adapter.Settings");
        ScanSettings(profile.Director.Adapter.Settings, "Director.Adapter.Settings");
        ScanSettings(profile.Destination.Adapter.Settings, "Destination.Adapter.Settings");

        var profileJson = JsonSerializer.Serialize(profile);
        foreach (var marker in SecretValueMarkers.Where(marker => profileJson.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            Add("PRIVATE_SECRET_MATERIAL_FORBIDDEN", "$", $"Profile contains forbidden private-secret marker '{marker}'.");
        }

        return new ProfileValidationResult(issues);

        void CheckAdapter(AdapterConfigurationV1 adapter, IReadOnlySet<string> supported, string path)
        {
            if (!supported.Contains(adapter.AdapterId))
            {
                Add("UNKNOWN_ADAPTER", path + ".AdapterId", $"Unknown or unsupported adapter '{adapter.AdapterId}'.");
            }
        }

        void ScanSettings(SortedDictionary<string, string> settings, string path)
        {
            foreach (var pair in settings)
            {
                var normalized = pair.Key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
                if (SecretKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
                {
                    Add("PRIVATE_SECRET_FIELD_FORBIDDEN", path + "." + pair.Key, "Profiles may contain public trust references, but not credentials or private secrets.");
                }
                if (SecretValueMarkers.Any(marker => pair.Value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    Add("PRIVATE_SECRET_MATERIAL_FORBIDDEN", path + "." + pair.Key, "Profiles may not contain reusable secret material.");
                }
            }
        }

        void Add(string code, string path, string message) => issues.Add(new ProfileValidationIssue(code, path, message));
    }
}
