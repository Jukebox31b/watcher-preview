using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class HashBoundInstructionFileService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public HashBoundFileValidationResult Validate(
        HashBoundInstructionAuthorization authorization,
        string approvedDirectory,
        IReadOnlyList<string>? expectedLiteralPaths = null)
    {
        if (string.IsNullOrWhiteSpace(authorization.AbsolutePath) || string.IsNullOrWhiteSpace(approvedDirectory))
        {
            return Reject("Authorization path and approved directory are required.");
        }

        string approvedRoot;
        string candidatePath;
        try
        {
            approvedRoot = Path.GetFullPath(approvedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            candidatePath = Path.GetFullPath(authorization.AbsolutePath);
        }
        catch (Exception ex)
        {
            return Reject($"Instruction path is invalid: {ex.Message}");
        }

        if (!candidatePath.StartsWith(approvedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidatePath).Equals(authorization.FileName, StringComparison.Ordinal))
        {
            return Reject("Instruction path escapes the approved directory or filename does not match authorization.");
        }

        if (!File.Exists(candidatePath))
        {
            return Reject("Authorized instruction file does not exist.");
        }

        var info = new FileInfo(candidatePath);
        if (authorization.FileLastWriteTimeUtcAtAuthorization is { } authorizedWriteTime &&
            info.LastWriteTimeUtc != authorizedWriteTime.UtcDateTime)
        {
            return Reject("Instruction file was modified after authorization.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(candidatePath);
        }
        catch (Exception ex)
        {
            return Reject($"Instruction file could not be read: {ex.Message}");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != authorization.ExpectedSizeBytes)
        {
            return Reject($"Instruction size mismatch: expected {authorization.ExpectedSizeBytes}, actual {bytes.LongLength}.", bytes.LongLength, actualHash);
        }

        if (!actualHash.Equals(authorization.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("Instruction SHA-256 does not match authorization.", bytes.LongLength, actualHash);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Reject("UTF-8 BOM is prohibited.", bytes.LongLength, actualHash);
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value <= 0x08 || value is 0x0B or 0x0C || (value >= 0x0E && value <= 0x1F) || value == 0x7F)
            {
                return Reject($"Forbidden control byte 0x{value:X2} at byte offset {index}.", bytes.LongLength, actualHash);
            }
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            return Reject($"Instruction is not strict UTF-8: {ex.Message}", bytes.LongLength, actualHash);
        }

        var envelopes = ChatGptEnvelopeCapture.ExtractEnvelopes(decoded);
        if (envelopes.Count != 1 ||
            Count(decoded, ChatGptEnvelopeCapture.OpenMarker) != 1 ||
            Count(decoded, ChatGptEnvelopeCapture.CloseMarker) != 1 ||
            Count(decoded, "BEGIN_INSTRUCTION") != 1 ||
            Count(decoded, "END_INSTRUCTION") != 1)
        {
            return Reject("Instruction file must contain exactly one complete strict task envelope and one instruction block.", bytes.LongLength, actualHash);
        }

        var envelope = envelopes[0];
        if (!decoded.TrimEnd('\r', '\n').Equals(envelope, StringComparison.Ordinal))
        {
            return Reject("Instruction file contains data outside the single envelope.", bytes.LongLength, actualHash);
        }

        var taskId = BranchLineageSafetyService.ReadField(envelope, "task_id");
        var sourceReport = BranchLineageSafetyService.ReadField(envelope, "source_report");
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(sourceReport))
        {
            return Reject("Instruction task_id or source_report is missing.", bytes.LongLength, actualHash);
        }

        if (!taskId.Equals(authorization.ExpectedTaskId, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("Instruction task_id does not match authorization.", bytes.LongLength, actualHash);
        }

        foreach (var expectedPath in expectedLiteralPaths ?? [])
        {
            if (!decoded.Contains(expectedPath, StringComparison.Ordinal))
            {
                return Reject($"Required path was not preserved byte-for-byte: {expectedPath}", bytes.LongLength, actualHash);
            }
        }

        return new HashBoundFileValidationResult(true, "Hash-bound instruction file is valid.", taskId, sourceReport, bytes.LongLength, actualHash, envelope);
    }

    public SafetyValidationResult ValidateSupersession(
        SupersessionRecord record,
        string approvedDirectory)
    {
        if (string.IsNullOrWhiteSpace(record.RevokedPath) || string.IsNullOrWhiteSpace(record.ReplacementPath) ||
            record.RevokedPath.Equals(record.ReplacementPath, StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyValidationResult(false, false, "Corrected authorization must use a new filename and preserve the revoked file.");
        }

        var root = Path.GetFullPath(approvedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var revoked = Path.GetFullPath(record.RevokedPath);
        var replacement = Path.GetFullPath(record.ReplacementPath);
        if (!revoked.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !replacement.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(revoked) || !File.Exists(replacement))
        {
            return new SafetyValidationResult(false, false, "Both revoked and replacement files must remain present in the approved directory.");
        }

        var revokedHash = HashFile(revoked);
        var replacementHash = HashFile(replacement);
        if (!revokedHash.Equals(record.RevokedSha256, StringComparison.OrdinalIgnoreCase) ||
            !replacementHash.Equals(record.ReplacementSha256, StringComparison.OrdinalIgnoreCase) ||
            revokedHash.Equals(replacementHash, StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyValidationResult(false, false, "Supersession hashes are invalid or replacement bytes are unchanged.");
        }

        return new SafetyValidationResult(true, false, "Corrected file explicitly supersedes the preserved revoked file.");
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static HashBoundFileValidationResult Reject(string reason, long size = 0, string hash = "")
    {
        return new HashBoundFileValidationResult(false, reason, ActualSizeBytes: size, ActualSha256: hash);
    }
}
