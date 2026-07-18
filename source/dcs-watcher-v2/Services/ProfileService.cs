using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class ProfileService
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ProfileValidator _validator;

    public ProfileService(string profileDirectory, ProfileValidator? validator = null)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory)) throw new ArgumentException("Profile directory is required.", nameof(profileDirectory));
        ProfileDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(profileDirectory));
        if (IsInsideGitRepository(ProfileDirectory))
        {
            throw new InvalidOperationException("Profile storage must be outside a Git source tree.");
        }
        _validator = validator ?? new ProfileValidator();
    }

    public string ProfileDirectory { get; }

    public static string GetDefaultProfileDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DcsWatcherV2",
        "profiles");

    public WatcherProfileV1 CreateFresh(string name)
    {
        return new WatcherProfileV1
        {
            Enabled = false,
            Identity = new ProfileIdentityV1
            {
                ProfileId = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim()
            },
            ReportSource = new ReportSourceProfileV1
            {
                Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.ReportDemoFixture }
            },
            Director = new DirectorProfileV1
            {
                Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DirectorManualEnvelope }
            },
            Destination = new DestinationProfileV1
            {
                Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DeliveryTestSink }
            },
            AutomationPolicy = new AutomationPolicyProfileV1
            {
                Kind = WatcherAutomationPolicyKind.ManualApproval,
                RequireVisibleHumanApproval = true
            }
        };
    }

    public WatcherProfileV1 CreateImportedLegacyProfile(AppConfig legacy)
    {
        var reportAdapter = legacy.ReportPollMode.Trim().ToLowerInvariant() switch
        {
            "gitremote" => WatcherAdapterIds.ReportGitRemote,
            "github" => WatcherAdapterIds.ReportGitHub,
            "localfolder" => WatcherAdapterIds.ReportLocalFolder,
            "githubthenlocalfallback" => WatcherAdapterIds.ReportGitHubLocalFallback,
            _ => WatcherAdapterIds.ReportDemoFixture
        };
        var reportSettings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(reportSettings, "git_root", legacy.ReportGitRoot);
        AddIfPresent(reportSettings, "remote", legacy.ReportRemote);
        AddIfPresent(reportSettings, "folder", legacy.ReportFolder);
        AddIfPresent(reportSettings, "local_root", legacy.LocalReportRoot);
        AddIfPresent(reportSettings, "blob_base", legacy.ReportGitHubBlobBase);

        var directorSettings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(directorSettings, "conversation_url", legacy.ChatGptDirectorUrl);
        AddIfPresent(directorSettings, "cdp_host", legacy.ChatGptCdpHost);
        if (legacy.ChatGptCdpPort > 0) directorSettings["cdp_port"] = legacy.ChatGptCdpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var destinationIdentity = FirstNonBlank(legacy.CodexDirectorThreadId, legacy.CodexThreadId);
        var deliveryAdapter = string.IsNullOrWhiteSpace(destinationIdentity)
            ? WatcherAdapterIds.DeliveryTestSink
            : WatcherAdapterIds.DeliveryCodexVerifiedIpc;

        return new WatcherProfileV1
        {
            Enabled = false,
            Identity = new ProfileIdentityV1
            {
                ProfileId = "imported-configuration",
                Name = "Imported configuration",
                Description = "Disabled legacy import. Review every setting before enabling."
            },
            ReportSource = new ReportSourceProfileV1
            {
                Adapter = new AdapterConfigurationV1 { AdapterId = reportAdapter, Settings = reportSettings },
                ExpectedRepository = legacy.ReportRepoFullName,
                ExpectedBranch = legacy.ReportBranch
            },
            Director = new DirectorProfileV1
            {
                Adapter = new AdapterConfigurationV1
                {
                    AdapterId = string.IsNullOrWhiteSpace(legacy.ChatGptDirectorUrl)
                        ? WatcherAdapterIds.DirectorManualEnvelope
                        : WatcherAdapterIds.DirectorChatGptEdgeCdp,
                    Settings = directorSettings
                },
                ConversationIdentity = legacy.ChatGptDirectorUrl,
                RequireDirectParent = true,
                RequireCurrentPath = true,
                RequireBackendMessageObject = true,
                AllowFallbackBody = false,
                AllowWholePageCapture = false
            },
            Protocol = new ProtocolProfileV1 { MaximumEnvelopeBytes = Math.Clamp(legacy.MaxEnvelopeChars, 100, 1_000_000) },
            Destination = new DestinationProfileV1
            {
                Adapter = new AdapterConfigurationV1 { AdapterId = deliveryAdapter },
                DestinationIdentity = destinationIdentity
            },
            AutomationPolicy = new AutomationPolicyProfileV1
            {
                Kind = WatcherAutomationPolicyKind.ManualApproval,
                PolicyGeneration = 0,
                RequireVisibleHumanApproval = true
            },
            Guardrails = new GuardrailsProfileV1
            {
                MaximumTasksPerRun = 0,
                MaximumElapsedMinutes = 0,
                SummaryIntervalMinutes = 0,
                StopOnFailure = true,
                StopOnBranchDivergence = true
            },
            SecurityBinding = new PublicSecurityBindingV1()
        };
    }

    public string Save(WatcherProfileV1 profile, bool overwrite = true)
    {
        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Profile validation failed: " + string.Join("; ", validation.Issues.Select(issue => issue.Code + " at " + issue.Path)));
        }

        Directory.CreateDirectory(ProfileDirectory);
        var path = GetProfilePath(profile.Identity.ProfileId);
        if (!overwrite && File.Exists(path)) throw new IOException($"Profile already exists: {path}");
        Stage2AtomicFile.WriteAllBytes(path, SerializeCanonical(profile));
        return path;
    }

    public WatcherProfileV1 Load(string profileId)
    {
        var path = GetProfilePath(profileId);
        var bytes = File.ReadAllBytes(path);
        EnsureNoDuplicateProperties(bytes);
        var profile = JsonSerializer.Deserialize<WatcherProfileV1>(bytes, CanonicalOptions)
            ?? throw new InvalidDataException("Profile JSON deserialized to null.");
        var canonical = SerializeCanonical(profile);
        if (!bytes.AsSpan().SequenceEqual(canonical)) throw new InvalidDataException("Profile JSON is not in canonical form.");
        var validation = _validator.Validate(profile);
        if (!validation.IsValid) throw new InvalidDataException("Profile validation failed: " + string.Join("; ", validation.Issues.Select(issue => issue.Code)));
        return profile;
    }

    public IReadOnlyList<string> ListProfileIds()
    {
        if (!Directory.Exists(ProfileDirectory)) return [];
        return Directory.EnumerateFiles(ProfileDirectory, "*.watcher-profile.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name![..^".watcher-profile".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public byte[] SerializeCanonical(WatcherProfileV1 profile) => JsonSerializer.SerializeToUtf8Bytes(profile, CanonicalOptions);

    public string ComputeSha256(WatcherProfileV1 profile) => Convert.ToHexString(SHA256.HashData(SerializeCanonical(profile))).ToLowerInvariant();

    private string GetProfilePath(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profileId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Profile ID is not safe for file storage.", nameof(profileId));
        }
        return Path.Combine(ProfileDirectory, profileId + ".watcher-profile.json");
    }

    private static bool IsInsideGitRepository(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return true;
            current = current.Parent;
        }
        return false;
    }

    private static void EnsureNoDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        Inspect(document.RootElement, "$" );

        static void Inspect(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property at {path}.{property.Name}.");
                    Inspect(property.Value, path + "." + property.Name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray()) Inspect(item, path + "[" + index++ + "]");
            }
        }
    }

    private static void AddIfPresent(IDictionary<string, string> settings, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) settings[key] = value;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
