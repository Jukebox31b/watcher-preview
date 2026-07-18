using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DcsWatcherV2.Services;

public sealed record Stage3MonotonicCounterResult(bool Accepted, string ReasonCode, string Message);

public sealed class Stage3WindowsMonotonicCounterStore
{
    private const string RegistryRoot = @"Software\DcsWatcherV2\Stage3\MonotonicCounters";
    private const string ValueName = "counter";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public Stage3MonotonicCounterResult Advance(
        string purpose,
        string instanceId,
        string anchorPath,
        long generation,
        string objectDigest,
        DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows())
            return Reject("MONOTONIC_STORE_UNAVAILABLE", "The Stage 3 monotonic counter requires Windows registry protection.");
        if (generation < 0 || string.IsNullOrWhiteSpace(purpose) || string.IsNullOrWhiteSpace(instanceId) ||
            objectDigest.Length != 64 || !objectDigest.All(Uri.IsHexDigit))
            return Reject("MONOTONIC_COUNTER_INPUT_INVALID", "Monotonic counter identity, generation, or digest is invalid.");

        var keyPath = KeyPath(purpose, instanceId, anchorPath);
        using var mutex = new Mutex(false, MutexName(keyPath));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
                return Reject("MONOTONIC_COUNTER_LOCK_TIMEOUT", "Timed out waiting for the external monotonic-counter lock.");

            return AdvanceUnderLock(purpose, instanceId, anchorPath, generation, objectDigest, nowUtc, keyPath);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private Stage3MonotonicCounterResult AdvanceUnderLock(
        string purpose,
        string instanceId,
        string anchorPath,
        long generation,
        string objectDigest,
        DateTimeOffset nowUtc,
        string keyPath)
    {
        CounterRecord? existing = null;
        using (var previous = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
        {
            if (previous is not null)
            {
                existing = Read(previous);
                if (existing is null)
                    return Reject("EXTERNAL_MONOTONIC_COUNTER_INVALID", "Existing external monotonic counter is missing or malformed.");
            }
        }
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (key is null) return Reject("MONOTONIC_STORE_OPEN_FAILED", "Could not open the current-user monotonic counter.");
        if (existing is not null)
        {
            if (existing.Generation > generation)
                return Reject("EXTERNAL_GENERATION_ROLLBACK", "External monotonic counter is newer than the presented state.");
            if (existing.Generation == generation)
            {
                return existing.ObjectDigest.Equals(objectDigest, StringComparison.OrdinalIgnoreCase)
                    ? new Stage3MonotonicCounterResult(true, "OK", "External monotonic counter already matches the presented state.")
                    : Reject("EXTERNAL_GENERATION_CONFLICT", "External monotonic counter has another digest at this generation.");
            }
        }

        var record = new CounterRecord
        {
            Purpose = purpose,
            InstanceId = instanceId,
            AnchorPath = Path.GetFullPath(anchorPath),
            Generation = generation,
            ObjectDigest = objectDigest.ToLowerInvariant(),
            UpdatedAtUtc = nowUtc.ToUniversalTime().ToString("O")
        };
        key.SetValue(ValueName, JsonSerializer.Serialize(record), RegistryValueKind.String);
        key.Flush();
        return Validate(purpose, instanceId, anchorPath, generation, objectDigest);
    }

    public Stage3MonotonicCounterResult Validate(
        string purpose,
        string instanceId,
        string anchorPath,
        long generation,
        string objectDigest)
    {
        if (!OperatingSystem.IsWindows())
            return Reject("MONOTONIC_STORE_UNAVAILABLE", "The Stage 3 monotonic counter requires Windows registry protection.");
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(purpose, instanceId, anchorPath), writable: false);
        if (key is null) return Reject("EXTERNAL_MONOTONIC_COUNTER_MISSING", "External monotonic counter is missing.");
        var record = Read(key);
        if (record is null) return Reject("EXTERNAL_MONOTONIC_COUNTER_INVALID", "External monotonic counter is invalid.");
        if (!record.Schema.Equals("DCS_WATCHER_WINDOWS_MONOTONIC_COUNTER_V1", StringComparison.Ordinal) ||
            record.Generation < 0 || record.ObjectDigest.Length != 64 || !record.ObjectDigest.All(Uri.IsHexDigit) ||
            !DateTimeOffset.TryParse(record.UpdatedAtUtc, out _))
            return Reject("EXTERNAL_MONOTONIC_COUNTER_INVALID", "External monotonic counter schema or fields are invalid.");
        if (!record.Purpose.Equals(purpose, StringComparison.Ordinal) ||
            !record.InstanceId.Equals(instanceId, StringComparison.Ordinal) ||
            !SamePath(record.AnchorPath, anchorPath))
            return Reject("EXTERNAL_MONOTONIC_IDENTITY_MISMATCH", "External monotonic counter identity is invalid.");
        if (record.Generation > generation)
            return Reject("EXTERNAL_GENERATION_ROLLBACK", "External monotonic counter proves that the presented state was rolled back.");
        if (record.Generation < generation)
            return Reject("EXTERNAL_GENERATION_STALE", "Presented state advanced without committing its external monotonic counter.");
        if (!record.ObjectDigest.Equals(objectDigest, StringComparison.OrdinalIgnoreCase))
            return Reject("EXTERNAL_MONOTONIC_DIGEST_MISMATCH", "External monotonic counter does not bind the presented state digest.");
        return new Stage3MonotonicCounterResult(true, "OK", "External monotonic counter matches the presented state.");
    }

    internal void DeleteForOfflineTests(string purpose, string instanceId, string anchorPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        var fullAnchorPath = Path.GetFullPath(anchorPath);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullAnchorPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Offline test counters must be bound below the Windows temporary directory.");

        var keyPath = KeyPath(purpose, instanceId, fullAnchorPath);
        using var mutex = new Mutex(false, MutexName(keyPath));
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(LockTimeout); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new TimeoutException("Timed out waiting to delete an offline monotonic counter.");
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private static CounterRecord? Read(RegistryKey key)
    {
        if (key.GetValue(ValueName) is not string value) return null;
        try { return JsonSerializer.Deserialize<CounterRecord>(value); }
        catch (JsonException) { return null; }
    }

    private static string KeyPath(string purpose, string instanceId, string anchorPath)
    {
        var identity = $"{purpose}|{instanceId}|{Path.GetFullPath(anchorPath).ToUpperInvariant()}";
        var digest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(identity));
        return $@"{RegistryRoot}\{digest}";
    }

    internal static string MutexNameForDiagnostics(string purpose, string instanceId, string anchorPath) =>
        MutexName(KeyPath(purpose, instanceId, anchorPath));

    private static string MutexName(string keyPath)
    {
        var digest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(keyPath.ToUpperInvariant()));
        return $@"Local\DcsWatcherV2.Stage3.MonotonicCounter.{digest}";
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static Stage3MonotonicCounterResult Reject(string code, string message) => new(false, code, message);

    private sealed class CounterRecord
    {
        public string Schema { get; set; } = "DCS_WATCHER_WINDOWS_MONOTONIC_COUNTER_V1";
        public string Purpose { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string AnchorPath { get; set; } = string.Empty;
        public long Generation { get; set; }
        public string ObjectDigest { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }
}
