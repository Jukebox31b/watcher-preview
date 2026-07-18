using System.Text;
using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Demo;

public sealed class SafeDemoComposition : IDisposable
{
    private readonly object _sync = new();
    private readonly BranchLineageSafetyService _lineage = new();
    private readonly Stage2EnvelopeValidator _envelopeValidator = new();
    private readonly EphemeralStage2ProvenanceSigner _signer = new("demo-ephemeral-signer");
    private readonly Dictionary<string, DateTimeOffset> _firstSeenByTransaction = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedNonces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedEnvelopeHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DemoActivityRecord> _activity = [];
    private readonly List<DemoEvidenceRecord> _evidence = [];

    public SafeDemoComposition()
        : this(DemoAdapterSelection.IsolatedDefault, new AdapterRegistry())
    {
    }

    public SafeDemoComposition(DemoAdapterSelection selection, AdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(registry);
        ValidateIsolatedSelection(selection, registry);
        Selection = selection;
        Sink = new InMemoryDemoTestSink();
    }

    public DemoAdapterSelection Selection { get; }
    public InMemoryDemoTestSink Sink { get; }
    public IReadOnlyList<DemoActivityRecord> Activity => _activity;
    public IReadOnlyList<DemoEvidenceRecord> Evidence => _evidence;
    public int SigningCount { get; private set; }
    public int DeliveryAttemptCount { get; private set; }

    public DemoRunResult RunCurrentPath() => Run(DemoFixtureCatalog.CurrentPath());
    public DemoRunResult ReplayCurrentPath() => Run(DemoFixtureCatalog.CurrentPath());
    public DemoRunResult RunSiblingBranch() => Run(DemoFixtureCatalog.SiblingBranch());

    public DemoRunResult Run(DemoFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        lock (_sync)
        {
            return RunCore(fixture);
        }
    }

