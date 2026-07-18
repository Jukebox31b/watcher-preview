using System.Text.RegularExpressions;

namespace DcsWatcherV2.Services;

public static class ChatGptUrlMatcher
{
    private static readonly Regex GptBaseIdRegex = new(
        @"^(?<base>g-[a-z0-9]+-[0-9a-f]{12,})(?:-.+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ChatGptUrlMatchResult Compare(
        string configuredUrl,
        string tabUrl,
        bool requireSameConversation)
    {
        var configured = Parse(configuredUrl);
        var tab = Parse(tabUrl);
        var gptBaseMatches = !string.IsNullOrWhiteSpace(configured.GptBaseId) &&
                             !string.IsNullOrWhiteSpace(tab.GptBaseId) &&
                             configured.GptBaseId.Equals(tab.GptBaseId, StringComparison.OrdinalIgnoreCase);
        var hasBothConversationIds = !string.IsNullOrWhiteSpace(configured.ConversationId) &&
                                     !string.IsNullOrWhiteSpace(tab.ConversationId);
        var conversationMatches = hasBothConversationIds &&
                                  configured.ConversationId.Equals(tab.ConversationId, StringComparison.OrdinalIgnoreCase);

        if (!configured.IsChatGptHost || !tab.IsChatGptHost)
        {
            return new ChatGptUrlMatchResult(
                false,
                configured,
                tab,
                conversationMatches,
                gptBaseMatches,
                "Configured URL and tab URL must both be on chatgpt.com.");
        }

        if (hasBothConversationIds)
        {
            return new ChatGptUrlMatchResult(
                conversationMatches,
                configured,
                tab,
                conversationMatches,
                gptBaseMatches,
                conversationMatches
                    ? "Matched same ChatGPT conversation id."
                    : "Different ChatGPT conversation id.");
        }

        if (requireSameConversation)
        {
            return new ChatGptUrlMatchResult(
                false,
                configured,
                tab,
                conversationMatches,
                gptBaseMatches,
                "Require same ChatGPT conversation is enabled, but one URL is missing /c/{conversation_id}.");
        }

        if (!string.IsNullOrWhiteSpace(configured.GptBaseId) &&
            !string.IsNullOrWhiteSpace(tab.GptBaseId))
        {
            return new ChatGptUrlMatchResult(
                gptBaseMatches,
                configured,
                tab,
                conversationMatches,
                gptBaseMatches,
                gptBaseMatches
                    ? "Matched same ChatGPT GPT/project base id."
                    : "Different ChatGPT GPT/project base id.");
        }

        return new ChatGptUrlMatchResult(
            true,
            configured,
            tab,
            conversationMatches,
            gptBaseMatches,
            "Matched chatgpt.com host with conversation requirement disabled.");
    }

    public static ChatGptUrlParts Parse(string? url)
    {
        var originalUrl = url ?? string.Empty;
        if (!Uri.TryCreate(originalUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new ChatGptUrlParts(originalUrl, string.Empty, string.Empty, string.Empty, string.Empty, false);
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var gptSegment = ReadSegmentAfter(segments, "g");
        var conversationId = ReadSegmentAfter(segments, "c");
        var host = uri.Host.ToLowerInvariant();
        return new ChatGptUrlParts(
            originalUrl,
            host,
            gptSegment,
            NormalizeGptBaseId(gptSegment),
            conversationId,
            host.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadSegmentAfter(IReadOnlyList<string> segments, string marker)
    {
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (segments[i].Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return string.Empty;
    }

    private static string NormalizeGptBaseId(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var trimmed = segment.Trim().ToLowerInvariant();
        var match = GptBaseIdRegex.Match(trimmed);
        return match.Success ? match.Groups["base"].Value : trimmed;
    }
}

public sealed record ChatGptUrlParts(
    string OriginalUrl,
    string Host,
    string GptSegment,
    string GptBaseId,
    string ConversationId,
    bool IsChatGptHost);

public sealed record ChatGptUrlMatchResult(
    bool IsMatch,
    ChatGptUrlParts Configured,
    ChatGptUrlParts Tab,
    bool ConversationIdMatches,
    bool GptBaseIdMatches,
    string Reason);
