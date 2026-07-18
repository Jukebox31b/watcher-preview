using System.Text.Json;
using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex SafeProfileId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly string _applicationRoot;

    public ConfigService(string? applicationRoot = null)
    {
        _applicationRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(applicationRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watcher")
                : applicationRoot));
    }

    public string ApplicationRoot => _applicationRoot;

    public AppConfig Load()
    {
        var canonicalPath = GetGlobalConfigPath();
        if (File.Exists(canonicalPath))
        {
            var loaded = Deserialize(canonicalPath);
            ApplySafeCompatibilityMigrations(loaded);
            return loaded;
        }

        var legacyPath = FindLegacyConfigPath();
        if (legacyPath is not null)
        {
            var legacy = Deserialize(legacyPath);
            ApplySafeCompatibilityMigrations(legacy);
            legacy.LegacyEvidenceRoot = Path.GetDirectoryName(legacyPath) ?? string.Empty;
            EnsureLegacyProfileImported(legacy);

            var portable = CreatePortableDefaults();
            portable.LegacyEvidenceRoot = legacy.LegacyEvidenceRoot;
            Save(portable);
            return portable;
        }

        return CreatePortableDefaults();
    }

    public AppConfig CreatePortableDefaults() => new()
    {
        InstallationRoot = _applicationRoot,
        InstallationSecurityRoot = Path.Combine(_applicationRoot, "security"),
        ProfileDirectory = Path.Combine(_applicationRoot, "profiles"),
        WatcherPreferencesPath = Path.Combine(_applicationRoot, "preferences.json"),
        OperatingStage = nameof(WatcherOperatingStage.Stage1DetectOnly),
        InstructionDeliveryMode = nameof(AuthorizedInstructionDeliveryMode.HashBoundFile),
        ReportPollMode = "LocalFolder",
        ChatGptCaptureScope = "BackendMessageObject",
        RequireHumanWakeConfirmation = true,
        RequireSingleTaskEnvelope = true,
        CaptureNewestEnvelopeOnly = true,
        Stage4StopOnFailure = true
    };

    public void Save(AppConfig config)
    {
        var path = GetConfigPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions));
    }

    public string GetLedgerRoot(AppConfig config)
    {
        if (config.RuntimeComposedFromProfile)
        {
            if (!config.ActiveProfileId.Equals(config.RuntimeProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException("Active and runtime profile identities do not match.");
            return GetProfileRuntimeRoot(config.RuntimeProfileId);
        }

        if (Path.IsPathFullyQualified(config.LedgerRoot))
        {
            var testRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.LedgerRoot));
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (testRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return testRoot;
        }

        throw new InvalidOperationException("Runtime storage requires a validated profile composition.");
    }

    public string GetConfigPath(AppConfig config) => config.RuntimeComposedFromProfile
        ? Path.Combine(GetProfileRuntimeRoot(config.RuntimeProfileId), "runtime-config.json")
        : GetGlobalConfigPath();

    public string GetGlobalConfigPath() => Path.Combine(_applicationRoot, "config.json");

    public string GetProfileDirectory(AppConfig config) => Path.Combine(_applicationRoot, "profiles");

    public string GetPreferencesPath(AppConfig config) => Path.Combine(_applicationRoot, "preferences.json");

    public string GetProfileRuntimeRoot(string profileId)
    {
        ValidateProfileId(profileId);
        var profilesRoot = Path.GetFullPath(Path.Combine(_applicationRoot, "profiles"));
        var candidate = Path.GetFullPath(Path.Combine(profilesRoot, profileId));
        var prefix = profilesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Profile runtime path escaped the Watcher application-data root.");
        }
        return candidate;
    }

    public WatcherPreferences LoadPreferences(AppConfig config)
    {
        var path = GetPreferencesPath(config);
        if (!File.Exists(path)) return new WatcherPreferences { LastSelectedProfileId = config.ActiveProfileId };
        return JsonSerializer.Deserialize<WatcherPreferences>(File.ReadAllText(path), JsonOptions) ?? new WatcherPreferences();
    }

    public void SavePreferences(AppConfig config, WatcherPreferences preferences)
    {
        var path = GetPreferencesPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions));
    }

    public WatcherProfileV1? EnsureLegacyProfileImported(AppConfig legacy)
    {
        var profiles = new ProfileService(GetProfileDirectory(legacy));
        const string importedId = "imported-configuration";
        if (profiles.ListProfileIds().Contains(importedId, StringComparer.Ordinal))
        {
            return profiles.Load(importedId);
        }

        var imported = profiles.CreateImportedLegacyProfile(legacy);
        if (imported.ReportSource.Adapter.AdapterId == WatcherAdapterIds.ReportDemoFixture)
        {
            imported.ReportSource.Adapter.AdapterId = WatcherAdapterIds.ReportLocalFolder;
        }
        imported.Enabled = false;
        imported.AutomationPolicy.Kind = WatcherAutomationPolicyKind.ManualApproval;
        imported.AutomationPolicy.RequireVisibleHumanApproval = true;
        imported.AutomationPolicy.PolicyGeneration = 0;
        imported.Guardrails.MaximumTasksPerRun = 0;
        imported.Guardrails.MaximumElapsedMinutes = 0;
        imported.Guardrails.SummaryIntervalMinutes = 0;
        profiles.Save(imported, overwrite: false);
        return imported;
    }

    internal static void ForceInactive(AppConfig config)
    {
        config.ActiveProfileId = string.Empty;
        config.RuntimeProfileId = string.Empty;
        config.RuntimeProfileRoot = string.Empty;
        config.ProfileConfigurationSha256 = string.Empty;
        config.RuntimeComposedFromProfile = false;
        config.OperatingStage = nameof(WatcherOperatingStage.Stage1DetectOnly);
        config.StartWatcherOnLaunch = false;
        config.SubmitChatGptPrompt = false;
        config.SubmitCodexPrompt = false;
        config.AutoCaptureChatGptEnvelope = false;
        config.AutoSendCapturedTaskToCodex = false;
        config.AutomaticInstructionDeliveryEnabled = false;
        config.LiveManualPilotAuthorized = false;
        config.AutomaticWakeEnabled = false;
        config.AutomaticDeliveryEnabled = false;
        config.LiveCodexIntakeEnabled = false;
        config.Stage4Authorized = false;
        config.Stage5Authorized = false;
        config.UiBridgeToCodex = false;
        config.CodexUiPasteFallbackEnabled = false;
        config.CodexUseClipboardFallback = false;
        config.ChatGptOpenIfMissing = false;
    }

    public static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !SafeProfileId.IsMatch(profileId) ||
            profileId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Profile ID is not safe for local storage.", nameof(profileId));
        }
    }

    private AppConfig Deserialize(string path)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? CreatePortableDefaults();
        config.InstallationRoot = _applicationRoot;
        config.InstallationSecurityRoot = Path.Combine(_applicationRoot, "security");
        config.ProfileDirectory = Path.Combine(_applicationRoot, "profiles");
        config.WatcherPreferencesPath = Path.Combine(_applicationRoot, "preferences.json");
        return config;
    }

    private static void ApplySafeCompatibilityMigrations(AppConfig config)
    {
        if (!Enum.TryParse<AuthorizedInstructionDeliveryMode>(config.InstructionDeliveryMode, true, out _))
        {
            config.InstructionDeliveryMode = nameof(AuthorizedInstructionDeliveryMode.HashBoundFile);
        }
        config.ChatGptCaptureScope = "BackendMessageObject";
        config.Stage5Authorized = false;
    }

    private static string? FindLegacyConfigPath()
    {
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, string.Concat(".dcs", "-watcher-v2"), "config.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                if (seen.Add(current.FullName)) yield return current.FullName;
            }
        }
    }
}