    private DemoRunResult RunCore(DemoFixture fixture)
    {
        var activityStart = _activity.Count;
        AddActivity(fixture, "Report", "VERIFIED", $"Loaded sanitized report {fixture.ReportName} ({fixture.ReportSha256}).");
        AddActivity(fixture, "Lineage", "CHECKING", "Checking exact parentage, ancestry, current node, and current-path membership.");

        var lineage = _lineage.Validate(fixture.Wake, fixture.Snapshot, fixture.Response);
        if (!lineage.Eligible)
        {
            var disposition = lineage.BranchDivergence
                ? DemoDispositions.RejectedBranchDivergence
                : DemoDispositions.RejectedInvalid;
            var reasonCode = lineage.BranchDivergence ? "BRANCH_DIVERGENCE" : "LINEAGE_REJECTED";
            AddActivity(fixture, "Lineage", disposition, lineage.Reason);
            return Complete(
                fixture,
                accepted: false,
                disposition,
                reasonCode,
                lineage.Reason,
                taskId: string.Empty,
                envelopeSha256: string.Empty,
                provenanceSha256: string.Empty,
                signatureCreated: false,
                deliveryAttempted: false,
                firstSeenUtc: null,
                activityStart);
        }

        AddActivity(fixture, "Lineage", "VERIFIED", "Direct parent, ancestry, current node, and onCurrentPath=true are verified.");
        var envelopeBytes = new UTF8Encoding(false, true).GetBytes(lineage.EnvelopeText);
        var envelope = _envelopeValidator.Validate(envelopeBytes);
        if (!envelope.Valid)
        {
            AddActivity(fixture, "Integrity", DemoDispositions.RejectedInvalid, envelope.Reason);
            return Complete(
                fixture,
                false,
                DemoDispositions.RejectedInvalid,
                envelope.ReasonCode,
                envelope.Reason,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                false,
                null,
                activityStart);
        }

        var envelopeSha256 = Stage2Crypto.Sha256Hex(envelopeBytes);
        AddActivity(fixture, "Integrity", "VERIFIED", $"Exact envelope bytes verified as {envelopeSha256}.");

        if (_firstSeenByTransaction.TryGetValue(fixture.Wake.TransactionId, out var firstSeenUtc) ||
            _reservedNonces.Contains(fixture.Wake.Nonce) ||
            _reservedEnvelopeHashes.Contains(envelopeSha256))
        {
            firstSeenUtc = firstSeenUtc == default ? fixture.TimestampUtc : firstSeenUtc;
            AddActivity(fixture, "Replay", DemoDispositions.RejectedReplay, "The identical transaction, nonce, or envelope was already accepted.");
            return Complete(
                fixture,
                false,
                DemoDispositions.RejectedReplay,
                "TRANSACTION_REPLAY",
                "Identical demo fixture replay rejected before signing or delivery.",
                envelope.TaskId,
                envelopeSha256,
                string.Empty,
                false,
                false,
                firstSeenUtc,
                activityStart);
        }

        _firstSeenByTransaction.Add(fixture.Wake.TransactionId, fixture.TimestampUtc);
        _reservedNonces.Add(fixture.Wake.Nonce);
        _reservedEnvelopeHashes.Add(envelopeSha256);
        AddActivity(fixture, "Replay", "RESERVED", "In-memory replay identity reserved.");

        var provenance = BuildProvenance(fixture, envelope, envelopeBytes);
        var unsignedProvenance = Stage2CanonicalJson.SerializeUnsignedProvenance(provenance);
        SigningCount++;
        provenance.SignatureOrMac = Convert.ToBase64String(_signer.Sign(unsignedProvenance));
        var provenanceSha256 = Stage2Crypto.Sha256Hex(Stage2CanonicalJson.SerializeSignedProvenance(provenance));
        AddActivity(fixture, "Provenance", "SIGNED", $"Offline demo provenance signed as {provenanceSha256}.");

        var transaction = new Stage2BoundInstructionTransactionV1
        {
            Provenance = provenance,
            EnvelopeBase64 = Convert.ToBase64String(envelopeBytes),
            ResponseMessageContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.Response.Content))
        };
        var publicKey = new Stage2PublicKeyRecord
        {
            KeyId = _signer.KeyId,
            Algorithm = Stage2InstructionProvenanceV1.AlgorithmName,
            PublicKeySpkiBase64 = Convert.ToBase64String(_signer.PublicKeySpki),
            PublicKeyFingerprintSha256 = _signer.PublicKeyFingerprintSha256,
            Status = "active",
            CreatedAtUtc = fixture.TimestampUtc.ToString("O")
        };

        DeliveryAttemptCount++;
        var receipt = Sink.Receive(
            new DemoSignedTransaction(Stage2CanonicalJson.SerializeTransaction(transaction), provenance, publicKey),
            fixture.TimestampUtc.AddSeconds(1));
        AddActivity(fixture, "Test sink", receipt.Disposition, receipt.Accepted
            ? "Verified non-actionable demo evidence accepted once."
            : $"Test sink rejected the demo transaction: {receipt.ReasonCode}.");

        return Complete(
            fixture,
            receipt.Accepted,
            receipt.Disposition,
            receipt.ReasonCode,
            receipt.Accepted ? "Valid fixture accepted once by the non-actionable test sink." : "Test sink rejected the fixture.",
            envelope.TaskId,
            envelopeSha256,
            provenanceSha256,
            signatureCreated: true,
            deliveryAttempted: true,
            fixture.TimestampUtc,
            activityStart);
    }

    public void Dispose() => _signer.Dispose();

    private Stage2InstructionProvenanceV1 BuildProvenance(
        DemoFixture fixture,
        Stage2EnvelopeValidationResult envelope,
        byte[] envelopeBytes)
    {
        var ancestry = BranchLineageSafetyService.BuildActionableAncestry(
            fixture.Snapshot,
            fixture.Snapshot.CurrentNode,
            fixture.Wake.WakeMessageId,
            fixture.Response.MessageId).ToList();
        var provenance = new Stage2InstructionProvenanceV1
        {
            TransactionId = fixture.Wake.TransactionId,
            ConversationId = fixture.Wake.ConversationId,
            WakeToken = fixture.Wake.WakeToken,
            WakeMessageId = fixture.Wake.WakeMessageId,
            WakeMessageCreatedAt = fixture.Wake.WakeCreatedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty,
            AssistantMessageId = fixture.Response.MessageId,
            AssistantMessageCreatedAt = fixture.Response.CreatedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty,
            AssistantParentMessageId = fixture.Response.ParentMessageId,
            ExpectedParentMessageId = fixture.Wake.WakeMessageId,
            CurrentNodeBeforeWake = fixture.Wake.CurrentNodeBeforeWake,
            CurrentNodeAfterResponse = fixture.Snapshot.CurrentNode,
            CurrentNodeAtCapture = fixture.Response.CurrentNodeAtCapture,
            CurrentPathRoot = ancestry.FirstOrDefault() ?? string.Empty,
            OnCurrentPath = true,
            AncestryMessageIds = ancestry,
            AncestryDigestSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", ancestry))),
            AncestryVerified = true,
            DirectParentVerified = true,
            CurrentNodeVerified = true,
            SelectedAssistantIndex = fixture.Response.SelectedAssistantIndex ?? 0,
            CaptureMethod = fixture.Response.CaptureMethod,
            FallbackBodyUsed = false,
            WholePageCaptureUsed = false,
            BackendMessageObjectVerified = true,
            BackendVerificationTimestamp = fixture.TimestampUtc.ToString("O"),
            TaskId = envelope.TaskId,
            SourceReport = envelope.SourceReport,
            EnvelopeSizeBytes = envelopeBytes.LongLength,
            EnvelopeSha256 = Stage2Crypto.Sha256Hex(envelopeBytes),
            ResponseMessageContentSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(fixture.Response.Content)),
            DestinationCodexThreadId = WatcherAdapterIds.DeliveryTestSink,
            WatcherSourceCommit = "demo-source",
            WatcherSourceTreeSha256 = HashLabel("demo-source-tree"),
            WatcherExecutableSha256 = HashLabel("demo-executable"),
            WatcherConfigurationSha256 = HashLabel("demo-configuration"),
            IssueTimeUtc = fixture.TimestampUtc.ToString("O"),
            ExpiryTimeUtc = fixture.TimestampUtc.AddMinutes(5).ToString("O"),
            Nonce = fixture.Wake.Nonce,
            SignerKeyId = _signer.KeyId,
            SignatureOrMacAlgorithm = Stage2InstructionProvenanceV1.AlgorithmName
        };
        provenance.ReplayLedgerKey = HashLabel(string.Join("|", new[]
        {
            provenance.TransactionId,
            provenance.Nonce,
            provenance.EnvelopeSha256,
            provenance.WakeMessageId,
            provenance.AssistantMessageId,
            provenance.DestinationCodexThreadId
        }));
        return provenance;
    }

    private DemoRunResult Complete(
        DemoFixture fixture,
        bool accepted,
        string disposition,
        string reasonCode,
        string message,
        string taskId,
        string envelopeSha256,
        string provenanceSha256,
        bool signatureCreated,
        bool deliveryAttempted,
        DateTimeOffset? firstSeenUtc,
        int activityStart)
    {
        var evidence = new DemoEvidenceRecord(
            fixture.FixtureId,
            fixture.TimestampUtc,
            fixture.Wake.TransactionId,
            fixture.Wake.ConversationId,
            taskId,
            fixture.Wake.WakeMessageId,
            fixture.Response.MessageId,
            fixture.Response.ParentMessageId,
            fixture.Snapshot.CurrentNode,
            fixture.Response.OnCurrentPath,
            envelopeSha256,
            provenanceSha256,
            signatureCreated ? _signer.PublicKeyFingerprintSha256 : string.Empty,
            WatcherAdapterIds.DeliveryTestSink,
            signatureCreated,
            deliveryAttempted,
            disposition,
            reasonCode,
            firstSeenUtc);
        _evidence.Add(evidence);
        return new DemoRunResult(
            accepted,
            disposition,
            reasonCode,
            message,
            evidence,
            _activity.Skip(activityStart).ToArray(),
            Sink.AcceptedCount);
    }

    private void AddActivity(DemoFixture fixture, string stage, string status, string message)
    {
        var sequence = _activity.Count + 1;
        _activity.Add(new DemoActivityRecord(
            sequence,
            fixture.TimestampUtc.AddMilliseconds(sequence),
            fixture.FixtureId,
            stage,
            status,
            message));
    }

    private static string HashLabel(string value) => Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(value));

    private static void ValidateIsolatedSelection(DemoAdapterSelection selection, AdapterRegistry registry)
    {
        var report = registry.GetRequired(selection.ReportAdapterId);
        var director = registry.GetRequired(selection.DirectorAdapterId);
        var delivery = registry.GetRequired(selection.DeliveryAdapterId);
        if (report.Role != WatcherAdapterRole.ReportSource || director.Role != WatcherAdapterRole.Director || delivery.Role != WatcherAdapterRole.Delivery ||
            !selection.Equals(DemoAdapterSelection.IsolatedDefault))
        {
            throw new InvalidOperationException(
                "DEMO_ADAPTER_ISOLATION: demo composition permits only report.demo-fixture, director.demo-fixture, and delivery.test-sink.");
        }
    }
}
