using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record EnvelopeCaptureResult(
    bool Success,
    CapturedTaskEnvelope? Envelope,
    string Message,
    EnvelopeExtractionDiagnostics? Diagnostics = null);

public sealed record EnvelopeExtractionDiagnostics(
    int EnvelopeCount,
    int TextLength,
    bool FoundOpening,
    bool FoundClosing,
    int AssistantMessageCount,
    int AssistantEnvelopeCount = 0,
    int BodyEnvelopeCount = 0,
    bool UsedBodyFallback = false,
    int SelectedAssistantMessageIndex = -1,
    int SelectedAssistantEnvelopeCount = 0,
    string CaptureScope = "",
    string SelectedTaskId = "",
    string SkippedDuplicateTaskIds = "",
    string RejectionReason = "");

public sealed class ChatGptEnvelopeCapture
{
    public const string OpenMarker = "<<<DCS_CODEX_TASK_V1>>>";
    public const string CloseMarker = "<<<END_DCS_CODEX_TASK_V1>>>";
    public const string LatestAssistantMessageScope = "LatestAssistantMessage";
    public const string WholeConversationNewestScope = "WholeConversationNewest";
    private const string BeginInstruction = "BEGIN_INSTRUCTION";
    private const string EndInstruction = "END_INSTRUCTION";

