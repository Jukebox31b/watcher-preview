using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Demo;

public sealed class InMemoryDemoTestSink
{
    private readonly object _sync = new();
    private readonly HashSet<string> _acceptedTransactions = new(StringComparer.Ordinal);
    private readonly List<DemoSinkReceipt> _receipts = [];

    public int ReceiveCount { get; private set; }
    public int AcceptedCount => _receipts.Count(receipt => receipt.Accepted);
    public IReadOnlyList<DemoSinkReceipt> Receipts => _receipts;

    public DemoSinkReceipt Receive(DemoSignedTransaction signedTransaction, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(signedTransaction);
        lock (_sync)
        {
            return ReceiveCore(signedTransaction, nowUtc);
        }
    }

    private DemoSinkReceipt ReceiveCore(DemoSignedTransaction signedTransaction, DateTimeOffset nowUtc)
    {
        ReceiveCount++;

        var parsed = Stage2CanonicalJson.ParseTransaction(signedTransaction.PayloadBytes);
        if (!parsed.Success || parsed.Transaction is null)
        {
            return Record(false, DemoDispositions.RejectedInvalid, parsed.ReasonCode, string.Empty, string.Empty, nowUtc);
        }

        var provenance = parsed.Transaction.Provenance;
        if (!provenance.DestinationCodexThreadId.Equals(WatcherAdapterIds.DeliveryTestSink, StringComparison.Ordinal))
        {
            return Record(false, DemoDispositions.RejectedInvalid, "DEMO_DESTINATION_REJECTED", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
        }
        if (!provenance.SignerKeyId.Equals(signedTransaction.PublicKey.KeyId, StringComparison.Ordinal) ||
            !signedTransaction.PublicKey.Status.Equals("active", StringComparison.Ordinal))
        {
            return Record(false, DemoDispositions.RejectedInvalid, "DEMO_SIGNER_BINDING_INVALID", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
        }

        byte[] envelopeBytes;
        byte[] signature;
        try
        {
            envelopeBytes = Convert.FromBase64String(parsed.Transaction.EnvelopeBase64);
            signature = Convert.FromBase64String(provenance.SignatureOrMac);
        }
        catch (FormatException)
        {
            return Record(false, DemoDispositions.RejectedInvalid, "DEMO_ENCODING_INVALID", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
        }

        if (!Stage2Crypto.Sha256Hex(envelopeBytes).Equals(provenance.EnvelopeSha256, StringComparison.OrdinalIgnoreCase) ||
            !Stage2Crypto.Verify(signedTransaction.PublicKey, Stage2CanonicalJson.SerializeUnsignedProvenance(provenance), signature))
        {
            return Record(false, DemoDispositions.RejectedInvalid, "DEMO_PROVENANCE_INVALID", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
        }

        if (!_acceptedTransactions.Add(provenance.TransactionId))
        {
            return Record(false, DemoDispositions.RejectedReplay, "TRANSACTION_REPLAY", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
        }

        return Record(true, DemoDispositions.AcceptedOnce, "OK", provenance.TransactionId, provenance.EnvelopeSha256, nowUtc);
    }

    private DemoSinkReceipt Record(
        bool accepted,
        string disposition,
        string reasonCode,
        string transactionId,
        string envelopeSha256,
        DateTimeOffset timestampUtc)
    {
        var receipt = new DemoSinkReceipt(
            accepted,
            disposition,
            reasonCode,
            transactionId,
            envelopeSha256,
            timestampUtc,
            NonActionable: true);
        _receipts.Add(receipt);
        return receipt;
    }
}
