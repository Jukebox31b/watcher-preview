using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ManualAuthorizationValidationResult(bool Accepted, string ReasonCode, string Message);

public sealed class ManualInstructionAuthorizationValidator
{
    public ManualAuthorizationValidationResult ValidateFile(
        ManualInstructionAuthorizationV1 authorization,
        string expectedThreadId)
    {
        if (!authorization.Schema.Equals("manual_user_visible_file_authorization", StringComparison.Ordinal))
        {
            return Reject("MANUAL_SCHEMA_INVALID", "Manual authorization uses the wrong classification.");
        }

        if (!authorization.ReceivingCodexThreadId.Equals(expectedThreadId, StringComparison.Ordinal))
        {
            return Reject("MANUAL_DESTINATION_MISMATCH", "Manual authorization targets another Codex thread.");
        }

        if (string.IsNullOrWhiteSpace(authorization.DirectManuallyPastedAuthorizationText) || authorization.ReceiptTimestampUtc == default)
        {
            return Reject("MANUAL_AUTHORIZATION_MISSING", "A direct manually pasted authorization and receipt time are required.");
        }

        var textHash = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(authorization.DirectManuallyPastedAuthorizationText));
        if (!textHash.Equals(authorization.AuthorizationTextSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("MANUAL_TEXT_HASH_MISMATCH", "Manual authorization text hash does not match.");
        }

        if (!Path.IsPathFullyQualified(authorization.AbsoluteFilePath) || !File.Exists(authorization.AbsoluteFilePath))
        {
            return Reject("MANUAL_FILE_MISSING", "Authorized file path must be absolute and exist.");
        }

        var bytes = File.ReadAllBytes(authorization.AbsoluteFilePath);
        if (bytes.LongLength != authorization.ExpectedSizeBytes)
        {
            return Reject("MANUAL_FILE_SIZE_MISMATCH", "Manual file size does not match authorization.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return hash.Equals(authorization.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
            ? new ManualAuthorizationValidationResult(true, "OK", "Manual user-visible file authorization is valid and remains distinct from Watcher provenance.")
            : Reject("MANUAL_FILE_HASH_MISMATCH", "Manual file SHA-256 does not match authorization.");
    }

    private static ManualAuthorizationValidationResult Reject(string code, string message) => new(false, code, message);
}