    public EnvelopeCaptureResult TryCaptureFromAssistantMessages(
        IReadOnlyList<string> assistantMessages,
        AppConfig config,
        AppState state)
    {
        if (assistantMessages.Count == 0)
        {
            return Fail(
                "No assistant messages were available to capture.",
                new EnvelopeExtractionDiagnostics(0, 0, false, false, 0, CaptureScope: ResolveCaptureScope(config)));
        }

        var scope = ResolveCaptureScope(config);
        if (scope.Equals(WholeConversationNewestScope, StringComparison.OrdinalIgnoreCase))
        {
            var wholeConversationText = string.Join(Environment.NewLine + Environment.NewLine, assistantMessages);
            return TryCapture(
                wholeConversationText,
                config,
                state,
                BuildDiagnostics(wholeConversationText, assistantMessages.Count) with
                {
                    CaptureScope = WholeConversationNewestScope,
                    SelectedAssistantMessageIndex = FindNewestMessageIndexWithEnvelope(assistantMessages),
                    SelectedAssistantEnvelopeCount = 1
                });
        }

        for (var index = assistantMessages.Count - 1; index >= 0; index--)
        {
            var messageText = assistantMessages[index];
            if (!messageText.Contains(OpenMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var envelopeCount = ExtractEnvelopes(messageText).Count;

            var diagnostics = BuildDiagnostics(messageText, assistantMessages.Count) with
            {
                CaptureScope = LatestAssistantMessageScope,
                SelectedAssistantMessageIndex = index,
                SelectedAssistantEnvelopeCount = envelopeCount,
                AssistantEnvelopeCount = envelopeCount
            };

            return TryCapture(messageText, config, state, diagnostics);
        }

        return Fail(
            "No assistant message contained a DCS_CODEX_TASK_V1 envelope marker.",
            new EnvelopeExtractionDiagnostics(
                EnvelopeCount: 0,
                TextLength: assistantMessages.Sum(message => message.Length),
                FoundOpening: assistantMessages.Any(message => message.Contains(OpenMarker, StringComparison.Ordinal)),
                FoundClosing: assistantMessages.Any(message => message.Contains(CloseMarker, StringComparison.Ordinal)),
                AssistantMessageCount: assistantMessages.Count,
                CaptureScope: LatestAssistantMessageScope));
    }

    public EnvelopeCaptureResult TryCapture(
        string rawText,
        AppConfig config,
        AppState state,
        EnvelopeExtractionDiagnostics? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Fail("No text was available to capture.", diagnostics);
        }

        var candidateDiagnostics = diagnostics ?? BuildDiagnostics(rawText, assistantMessageCount: 0);
        if (candidateDiagnostics.UsedBodyFallback ||
            candidateDiagnostics.CaptureScope.Equals(WholeConversationNewestScope, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Whole-page, whole-conversation, and fallbackBody capture are diagnostic-only and cannot authorize an instruction.", candidateDiagnostics);
        }

        var envelopes = ExtractEnvelopes(rawText);
        var lastOpenIndex = rawText.LastIndexOf(OpenMarker, StringComparison.Ordinal);
        if (config.CaptureNewestEnvelopeOnly && lastOpenIndex >= 0)
        {
            var closeAfterLastOpen = rawText.IndexOf(CloseMarker, lastOpenIndex + OpenMarker.Length, StringComparison.Ordinal);
            if (closeAfterLastOpen < 0)
            {
                return Fail("Newest envelope is missing closing marker.", candidateDiagnostics);
            }
        }

        if (!candidateDiagnostics.FoundOpening)
        {
            return Fail("Envelope is missing opening marker.", candidateDiagnostics);
        }

        if (!candidateDiagnostics.FoundClosing)
        {
            return Fail("Envelope is missing closing marker.", candidateDiagnostics);
        }

        if (envelopes.Count == 0)
        {
            return Fail("No complete DCS_CODEX_TASK_V1 envelope was found.", candidateDiagnostics);
        }

        if (config.RequireSingleTaskEnvelope &&
            envelopes.Count > 1 &&
            (candidateDiagnostics.CaptureScope.Equals(LatestAssistantMessageScope, StringComparison.OrdinalIgnoreCase) ||
             !config.CaptureNewestEnvelopeOnly))
        {
            return Fail($"More than one task envelope found ({envelopes.Count}) and RequireSingleTaskEnvelope is enabled.", candidateDiagnostics);
        }

        var envelopeText = config.CaptureNewestEnvelopeOnly ? envelopes[^1] : envelopes[0];

        if (envelopeText.Length > config.MaxEnvelopeChars)
        {
            return Fail($"Envelope length {envelopeText.Length} exceeds MaxEnvelopeChars {config.MaxEnvelopeChars}.", candidateDiagnostics);
        }

        if (!envelopeText.Contains(BeginInstruction, StringComparison.Ordinal))
        {
            return Fail("Envelope is missing BEGIN_INSTRUCTION.", candidateDiagnostics);
        }

        if (!envelopeText.Contains(EndInstruction, StringComparison.Ordinal))
        {
            return Fail("Envelope is missing END_INSTRUCTION.", candidateDiagnostics);
        }

        var instructionStart = envelopeText.IndexOf(BeginInstruction, StringComparison.Ordinal);
        var instructionBodyStart = instructionStart + BeginInstruction.Length;
        var instructionEnd = envelopeText.IndexOf(EndInstruction, instructionBodyStart, StringComparison.Ordinal);

        if (instructionEnd < instructionBodyStart)
        {
            return Fail("Instruction markers are not ordered correctly.", candidateDiagnostics);
        }

        var instructionBody = envelopeText.Substring(instructionBodyStart, instructionEnd - instructionBodyStart);
        if (instructionBody.Trim().Length < config.MinInstructionChars)
        {
            return Fail($"Instruction body length {instructionBody.Trim().Length} is shorter than MinInstructionChars {config.MinInstructionChars}.", candidateDiagnostics);
        }

        var taskId = ReadRequiredField(envelopeText, "task_id");
        candidateDiagnostics = candidateDiagnostics with { SelectedTaskId = taskId };
        var origin = ReadRequiredField(envelopeText, "origin");
        var repo = ReadRequiredField(envelopeText, "repo");
        var target = ReadRequiredField(envelopeText, "target");
        var mode = ReadRequiredField(envelopeText, "mode");
        var createdAtText = ReadRequiredField(envelopeText, "created_at");
        var sourceReport = ReadRequiredField(envelopeText, "source_report");

        var missingFields = new[]
            {
                ("task_id", taskId),
                ("origin", origin),
                ("repo", repo),
                ("target", target),
                ("mode", mode),
                ("created_at", createdAtText),
                ("source_report", sourceReport)
            }
            .Where(field => string.IsNullOrWhiteSpace(field.Item2))
            .Select(field => field.Item1)
            .ToArray();

        if (missingFields.Length > 0)
        {
            return Fail($"Envelope is missing required field(s): {string.Join(", ", missingFields)}.", candidateDiagnostics);
        }

        if (state.IsTaskAlreadyCapturedOrPending(taskId))
        {
            return Fail($"Duplicate task_id already captured or pending delivery: {taskId}", candidateDiagnostics);
        }

        if (state.IsCodexTaskSent(taskId))
        {
            return Fail($"Duplicate task_id already sent to Codex: {taskId}", candidateDiagnostics);
        }

        if (!config.AllowRepoMismatch &&
            !repo.Equals(config.ExpectedRepo, StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"Envelope repo '{repo}' does not match configured repo '{config.ExpectedRepo}'.", candidateDiagnostics);
        }

        DateTimeOffset? createdAt = null;
        if (DateTimeOffset.TryParse(createdAtText, out var parsedCreatedAt))
        {
            createdAt = parsedCreatedAt;
        }

        var envelope = new CapturedTaskEnvelope
        {
            TaskId = taskId,
            Origin = origin,
            Repo = repo,
            Target = target,
            Mode = mode,
            CreatedAt = createdAt,
            SourceReport = sourceReport,
            InstructionBody = instructionBody,
            RawEnvelope = envelopeText
        };

        return new EnvelopeCaptureResult(true, envelope, $"Captured valid task envelope {taskId}.", candidateDiagnostics);
    }

    public static IReadOnlyList<string> ExtractEnvelopes(string text)
    {
        var envelopes = new List<string>();
        var searchStart = 0;

        while (searchStart < text.Length)
        {
            var openIndex = text.IndexOf(OpenMarker, searchStart, StringComparison.Ordinal);
            if (openIndex < 0)
            {
                break;
            }

            var closeIndex = text.IndexOf(CloseMarker, openIndex + OpenMarker.Length, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                break;
            }

            var endIndex = closeIndex + CloseMarker.Length;
            envelopes.Add(text.Substring(openIndex, endIndex - openIndex));
            searchStart = endIndex;
        }

        return envelopes;
    }

    public static EnvelopeExtractionDiagnostics BuildDiagnostics(string text, int assistantMessageCount)
    {
        return new EnvelopeExtractionDiagnostics(
            EnvelopeCount: ExtractEnvelopes(text).Count,
            TextLength: text.Length,
            FoundOpening: text.Contains(OpenMarker, StringComparison.Ordinal),
            FoundClosing: text.Contains(CloseMarker, StringComparison.Ordinal),
            AssistantMessageCount: assistantMessageCount,
            AssistantEnvelopeCount: ExtractEnvelopes(text).Count);
    }

    public static string ResolveCaptureScope(AppConfig config)
    {
        return config.ChatGptCaptureScope?.Trim() switch
        {
            WholeConversationNewestScope => WholeConversationNewestScope,
            LatestAssistantMessageScope => LatestAssistantMessageScope,
            _ => LatestAssistantMessageScope
        };
    }

    private static int FindNewestMessageIndexWithEnvelope(IReadOnlyList<string> assistantMessages)
    {
        for (var index = assistantMessages.Count - 1; index >= 0; index--)
        {
            if (ExtractEnvelopes(assistantMessages[index]).Count > 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static EnvelopeCaptureResult Fail(string message, EnvelopeExtractionDiagnostics? diagnostics)
    {
        return new EnvelopeCaptureResult(false, null, message, diagnostics is null
            ? null
            : diagnostics with { RejectionReason = message });
    }

    private static string ReadRequiredField(string text, string fieldName)
    {
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
            {
                continue;
            }

            var key = line[..colonIndex].Trim();
            if (key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colonIndex + 1)..].Trim();
            }
        }

        var instructionStart = text.IndexOf(BeginInstruction, StringComparison.Ordinal);
        var header = instructionStart >= 0 ? text[..instructionStart] : text;
        header = header.Replace(OpenMarker, " ", StringComparison.Ordinal);

        var orderedFields = new[]
        {
            "task_id",
            "origin",
            "repo",
            "target",
            "mode",
            "created_at",
            "source_report"
        };
        var alternation = string.Join("|", orderedFields.Select(System.Text.RegularExpressions.Regex.Escape));
        var pattern = $@"(?is)(?:^|\s){System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*:\s*(.*?)(?=\s+(?:{alternation})\s*:|$)";
        var match = System.Text.RegularExpressions.Regex.Match(header, pattern);
        if (!match.Success)
        {
            return string.Empty;
        }

        return System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty);
    }
}
