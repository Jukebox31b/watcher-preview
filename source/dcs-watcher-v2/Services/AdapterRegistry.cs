using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class AdapterRegistry
{
    private static readonly IReadOnlyDictionary<string, WatcherAdapterMetadata> FixedAdapters =
        new Dictionary<string, WatcherAdapterMetadata>(StringComparer.Ordinal)
        {
            [WatcherAdapterIds.ReportGitRemote] = Preview(WatcherAdapterIds.ReportGitRemote, WatcherAdapterRole.ReportSource, "Git remote", "Reads reports from a configured Git remote."),
            [WatcherAdapterIds.ReportGitHub] = Preview(WatcherAdapterIds.ReportGitHub, WatcherAdapterRole.ReportSource, "GitHub", "Reads reports from a configured GitHub repository."),
            [WatcherAdapterIds.ReportLocalFolder] = Preview(WatcherAdapterIds.ReportLocalFolder, WatcherAdapterRole.ReportSource, "Local folder", "Reads reports from a configured local folder."),
            [WatcherAdapterIds.ReportGitHubLocalFallback] = Experimental(WatcherAdapterIds.ReportGitHubLocalFallback, WatcherAdapterRole.ReportSource, "GitHub local fallback", "Experimental local fallback for GitHub report acquisition."),
            [WatcherAdapterIds.ReportDemoFixture] = DemoOnly(WatcherAdapterIds.ReportDemoFixture, WatcherAdapterRole.ReportSource, "Synthetic report fixture", "Deterministic sanitized report input for the offline demo."),

            [WatcherAdapterIds.DirectorChatGptEdgeCdp] = Experimental(WatcherAdapterIds.DirectorChatGptEdgeCdp, WatcherAdapterRole.Director, "ChatGPT desktop session", "Experimental authenticated desktop-session Director adapter."),
            [WatcherAdapterIds.DirectorManualEnvelope] = Preview(WatcherAdapterIds.DirectorManualEnvelope, WatcherAdapterRole.Director, "Manual envelope", "Accepts one human-presented instruction envelope."),
            [WatcherAdapterIds.DirectorHashBoundFile] = Preview(WatcherAdapterIds.DirectorHashBoundFile, WatcherAdapterRole.Director, "Hash-bound file", "Accepts one explicitly authorized hash-bound instruction file."),
            [WatcherAdapterIds.DirectorDemoFixture] = DemoOnly(WatcherAdapterIds.DirectorDemoFixture, WatcherAdapterRole.Director, "Synthetic Director fixture", "Deterministic sanitized message lineage for the offline demo."),

            [WatcherAdapterIds.DeliveryCodexVerifiedIpc] = Experimental(WatcherAdapterIds.DeliveryCodexVerifiedIpc, WatcherAdapterRole.Delivery, "Verified Codex delivery", "Experimental verified local Codex delivery."),
            [WatcherAdapterIds.DeliveryHashBoundFile] = Preview(WatcherAdapterIds.DeliveryHashBoundFile, WatcherAdapterRole.Delivery, "Hash-bound file", "Writes an integrity-bound delivery file for explicit handling."),
            [WatcherAdapterIds.DeliveryManualVisiblePaste] = Preview(WatcherAdapterIds.DeliveryManualVisiblePaste, WatcherAdapterRole.Delivery, "Manual visible paste", "Presents content for an explicit human-visible handoff."),
            [WatcherAdapterIds.DeliveryTestSink] = DemoOnly(WatcherAdapterIds.DeliveryTestSink, WatcherAdapterRole.Delivery, "Non-actionable test sink", "Accepts verified demo evidence in memory and performs no action."),
            [WatcherAdapterIds.DeliveryUiPasteFallback] = Experimental(WatcherAdapterIds.DeliveryUiPasteFallback, WatcherAdapterRole.Delivery, "UI paste fallback", "Experimental visible UI fallback delivery."),
        };

    public IReadOnlyList<WatcherAdapterMetadata> All =>
        FixedAdapters.Values.OrderBy(adapter => adapter.AdapterId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<WatcherAdapterMetadata> ForRole(WatcherAdapterRole role) =>
        FixedAdapters.Values.Where(adapter => adapter.Role == role).OrderBy(adapter => adapter.AdapterId, StringComparer.Ordinal).ToArray();

    public AdapterResolutionResult Resolve(string adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId) || !FixedAdapters.TryGetValue(adapterId, out var metadata))
        {
            return new AdapterResolutionResult(false, "UNKNOWN_ADAPTER", $"Unknown or unsupported adapter '{adapterId}'.");
        }

        return new AdapterResolutionResult(true, "OK", "Adapter metadata resolved.", metadata);
    }

    public WatcherAdapterMetadata GetRequired(string adapterId)
    {
        var result = Resolve(adapterId);
        return result.Metadata ?? throw new ArgumentException(result.Message, nameof(adapterId));
    }

    private static WatcherAdapterMetadata Preview(string id, WatcherAdapterRole role, string name, string description) =>
        new(id, role, name, WatcherAdapterMaturity.Preview, description);

    private static WatcherAdapterMetadata Experimental(string id, WatcherAdapterRole role, string name, string description) =>
        new(id, role, name, WatcherAdapterMaturity.Experimental, description);

    private static WatcherAdapterMetadata DemoOnly(string id, WatcherAdapterRole role, string name, string description) =>
        new(id, role, name, WatcherAdapterMaturity.DemoOnly, description);
}
