using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record Stage2ReplayReservationResult(bool Accepted, string ReasonCode, string Message);

public sealed class Stage2ReplayLedger
{
    private readonly string _path;
    private readonly object _sync = new();

    public Stage2ReplayLedger(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public Stage2ReplayReservationResult Reserve(Stage2InstructionProvenanceV1 provenance, string disposition)
    {
        lock (_sync)
        {
            var ledger = Load();
            var duplicate = FindConflict(ledger, provenance);
            if (duplicate is not null)
            {
                return duplicate;
            }

            ledger.Records.Add(new Stage2ReplayLedgerRecord
            {
                TransactionId = provenance.TransactionId,
                Nonce = provenance.Nonce,
                EnvelopeSha256 = provenance.EnvelopeSha256,
                WakeMessageId = provenance.WakeMessageId,
                AssistantMessageId = provenance.AssistantMessageId,
                DestinationCodexThreadId = provenance.DestinationCodexThreadId,
                FirstSeenTimestampUtc = provenance.IssueTimeUtc,
                Disposition = disposition,
                DeliveryAttemptCount = 0
            });
            Save(ledger);
            return new Stage2ReplayReservationResult(true, "OK", "Replay identity reserved.");
        }
    }

    public Stage2ReplayReservationResult MarkAttempt(string transactionId, string disposition)
    {
        lock (_sync)
        {
            var ledger = Load();
            var record = ledger.Records.SingleOrDefault(item => item.TransactionId.Equals(transactionId, StringComparison.Ordinal));
            if (record is null)
            {
                return new Stage2ReplayReservationResult(false, "TRANSACTION_UNKNOWN", "Transaction has not been reserved.");
            }

            if (record.Disposition.Equals("accepted", StringComparison.Ordinal))
            {
                return new Stage2ReplayReservationResult(false, "ACCEPTED_TRANSACTION_REPLAY", "An accepted transaction cannot be attempted again.");
            }

            record.DeliveryAttemptCount++;
            record.Disposition = disposition;
            Save(ledger);
            return new Stage2ReplayReservationResult(true, "OK", "Replay ledger disposition updated.");
        }
    }

    public Stage2ReplayLedgerDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new Stage2ReplayLedgerDocument();
        }

        var bytes = File.ReadAllBytes(_path);
        return JsonSerializer.Deserialize<Stage2ReplayLedgerDocument>(bytes, Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("Replay ledger is empty.");
    }

    private static Stage2ReplayReservationResult? FindConflict(
        Stage2ReplayLedgerDocument ledger,
        Stage2InstructionProvenanceV1 provenance)
    {
        foreach (var existing in ledger.Records)
        {
            if (existing.TransactionId.Equals(provenance.TransactionId, StringComparison.Ordinal))
            {
                return Reject("TRANSACTION_REPLAY", "Transaction ID has already been observed.");
            }

            if (existing.Nonce.Equals(provenance.Nonce, StringComparison.Ordinal))
            {
                return Reject("NONCE_REPLAY", "Nonce has already been observed.");
            }

            if (existing.AssistantMessageId.Equals(provenance.AssistantMessageId, StringComparison.Ordinal) &&
                !existing.WakeMessageId.Equals(provenance.WakeMessageId, StringComparison.Ordinal))
            {
                return Reject("ASSISTANT_RESPONSE_REPLAY", "Assistant response was already bound to another wake.");
            }

            if (existing.EnvelopeSha256.Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (!existing.DestinationCodexThreadId.Equals(provenance.DestinationCodexThreadId, StringComparison.Ordinal))
                {
                    return Reject("DESTINATION_REPLAY", "Envelope was already bound to another destination thread.");
                }

                return Reject("ENVELOPE_REPLAY", "Envelope was already observed under another transaction.");
            }
        }

        return null;
    }

    private void Save(Stage2ReplayLedgerDocument ledger)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, Stage2CanonicalJson.Options);
        Stage2AtomicFile.WriteAllBytes(_path, bytes);
    }

    private static Stage2ReplayReservationResult Reject(string code, string message) => new(false, code, message);
}
