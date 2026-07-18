using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record Stage2CanonicalParseResult(
    bool Success,
    string ReasonCode,
    string Message,
    Stage2BoundInstructionTransactionV1? Transaction = null);

public static class Stage2CanonicalJson
{
    public const int MaxPayloadBytes = 1_000_000;
    public const int MaxJsonStringChars = 750_000;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] SerializeUnsignedProvenance(Stage2InstructionProvenanceV1 provenance)
    {
        var clone = CloneProvenance(provenance);
        clone.SignatureOrMac = string.Empty;
        return JsonSerializer.SerializeToUtf8Bytes(clone, Options);
    }

    public static byte[] SerializeSignedProvenance(Stage2InstructionProvenanceV1 provenance)
    {
        return JsonSerializer.SerializeToUtf8Bytes(provenance, Options);
    }

    public static byte[] SerializeTransaction(Stage2BoundInstructionTransactionV1 transaction)
    {
        return JsonSerializer.SerializeToUtf8Bytes(transaction, Options);
    }

    public static Stage2CanonicalParseResult ParseTransaction(byte[] payloadBytes)
    {
        if (payloadBytes.Length == 0)
        {
            return Fail("INVALID_JSON", "Transaction payload is empty.");
        }

        if (payloadBytes.Length > MaxPayloadBytes)
        {
            return Fail("OVERSIZED_PAYLOAD", $"Transaction payload exceeds {MaxPayloadBytes} bytes.");
        }

        var inspection = Inspect(payloadBytes);
        if (!inspection.Success)
        {
            return Fail(inspection.ReasonCode, inspection.Message);
        }

        Stage2BoundInstructionTransactionV1? transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<Stage2BoundInstructionTransactionV1>(payloadBytes, Options);
        }
        catch (JsonException ex)
        {
            var reason = ex.Message.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase)
                ? "UNKNOWN_JSON_FIELD"
                : "INVALID_JSON_TYPE";
            return Fail(reason, ex.Message);
        }

        if (transaction is null)
        {
            return Fail("INVALID_JSON", "Transaction JSON deserialized to null.");
        }

        var canonical = SerializeTransaction(transaction);
        if (!payloadBytes.AsSpan().SequenceEqual(canonical))
        {
            return Fail("NONCANONICAL_JSON", "Transaction JSON is valid but is not in canonical serialization.");
        }

        return new Stage2CanonicalParseResult(true, "OK", "Canonical transaction parsed.", transaction);
    }

    public static Stage2InstructionProvenanceV1 CloneProvenance(Stage2InstructionProvenanceV1 source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, Options);
        return JsonSerializer.Deserialize<Stage2InstructionProvenanceV1>(bytes, Options)
            ?? throw new InvalidOperationException("Could not clone provenance object.");
    }

    private static JsonInspectionResult Inspect(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var objectProperties = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        objectProperties.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                    {
                        var propertyName = reader.GetString() ?? string.Empty;
                        if (objectProperties.Count == 0 || !objectProperties.Peek().Add(propertyName))
                        {
                            return new JsonInspectionResult(false, "DUPLICATE_JSON_KEY", $"Duplicate JSON key: {propertyName}");
                        }

                        var stringCheck = ValidateJsonString(propertyName);
                        if (!stringCheck.Success)
                        {
                            return stringCheck;
                        }

                        break;
                    }
                    case JsonTokenType.String:
                    {
                        var value = reader.GetString() ?? string.Empty;
                        var stringCheck = ValidateJsonString(value);
                        if (!stringCheck.Success)
                        {
                            return stringCheck;
                        }

                        break;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            return new JsonInspectionResult(false, "INVALID_JSON", ex.Message);
        }

        return new JsonInspectionResult(true, "OK", "JSON structure is strict.");
    }

    private static JsonInspectionResult ValidateJsonString(string value)
    {
        if (value.Length > MaxJsonStringChars)
        {
            return new JsonInspectionResult(false, "OVERSIZED_JSON_VALUE", "A JSON string exceeds the configured maximum.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character < 0x20 || character == 0x7F)
            {
                return new JsonInspectionResult(false, "UNSAFE_JSON_CONTROL", $"Unsafe control character U+{(int)character:X4} in JSON string.");
            }

            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return new JsonInspectionResult(false, "INVALID_UNICODE", "JSON string contains an unpaired surrogate.");
                }

                index++;
            }
        }

        return new JsonInspectionResult(true, "OK", "String is safe.");
    }

    private static Stage2CanonicalParseResult Fail(string code, string message)
    {
        return new Stage2CanonicalParseResult(false, code, message);
    }

    private sealed record JsonInspectionResult(bool Success, string ReasonCode, string Message);
}
