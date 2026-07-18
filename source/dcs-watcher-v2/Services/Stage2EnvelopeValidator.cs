using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage2EnvelopeValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Stage2EnvelopeValidationResult Validate(byte[] bytes, int maxEnvelopeBytes = 500_000)
    {
        if (bytes.Length == 0)
        {
            return Reject("ENVELOPE_EMPTY", "Envelope is empty.");
        }

        if (bytes.Length > maxEnvelopeBytes)
        {
            return Reject("ENVELOPE_OVERSIZED", $"Envelope exceeds {maxEnvelopeBytes} bytes.");
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Reject("UTF8_BOM", "UTF-8 BOM is not permitted.");
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value <= 0x08 || value is 0x0B or 0x0C || value is >= 0x0E and <= 0x1F || value == 0x7F)
            {
                return Reject("UNSAFE_CONTROL_BYTE", $"Forbidden control byte 0x{value:X2} at offset {index}.");
            }
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            return Reject("INVALID_UTF8", ex.Message);
        }

        if (Count(text, ChatGptEnvelopeCapture.OpenMarker) != 1 ||
            Count(text, ChatGptEnvelopeCapture.CloseMarker) != 1 ||
            Count(text, "BEGIN_INSTRUCTION") != 1 ||
            Count(text, "END_INSTRUCTION") != 1)
        {
            return Reject("ENVELOPE_COUNT_INVALID", "Exactly one complete envelope and instruction block is required.");
        }

        var envelopes = ChatGptEnvelopeCapture.ExtractEnvelopes(text);
        if (envelopes.Count != 1)
        {
            return Reject("ENVELOPE_PARTIAL", "Envelope markers are partial, malformed, or incorrectly ordered.");
        }

        var envelope = envelopes[0];
        if (!text.TrimEnd('\r', '\n').Equals(envelope, StringComparison.Ordinal))
        {
            return Reject("ENVELOPE_EXTRA_DATA", "Data outside the exact envelope is prohibited.");
        }

        var taskId = BranchLineageSafetyService.ReadField(envelope, "task_id");
        var sourceReport = BranchLineageSafetyService.ReadField(envelope, "source_report");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Reject("TASK_ID_MISSING", "task_id is required.");
        }

        if (string.IsNullOrWhiteSpace(sourceReport))
        {
            return Reject("SOURCE_REPORT_MISSING", "source_report is required.");
        }

        foreach (var field in new[] { "origin", "repo", "target", "mode", "created_at" })
        {
            if (string.IsNullOrWhiteSpace(BranchLineageSafetyService.ReadField(envelope, field)))
            {
                return Reject("ENVELOPE_FIELD_MISSING", $"{field} is required.");
            }
        }

        return new Stage2EnvelopeValidationResult(true, "OK", "Strict envelope bytes are valid.", envelope, taskId, sourceReport, bytes);
    }

    private static int Count(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }

        return count;
    }

    private static Stage2EnvelopeValidationResult Reject(string reasonCode, string reason)
    {
        return new Stage2EnvelopeValidationResult(false, reasonCode, reason);
    }
}
