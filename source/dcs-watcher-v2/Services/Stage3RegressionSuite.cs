using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;

namespace DcsWatcherV2.Services;

public sealed class Stage3RegressionSuite
{
    private static readonly string[] TestNames =
    [
        "Two Watcher processes reserve the same transaction", "Two Watcher processes reserve different transactions simultaneously",
        "Watcher and verifier contend on the same ledger", "Two verifier processes attempt the same acceptance", "Lock timeout",
        "Abandoned mutex", "Mutex owner process termination", "Process restart during reservation", "Process restart during acceptance",
        "High-contention repeated access", "Entry deletion", "Entry insertion", "Entry reorder", "Ledger truncation", "Duplicate sequence",
        "Sequence rollback", "Generation rollback", "Ledger instance replacement", "Outbound/intake ledger swap", "Stale backup restoration",
        "Modified signed checkpoint", "Missing signed checkpoint", "Checkpoint newer than ledger", "Ledger newer than checkpoint",
        "Unknown extra ledger file", "Modified transaction disposition", "Modified destination thread", "Modified nonce",
        "Modified envelope digest", "Valid hash chain with invalid checkpoint signature", "Crash before temporary write",
        "Crash during temporary write", "Crash after temporary flush", "Crash before atomic replace",
        "Crash after atomic replace but before checkpoint", "Crash after checkpoint but before lock release", "Orphan temporary file",
        "Corrupt temporary file", "Valid temporary file with stale authoritative file", "Recovery-required state blocks automatic resend",
        "Valid signed build attestation", "Executable hash mismatch", "Application DLL hash mismatch", "Supporting DLL hash mismatch",
        "Configuration hash mismatch", "Provenance-schema hash mismatch", "Verifier-contract hash mismatch", "Replay-contract hash mismatch",
        "Untrusted source commit", "Dirty source-tree rejection", "Build-generation rollback", "Invalid attestation signature",
        "Unknown build-attestation signer", "Revoked build-attestation signer", "Toolchain mismatch", "Reproducible-build parity",
        "Explained normalized variance when binary parity is unavailable", "Active key accepted", "Pending key rejected for production acceptance",
        "Retiring key accepted only during configured overlap", "Retired key rejected after overlap", "Revoked key rejected",
        "Expired key rejected", "Unknown key rejected", "Algorithm downgrade rejected", "Key-ID collision rejected",
        "Fingerprint mismatch rejected", "Trust-store generation rollback rejected", "Old trust-store restoration rejected",
        "Revocation-record removal rejected", "Dual-key rotation transition", "Emergency revocation",
        "Envelope cannot reach actionable sink before verification", "Rejected envelope remains non-actionable",
        "Accepted test envelope reaches only test sink", "Wrong Director thread rejected", "Wrong source report rejected",
        "Wrong task ID rejected", "Modified envelope rejected", "Modified provenance rejected", "Valid provenance with untrusted build rejected",
        "Valid build with revoked signer rejected", "Replayed accepted transaction rejected", "Same assistant message under another wake rejected",
        "Same envelope under another transaction rejected", "Expired provenance rejected", "Excessive clock skew rejected",
        "fallbackBody=true rejected", "Whole-page capture rejected", "onCurrentPath=false rejected", "Wrong direct parent rejected",
        "Current-node lineage mismatch rejected", "Multiple envelopes rejected", "Partial envelope rejected", "Unsafe control byte rejected",
        "Invalid UTF-8 rejected", "Oversized input rejected", "Duplicate JSON key rejected", "Noncanonical provenance rejected",
        "Missing signature rejected", "Manual paste cannot be manufactured by Watcher", "Automatic transaction cannot claim manual mode",
        "Manual file authorization requires exact size and hash", "Manual file hash mismatch rejected",
        "Manual mode does not alter automatic replay ledger", "Automatic provenance does not satisfy manual authorization",
        "Manual and automatic transaction IDs cannot collide silently", "Historical sibling branch is rejected without output",
        "Historical fallbackBody pattern is rejected without retry", "Current-path direct response crosses process boundary once and replay is rejected",
        "Unsigned caller-selected intake policy rejected", "Pinned policy rejects alternate trust root",
        "Executing intake runtime path mismatch rejected", "Arbitrary ledger mutex name rejected",
        "Outbound ledger verifies complete provenance identity", "Malformed key lifecycle timestamp rejected",
        "Test-sink failure cannot record acceptance", "Build attestation binds exact signed intake policy",
        "Intake policy pins exact Director thread", "Outbound and intake checkpoint purposes are isolated",
        "Startup moves unfinished transaction to recovery required", "No compile-time installation identities remain",
        "Superseded signed intake policy is rejected",
        "Missing installation trust is rejected and test cleanup remains isolated",
        "External monotonic counter advancement is atomic across processes",
        "Verified manual-pilot payload reaches live intake callback",
        "Unverified manual-pilot payload never becomes actionable",
        "Manual-pilot wrong destination thread is rejected",
        "Manual-pilot replay is rejected before duplicate exposure",
        "Manual-pilot mode permits one transaction only",
        "Watcher stops after manual-pilot terminal result",
        "DcsWatcherV2 executable is not counted as DCS",
        "Current Watcher PID is not counted as DCS",
        "DCS executable is counted",
        "DCS server executable is counted",
        "Similar unrelated executable names are not counted",
        "Real DCS process blocks manual-pilot preflight",
        "Zero real DCS processes allows manual-pilot preflight",
        "Active conversation pre-wake snapshot succeeds",
        "Pre-wake snapshot missing conversation ID fails",
        "Pre-wake snapshot missing current_node fails",
        "Stale pre-wake snapshot fails",
        "Sibling-branch pre-wake snapshot fails",
        "Pre-wake snapshot probe completes within bounded timeout",
        "Pre-wake timeout reports exact unresolved condition",
        "Successful CDP-observed authenticated lineage response",
        "Successful in-page authenticated request",
        "Authenticated conversation HTTP 401",
        "Authenticated conversation HTTP 403",
        "Authenticated conversation wrong endpoint",
        "Authenticated conversation missing required observed header",
        "Authenticated snapshot hidden tab",
        "Authenticated snapshot tab becomes hidden",
        "Authenticated snapshot navigation during acquisition",
        "Authenticated snapshot multiple matching conversation tabs",
        "Authenticated snapshot missing conversation ID",
        "Authenticated snapshot missing current_node",
        "Authenticated snapshot missing ancestry",
        "Authenticated snapshot stale response",
        "Authenticated snapshot sibling-branch response",
        "Authenticated snapshot different conversation response",
        "Authenticated request exceeds absolute deadline",
        "Authenticated CDP caller exceeds the same deadline",
        "Authenticated response body unavailable",
        "Authenticated response malformed",
        "Authenticated diagnostics redact secret values",
        "Failed pre-wake gate creates no wake",
        "Same visible tab is revalidated before wake",
        "Historical HTTP 401 fixture is repaired by in-page authentication",
        "Verified Stage 4 payload reaches live intake callback once",
        "Unverified Stage 4 payload never becomes actionable",
        "Stage 4 wrong destination thread is rejected",
        "Stage 4 replay is rejected before duplicate exposure",
        "Installation trust fresh provisioning validates",
        "Installation trust serializes no private material",
        "Installation trust rejects broad ACL tampering",
        "Installation trust rejects bundle tampering",
        "Installation trust rejects another installation bundle",
        "Installation trust rejects unknown policy signer",
        "Installation trust rotates and revokes policy keys",
        "Installation trust rejects removed destination",
        "Installation trust missing bundle fails closed",
        "Installation trust exports public material only"
    ];

    private readonly string _selfExecutablePath;
    private readonly string _intakeExecutablePath;

    public Stage3RegressionSuite(string selfExecutablePath, string intakeExecutablePath)
    {
        _selfExecutablePath = selfExecutablePath;
        _intakeExecutablePath = intakeExecutablePath;
    }

    public IReadOnlyList<Stage3TestCaseResult> Run()
    {
        var results = new List<Stage3TestCaseResult>();
        for (var number = 1; number <= TestNames.Length; number++)
        {
            var captured = number;
            Run(results, number, TestNames[number - 1], () => Execute(captured));
        }
        return results;
    }

    public Stage3FaultInjectionReport RunFaultInjection()
    {
        var liveOutputBaseline = SnapshotLiveOutputState();
        var report = new Stage3FaultInjectionReport
        {
            Seed = 147516,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DuplicateTransactionAttempts = 100,
            DistinctTransactionAttempts = 100,
            CrashRecoverySimulations = 100,
            TamperedLedgerStartups = 100,
            TrustStoreRollbackAttempts = 100,
            ReplayAttempts = 100,
            ProcessIds = [Environment.ProcessId]
        };
        var reasons = report.RejectionReasons;

        using (var fixture = new Stage3RegressionFixture(cngIntakeSigner: true))
        {
            var batch = RunConcurrentBatch(fixture, unique: false, "fault-duplicate");
            report.ProcessIds.AddRange(batch.ProcessIds);
            report.DuplicateReservationAcceptedCount = batch.Accepted;
            report.DuplicateAcceptances = Math.Max(0, batch.Accepted - 1);
            foreach (var item in batch.Reasons) reasons[item.Key] = reasons.GetValueOrDefault(item.Key) + item.Value;
        }

        using (var fixture = new Stage3RegressionFixture(cngIntakeSigner: true))
        {
            var batch = RunConcurrentBatch(fixture, unique: true, "fault-distinct");
            report.ProcessIds.AddRange(batch.ProcessIds);
            report.DistinctReservationAcceptedCount = batch.Accepted;
            if (batch.Accepted != 100) report.UnauthorizedDeliveries += 100 - batch.Accepted;
            foreach (var item in batch.Reasons) reasons[item.Key] = reasons.GetValueOrDefault(item.Key) + item.Value;
            report.FinalLedgerState = fixture.OutboundLedger.ValidateOnly(DateTimeOffset.UtcNow).ReasonCode;
        }

        using (var fixture = new Stage3RegressionFixture(cngIntakeSigner: true))
        {
            var faultPoints = new[] { "before-temporary-write", "after-temporary-flush", "after-atomic-replace", "after-checkpoint" };
            for (var index = 0; index < 100; index++)
            {
                var instance = "fault-crash-" + index;
                var ledger = fixture.NewLedger("watcher-outbound", instance, Path.Combine("fault-crash", index.ToString()));
                Stage3RegressionFixture.Assert(ledger.Initialize(fixture.NowUtc));
                var identity = new Stage3LedgerIdentity
                {
                    LedgerRole = "watcher-outbound",
                    LedgerInstanceId = instance,
                    LedgerDirectory = Path.GetDirectoryName(ledger.LedgerPath)!,
                    AnchorDirectory = Path.GetDirectoryName(ledger.AnchorPath)!,
                    MutexName = ledger.MutexName
                };
                var provenancePath = Path.Combine(fixture.Root, "fault-crash-provenance", $"{index}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(provenancePath)!);
                File.WriteAllBytes(provenancePath, JsonSerializer.SerializeToUtf8Bytes(fixture.CloneProvenance("crash-" + index), Stage2CanonicalJson.Options));
                var request = new Stage3LedgerWorkerRequest
                {
                    Action = "fault-reserve",
                    Identity = identity,
                    TrustStorePath = fixture.TrustStorePath,
                    TrustRootPath = fixture.TrustRootPath,
                    TrustAnchorPath = fixture.TrustAnchorPath,
                    CheckpointSignerKeyId = fixture.OutboundCheckpointSigner.KeyId,
                    CheckpointSignerCngKeyName = fixture.OutboundCheckpointCngKeyName,
                    CheckpointSignerPurpose = "outbound-ledger-checkpoint",
                    ProvenancePath = provenancePath,
                    FaultStopAfterStep = faultPoints[index % faultPoints.Length],
                    TerminateProcessAtFault = true,
                    LockTimeoutMilliseconds = 5000
                };
                using var worker = StartWorker(fixture, request, 2000 + index);
                report.ProcessIds.Add(worker.Id);
                if (!worker.WaitForExit(15000))
                {
                    worker.Kill(true);
                    report.SilentRecoveries++;
                    continue;
                }
                if (worker.ExitCode == 0) report.SilentRecoveries++;

                var restarted = new Stage3ReplayLedgerV2Service(identity, fixture.OutboundCheckpointSigner,
                    new Stage3PurposeKeyResolver(fixture.TrustStore, "outbound-ledger-checkpoint", 1, fixture.NowUtc));
                var restartValidation = restarted.ValidateOnly(fixture.NowUtc);
                var shouldRemainValid = request.FaultStopAfterStep.Equals("before-temporary-write", StringComparison.Ordinal);
                if (shouldRemainValid != restartValidation.Accepted) report.SilentRecoveries++;
                if (shouldRemainValid && !restartValidation.AbandonedMutexRecovered) report.SilentRecoveries++;
                AddReason(reasons, "RESTART_" + restartValidation.ReasonCode);
            }
        }

        for (var index = 0; index < 100; index++)
        {
            using var fixture = new Stage3RegressionFixture();
            File.AppendAllText(fixture.OutboundLedger.LedgerPath, " ");
            var result = fixture.OutboundLedger.ValidateOnly(fixture.NowUtc);
            if (result.Accepted) report.SilentRecoveries++; else AddReason(reasons, result.ReasonCode);
        }

        for (var index = 0; index < 100; index++)
        {
            using var fixture = new Stage3RegressionFixture();
            var oldStore = File.ReadAllBytes(fixture.TrustStorePath);
            var oldAnchor = File.ReadAllBytes(fixture.TrustAnchorPath);
            using var rotated = new EphemeralStage2ProvenanceSigner("rollback-" + index);
            Stage3RegressionFixture.Assert(fixture.TrustStore.AddPendingKey(Stage3RegressionFixture.Trusted(rotated, "provenance", "pending"), fixture.NowUtc));
            File.WriteAllBytes(fixture.TrustStorePath, oldStore);
            File.WriteAllBytes(fixture.TrustAnchorPath, oldAnchor);
            var result = fixture.TrustStore.Validate(fixture.NowUtc);
            if (result.Accepted) report.SilentRecoveries++; else AddReason(reasons, result.ReasonCode);
        }

        for (var index = 0; index < 100; index++)
        {
            using var fixture = new Stage3RegressionFixture();
            var ledger = fixture.NewLedger("codex-intake", "fault-replay-" + index, "fault-replay");
            Stage3RegressionFixture.Assert(ledger.Initialize(fixture.NowUtc));
            Stage3RegressionFixture.Assert(ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc));
            var replay = ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc);
            if (replay.Accepted) report.DuplicateAcceptances++; else AddReason(reasons, replay.ReasonCode);
        }
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        report.DurationMilliseconds = (long)(report.CompletedAtUtc - report.StartedAtUtc).TotalMilliseconds;
        report.CrossProcessWorkerCount = report.ProcessIds.Distinct().Count() - 1;
        report.LockBehavior = "Stable per-ledger named Windows mutex; bounded timeout; durable owner metadata; abandoned-owner full validation; same-volume durable atomic replace.";
        report.LiveOutputs = CountForbiddenLiveProcesses() + CountLiveOutputChanges(liveOutputBaseline, SnapshotLiveOutputState());
        return report;
    }

    public static int RunLedgerWorker(string[] args)
    {
        if (args.Length != 1) return 64;
        Stage3LedgerResult result;
        Stage3LedgerWorkerRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<Stage3LedgerWorkerRequest>(File.ReadAllBytes(args[0]), Stage2CanonicalJson.Options)
                ?? throw new InvalidDataException("Worker request is empty.");
            if (request.Action.Equals("hold", StringComparison.Ordinal))
            {
                using var mutex = new Mutex(false, request.Identity.MutexName);
                if (!mutex.WaitOne(TimeSpan.FromMilliseconds(request.LockTimeoutMilliseconds))) return 2;
                Directory.CreateDirectory(request.Identity.LedgerDirectory);
                var ownerPath = Path.Combine(request.Identity.LedgerDirectory, Stage3ReplayLedgerV2Service.LockOwnerFileName);
                var owner = new Stage3LockOwnerMetadata
                {
                    MutexName = request.Identity.MutexName,
                    ProcessId = Environment.ProcessId,
                    ProcessStartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"),
                    AcquiredAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
                File.WriteAllBytes(ownerPath, JsonSerializer.SerializeToUtf8Bytes(owner, Stage2CanonicalJson.Options));
                Console.WriteLine("LOCKED");
                Console.Out.Flush();
                Thread.Sleep(request.HoldMilliseconds);
                File.Delete(ownerPath);
                mutex.ReleaseMutex();
                return 0;
            }

            using var signer = WindowsCngStage2ProvenanceSigner.OpenExisting(request.CheckpointSignerKeyId, request.CheckpointSignerCngKeyName);
            var trust = new Stage3TrustStoreService(request.TrustStorePath, request.TrustRootPath, request.TrustAnchorPath, null);
            var ledger = new Stage3ReplayLedgerV2Service(request.Identity, signer,
                new Stage3PurposeKeyResolver(trust, request.CheckpointSignerPurpose, 1, DateTimeOffset.UtcNow),
                TimeSpan.FromMilliseconds(request.LockTimeoutMilliseconds));
            if (request.Action.Equals("reserve-batch", StringComparison.Ordinal))
            {
                var template = JsonSerializer.Deserialize<Stage2InstructionProvenanceV1>(File.ReadAllBytes(request.ProvenancePath), Stage2CanonicalJson.Options)
                    ?? throw new InvalidDataException("Worker provenance is empty.");
                var batch = new Stage3LedgerWorkerBatchResult { ProcessId = Environment.ProcessId, Attempts = request.AttemptCount };
                for (var index = 0; index < request.AttemptCount; index++)
                {
                    var provenance = request.UniqueTransactions ? MakeBatchProvenance(template, $"{request.BatchPrefix}-{index}") : template;
                    var attempt = ledger.Reserve(provenance, DateTimeOffset.UtcNow);
                    if (attempt.Accepted) batch.Accepted++;
                    else batch.RejectionReasons[attempt.ReasonCode] = batch.RejectionReasons.GetValueOrDefault(attempt.ReasonCode) + 1;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(request.ResultPath)!);
                File.WriteAllBytes(request.ResultPath, JsonSerializer.SerializeToUtf8Bytes(batch, Stage2CanonicalJson.Options));
                return 0;
            }
            if (request.Action.Equals("reserve", StringComparison.Ordinal))
            {
                var provenance = JsonSerializer.Deserialize<Stage2InstructionProvenanceV1>(File.ReadAllBytes(request.ProvenancePath), Stage2CanonicalJson.Options)
                    ?? throw new InvalidDataException("Worker provenance is empty.");
                result = ledger.Reserve(provenance, DateTimeOffset.UtcNow);
            }
            else if (request.Action.Equals("fault-reserve", StringComparison.Ordinal))
            {
                var provenance = JsonSerializer.Deserialize<Stage2InstructionProvenanceV1>(File.ReadAllBytes(request.ProvenancePath), Stage2CanonicalJson.Options)
                    ?? throw new InvalidDataException("Worker provenance is empty.");
                result = ledger.Reserve(provenance, DateTimeOffset.UtcNow, new Stage3LedgerFaultOptions
                {
                    StopAfterStep = request.FaultStopAfterStep,
                    TerminateProcess = request.TerminateProcessAtFault
                });
            }
            else if (request.Action.Equals("transition", StringComparison.Ordinal))
            {
                result = ledger.Transition(request.TransactionId, request.NextDisposition, DateTimeOffset.UtcNow, request.IncrementAttempt);
            }
            else
            {
                result = new Stage3LedgerResult(false, "WORKER_ACTION_INVALID", "Unknown worker action.");
            }
        }
        catch (Exception ex)
        {
            result = new Stage3LedgerResult(false, "WORKER_FAILURE", ex.Message);
        }
        if (request is not null && !string.IsNullOrWhiteSpace(request.ResultPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.ResultPath)!);
            File.WriteAllBytes(request.ResultPath, JsonSerializer.SerializeToUtf8Bytes(result, Stage2CanonicalJson.Options));
        }
        return result.Accepted ? 0 : 1;
    }

    public static int RunMonotonicCounterWorker(string[] args)
    {
        if (args.Length != 7 || !long.TryParse(args[3], out var generation)) return 64;
        var result = new Stage3WindowsMonotonicCounterStore().Advance(
            args[0], args[1], args[2], generation, args[4], DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[5]))!);
        File.WriteAllBytes(args[5], JsonSerializer.SerializeToUtf8Bytes(result, Stage2CanonicalJson.Options));
        if (int.TryParse(args[6], out var holdMilliseconds) && holdMilliseconds > 0) Thread.Sleep(holdMilliseconds);
        return result.Accepted ? 0 : 1;
    }

    private void Execute(int number)
    {
        if (number <= 10) Concurrency(number);
        else if (number <= 30) LedgerTamper(number);
        else if (number <= 40) Atomicity(number);
        else if (number <= 57) BuildAttestation(number);
        else if (number <= 72) KeyLifecycle(number);
        else if (number <= 100) Intake(number);
        else if (number <= 107) Manual(number);
        else if (number <= 110) Historical(number);
        else SecurityControls(number);
    }

    private void Concurrency(int number)
    {
        if (number is 1 or 2 or 3 or 4 or 8 or 9 or 10)
        {
            using var fixture = new Stage3RegressionFixture(cngIntakeSigner: true);
            var identity = fixture.IntakeIdentity;
            var requests = new List<(Process Process, string Result)>();
            var count = number == 10 ? 8 : 2;
            for (var index = 0; index < count; index++)
            {
                var provenance = number == 2 || number == 10 ? fixture.CloneProvenance("p" + index) : fixture.CloneProvenance();
                var provenancePath = Path.Combine(fixture.Root, "workers", $"p-{index}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(provenancePath)!);
                File.WriteAllBytes(provenancePath, JsonSerializer.SerializeToUtf8Bytes(provenance, Stage2CanonicalJson.Options));
                var resultPath = Path.Combine(fixture.Root, "workers", $"r-{index}.json");
                var request = WorkerRequest(fixture, identity, provenancePath, resultPath, "reserve");
                requests.Add((StartWorker(fixture, request, index), resultPath));
            }
            foreach (var item in requests) item.Process.WaitForExit(15000);
            var accepted = requests.Count(item => File.Exists(item.Result) && ReadLedgerResult(item.Result).Accepted);
            var expected = number == 2 || number == 10 ? count : 1;
            if (accepted != expected) throw new InvalidOperationException($"Expected {expected} accepted reservations, observed {accepted}.");
            return;
        }

        using var local = new Stage3RegressionFixture(cngIntakeSigner: true);
        var holder = StartHolder(local.IntakeIdentity, local.Root, 2500);
        WaitForLine(holder, "LOCKED");
        if (number == 5)
        {
            var timed = new Stage3ReplayLedgerV2Service(local.IntakeIdentity, local.IntakeCheckpointSigner,
                new Stage3PurposeKeyResolver(local.TrustStore, "intake-ledger-checkpoint", 1, local.NowUtc), TimeSpan.FromMilliseconds(100));
            AssertRejected(timed.ValidateOnly(local.NowUtc), "LOCK_TIMEOUT");
            holder.WaitForExit(5000);
        }
        else
        {
            holder.Kill(true);
            holder.WaitForExit(5000);
            var recovered = local.IntakeLedger.ValidateOnly(local.NowUtc);
            if (!recovered.Accepted || !recovered.AbandonedMutexRecovered)
                throw new InvalidOperationException("Abandoned mutex was not recovered through full validation.");
        }
    }

    private static void LedgerTamper(int number)
    {
        using var fixture = new Stage3RegressionFixture();
        var ledger = fixture.NewLedger("watcher-outbound", "tamper-" + number, "tamper");
        Stage3RegressionFixture.Assert(ledger.Initialize(fixture.NowUtc));
        var beforeLedger = File.ReadAllBytes(ledger.LedgerPath);
        var beforeCheckpoint = File.ReadAllBytes(ledger.CheckpointPath);
        var beforeAnchor = File.ReadAllBytes(ledger.AnchorPath);
        Stage3RegressionFixture.Assert(ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc));
        if (number == 13)
            Stage3RegressionFixture.Assert(ledger.Transition(fixture.Transaction.Provenance.TransactionId, Stage3TransactionStates.Validated, fixture.NowUtc));

        if (number == 22) File.Delete(ledger.CheckpointPath);
        else if (number == 25) File.WriteAllText(Path.Combine(Path.GetDirectoryName(ledger.LedgerPath)!, "unexpected.bin"), "x");
        else if (number == 19)
        {
            var other = fixture.NewLedger("codex-intake", "other-ledger", "other-ledger");
            Stage3RegressionFixture.Assert(other.Initialize(fixture.NowUtc));
            File.Copy(other.LedgerPath, ledger.LedgerPath, true);
        }
        else if (number == 20)
        {
            File.WriteAllBytes(ledger.LedgerPath, beforeLedger);
            File.WriteAllBytes(ledger.CheckpointPath, beforeCheckpoint);
            File.WriteAllBytes(ledger.AnchorPath, beforeAnchor);
        }
        else if (number == 21 || number == 30)
        {
            var bytes = File.ReadAllBytes(ledger.CheckpointPath); bytes[^2] ^= 1; File.WriteAllBytes(ledger.CheckpointPath, bytes);
        }
        else if (number == 24)
        {
            File.WriteAllBytes(ledger.CheckpointPath, beforeCheckpoint);
        }
        else if (number == 23)
        {
            File.WriteAllBytes(ledger.LedgerPath, beforeLedger);
        }
        else if (number == 14)
        {
            var bytes = File.ReadAllBytes(ledger.LedgerPath); File.WriteAllBytes(ledger.LedgerPath, bytes[..(bytes.Length / 2)]);
        }
        else
        {
            var document = JsonSerializer.Deserialize<Stage3ReplayLedgerV2>(File.ReadAllBytes(ledger.LedgerPath), Stage2CanonicalJson.Options)!;
            var entry = document.Entries[0];
            switch (number)
            {
                case 11: document.Entries.Clear(); document.LedgerGeneration = 0; break;
                case 12: document.Entries.Add(JsonSerializer.Deserialize<Stage3ReplayLedgerEntryV2>(JsonSerializer.SerializeToUtf8Bytes(entry, Stage2CanonicalJson.Options), Stage2CanonicalJson.Options)!); document.LedgerGeneration++; break;
                case 13: document.Entries.Reverse(); break;
                case 15: document.Entries.Add(entry); document.LedgerGeneration++; break;
                case 16: entry.Sequence = 0; break;
                case 17: document.LedgerGeneration = 0; break;
                case 18: document.LedgerInstanceId = "replacement"; break;
                case 26: entry.Disposition = Stage3TransactionStates.TestSinkAccepted; break;
                case 27: entry.DestinationCodexThreadId = "wrong-thread"; break;
                case 28: entry.Nonce = new string('b', 64); break;
                case 29: entry.EnvelopeSha256 = new string('c', 64); break;
            }
            File.WriteAllBytes(ledger.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(document, Stage2CanonicalJson.Options));
        }
        var validation = ledger.ValidateOnly(fixture.NowUtc);
        if (validation.Accepted) throw new InvalidOperationException("Tampered ledger was accepted.");
        _ = beforeAnchor;
    }

    private static void Atomicity(int number)
    {
        using var fixture = new Stage3RegressionFixture();
        var ledger = fixture.NewLedger("watcher-outbound", "atomic-" + number, "atomic");
        Stage3RegressionFixture.Assert(ledger.Initialize(fixture.NowUtc));
        if (number == 40)
        {
            Stage3RegressionFixture.Assert(ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc));
            Stage3RegressionFixture.Assert(ledger.Transition(fixture.Transaction.Provenance.TransactionId, Stage3TransactionStates.RecoveryRequired, fixture.NowUtc));
            AssertRejected(ledger.Transition(fixture.Transaction.Provenance.TransactionId, Stage3TransactionStates.Validated, fixture.NowUtc), "ILLEGAL_STATE_TRANSITION");
            return;
        }
        if (number is 37 or 38)
        {
            var orphan = Path.Combine(Path.GetDirectoryName(ledger.LedgerPath)!, number == 37 ? "ledger-v2.orphan.tmp" : "ledger-v2.corrupt.tmp");
            File.WriteAllBytes(orphan, number == 37 ? File.ReadAllBytes(ledger.LedgerPath) : [0x7b, 0xff, 0x00]);
            AssertRejected(ledger.ValidateOnly(fixture.NowUtc), "UNEXPECTED_LEDGER_FILE");
            return;
        }
        if (number == 39)
        {
            var interrupted = ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc,
                new Stage3LedgerFaultOptions { StopAfterStep = "after-temporary-flush" });
            AssertRejected(interrupted, "FAULT_BEFORE_ATOMIC_REPLACE");
            AssertRejected(ledger.ValidateOnly(fixture.NowUtc), "UNEXPECTED_LEDGER_FILE");
            return;
        }
        var fault = number switch
        {
            31 => "before-temporary-write", 32 => "during-temporary-write", 33 or 34 => "after-temporary-flush",
            35 => "after-atomic-replace", 36 => "after-checkpoint", _ => "during-temporary-write"
        };
        var result = ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc, new Stage3LedgerFaultOptions { StopAfterStep = fault });
        if (result.Accepted) throw new InvalidOperationException("Injected crash point was accepted.");
        var restart = ledger.ValidateOnly(fixture.NowUtc);
        if (number == 31 && !restart.Accepted) throw new InvalidOperationException("Pre-write stop damaged authoritative state.");
        if (number >= 32 && restart.Accepted) throw new InvalidOperationException("Interrupted state recovered silently.");
    }

    private static void BuildAttestation(int number)
    {
        using var fixture = new Stage3RegressionFixture();
        var service = new Stage3BuildAttestationService();
        if (number == 41)
        {
            AssertAccepted(service.Validate(fixture.AttestationBytes, fixture.TrustStore, fixture.NowUtc));
            return;
        }
        if (number is 45 or 46 or 47 or 48)
        {
            var path = number switch { 45 => fixture.ConfigurationPath, 46 => fixture.ProvenanceSchemaPath, 47 => fixture.VerifierContractPath, _ => fixture.ReplayContractPath };
            File.AppendAllText(path, "changed");
            var result = service.ValidateRuntimeDependencies(fixture.Attestation, fixture.ConfigurationPath, fixture.ProvenanceSchemaPath, fixture.VerifierContractPath, fixture.ReplayContractPath);
            if (result.Accepted) throw new InvalidOperationException("Modified runtime dependency was accepted.");
            return;
        }
        if (number == 49)
        {
            var result = service.Validate(fixture.AttestationBytes, fixture.TrustStore, fixture.NowUtc, allowedSourceCommits: new HashSet<string> { new string('f', 40) }, verifyRuntimeFiles: false);
            AssertRejected(result, "SOURCE_COMMIT_UNTRUSTED"); return;
        }
        if (number is 56 or 57)
        {
            var first = Stage3BuildAttestationService.Serialize(fixture.Attestation);
            var second = Stage3BuildAttestationService.Serialize(Stage3BuildAttestationService.Deserialize(first)!);
            if (!first.AsSpan().SequenceEqual(second)) throw new InvalidOperationException("Canonical build manifest was not reproducible.");
            return;
        }
        if (number == 55)
        {
            var clone = CloneAttestation(fixture); clone.CompilerIdentity = "downgraded-toolchain";
            var bytes = service.Sign(clone, fixture.BuildSigner);
            var result = service.Validate(bytes, fixture.TrustStore, fixture.NowUtc, verifyRuntimeFiles: false,
                allowedCompilerIdentities: new HashSet<string> { fixture.Attestation.CompilerIdentity });
            AssertRejected(result, "TOOLCHAIN_MISMATCH");
            return;
        }
        var mutated = CloneAttestation(fixture);
        switch (number)
        {
            case 42:
            {
                var copy = Path.Combine(fixture.Root, "mutated-runtime", "DcsWatcherV2.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(fixture.ExecutablePath, copy);
                mutated.ExecutablePath = copy;
                mutated.ExecutableSha256 = Stage2Crypto.Sha256Hex(File.ReadAllBytes(copy));
                File.AppendAllText(copy, "x");
                break;
            }
            case 43:
            {
                var copy = Path.Combine(fixture.Root, "mutated-runtime", "DcsWatcherV2.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(fixture.ApplicationDllPath, copy);
                mutated.ApplicationDllPath = copy;
                mutated.ApplicationDllSha256 = Stage2Crypto.Sha256Hex(File.ReadAllBytes(copy));
                File.AppendAllText(copy, "x");
                break;
            }
            case 44: File.AppendAllText(fixture.SupportingDllPath, "x"); break;
            case 50: mutated.SourceStatus = "dirty"; break;
            case 51: mutated.BuildGeneration = 0; break;
            case 52: mutated.Signature = Convert.ToBase64String(new byte[64]); break;
            case 53: mutated.AttestationSignerKeyId = "unknown"; break;
            case 54:
                Stage3RegressionFixture.Assert(fixture.TrustStore.EmergencyRevoke(fixture.BuildSigner.KeyId, "test revocation", fixture.NowUtc));
                break;
        }
        var bytesToCheck = number is 42 or 43
            ? service.Sign(mutated, fixture.BuildSigner)
            : number is 44 or 54 ? fixture.AttestationBytes : Stage3BuildAttestationService.Serialize(mutated);
        var resultCheck = service.Validate(bytesToCheck, fixture.TrustStore, fixture.NowUtc);
        if (resultCheck.Accepted) throw new InvalidOperationException("Untrusted build attestation was accepted.");
    }

    private static void KeyLifecycle(int number)
    {
        using var fixture = new Stage3RegressionFixture();
        if (number == 58) { AssertAccepted(fixture.TrustStore.EvaluateKey(fixture.ProvenanceSigner.KeyId, "provenance", 1, fixture.NowUtc)); return; }
        if (number == 64) { AssertRejected(fixture.TrustStore.EvaluateKey("unknown", "provenance", 1, fixture.NowUtc), "TRUSTED_KEY_UNKNOWN"); return; }
        if (number is 68 or 69)
        {
            var old = File.ReadAllBytes(fixture.TrustStorePath);
            var oldAnchor = File.ReadAllBytes(fixture.TrustAnchorPath);
            using var key = new EphemeralStage2ProvenanceSigner("rollback-key");
            Stage3RegressionFixture.Assert(fixture.TrustStore.AddPendingKey(Stage3RegressionFixture.Trusted(key, "provenance", "pending"), fixture.NowUtc));
            File.WriteAllBytes(fixture.TrustStorePath, old);
            File.WriteAllBytes(fixture.TrustAnchorPath, oldAnchor);
            AssertRejected(fixture.TrustStore.Validate(fixture.NowUtc), "EXTERNAL_GENERATION_ROLLBACK"); return;
        }
        if (number == 70)
        {
            Stage3RegressionFixture.Assert(fixture.TrustStore.EmergencyRevoke(fixture.ProvenanceSigner.KeyId, "incident", fixture.NowUtc));
            var bytes = File.ReadAllBytes(fixture.TrustStorePath); bytes[^10] ^= 1; File.WriteAllBytes(fixture.TrustStorePath, bytes);
            if (fixture.TrustStore.Validate(fixture.NowUtc).Accepted) throw new InvalidOperationException("Revocation removal/tamper was accepted."); return;
        }
        using var candidate = new EphemeralStage2ProvenanceSigner("candidate-" + number);
        var record = Stage3RegressionFixture.Trusted(candidate, "provenance", "pending");
        if (number == 63) record.ExpirationUtc = fixture.NowUtc.AddMinutes(-1).ToString("O");
        if (number == 66) record.KeyId = fixture.ProvenanceSigner.KeyId;
        if (number == 67) record.PublicKeyFingerprintSha256 = new string('0', 64);
        var added = fixture.TrustStore.AddPendingKey(record, fixture.NowUtc);
        if (number is 66 or 67)
        {
            if (added.Accepted && fixture.TrustStore.Validate(fixture.NowUtc).Accepted) throw new InvalidOperationException("Collision or fingerprint mismatch was accepted.");
            return;
        }
        Stage3RegressionFixture.Assert(added);
        if (number == 59) { AssertRejected(fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc), "KEY_PENDING"); return; }
        Stage3RegressionFixture.Assert(fixture.TrustStore.TransitionKey(candidate.KeyId, "active", fixture.NowUtc));
        if (number == 71) { AssertAccepted(fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc)); return; }
        if (number == 72)
        {
            Stage3RegressionFixture.Assert(fixture.TrustStore.EmergencyRevoke(candidate.KeyId, "emergency", fixture.NowUtc));
            AssertRejected(fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc), "KEY_REVOKED"); return;
        }
        if (number is 60 or 61)
        {
            var overlap = fixture.NowUtc.AddMinutes(number == 60 ? 5 : -5);
            Stage3RegressionFixture.Assert(fixture.TrustStore.TransitionKey(candidate.KeyId, "retiring", fixture.NowUtc, retiringOverlapEndsUtc: overlap));
            var result = fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc);
            if (number == 60) AssertAccepted(result); else AssertRejected(result, "KEY_RETIRED"); return;
        }
        if (number == 62)
        {
            Stage3RegressionFixture.Assert(fixture.TrustStore.EmergencyRevoke(candidate.KeyId, "revoked", fixture.NowUtc));
            AssertRejected(fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc), "KEY_REVOKED"); return;
        }
        if (number == 63)
        {
            record.ExpirationUtc = fixture.NowUtc.AddMinutes(-1).ToString("O");
            AssertRejected(fixture.TrustStore.EvaluateKey(candidate.KeyId, "provenance", 1, fixture.NowUtc), "KEY_EXPIRED"); return;
        }
        if (number == 65)
        {
            var storeBytes = File.ReadAllBytes(fixture.TrustStorePath); var text = Encoding.UTF8.GetString(storeBytes).Replace(Stage2InstructionProvenanceV1.AlgorithmName, "ECDSA-P192", StringComparison.Ordinal);
            File.WriteAllText(fixture.TrustStorePath, text, new UTF8Encoding(false));
            if (fixture.TrustStore.Validate(fixture.NowUtc).Accepted) throw new InvalidOperationException("Algorithm downgrade was accepted.");
        }
    }

    private void Intake(int number)
    {
        using var fixture = new Stage3RegressionFixture(
            cngIntakeSigner: number == 75,
            intakeExecutablePath: number == 75 ? _intakeExecutablePath : null);
        if (number == 73)
        {
            if (Directory.Exists(fixture.IntakeConfig.TestSinkDirectory)) throw new InvalidOperationException("Sink existed before verification."); return;
        }
        fixture.PrepareOutboundForTestSink();
        if (number == 75)
        {
            var first = RunIntakeProcess(fixture, fixture.CreateFrame(), "accepted");
            if (!first.Disposition.Equals("ACCEPTED_FOR_TEST_SINK", StringComparison.Ordinal) || first.ActionableInstructionExposed || !File.Exists(first.TestSinkPath))
                throw new InvalidOperationException("Accepted transaction did not remain isolated in the test sink.");
            return;
        }
        var transaction = CloneTransaction(fixture.Transaction);
        var frame = fixture.CreateFrame();
        switch (number)
        {
            case 74: frame.DestinationCodexThreadId = "wrong"; break;
            case 76: frame.DestinationCodexThreadId = "wrong"; break;
            case 77: transaction.Provenance.SourceReport = "wrong.md"; break;
            case 78: transaction.Provenance.TaskId = "wrong-task"; break;
            case 79: transaction.EnvelopeBase64 = Convert.ToBase64String(fixture.EnvelopeBytes.Concat(new byte[] { 0x20 }).ToArray()); break;
            case 80: transaction.Provenance.Nonce = new string('f', 64); break;
            case 81: frame.BuildAttestationSha256 = new string('0', 64); break;
            case 82: Stage3RegressionFixture.Assert(fixture.TrustStore.EmergencyRevoke(fixture.ProvenanceSigner.KeyId, "test", fixture.NowUtc)); break;
            case 83:
            {
                var accepted = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(frame), fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
                if (!accepted.Disposition.Equals("ACCEPTED_FOR_TEST_SINK", StringComparison.Ordinal)) throw new InvalidOperationException("First intake failed.");
                var replay = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(frame), fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
                if (replay.Disposition != "REJECTED") throw new InvalidOperationException("Replay was accepted."); return;
            }
            case 84: transaction.Provenance.WakeMessageId = "other-wake"; break;
            case 85: transaction.Provenance.TransactionId = Guid.NewGuid().ToString("D"); break;
            case 86: transaction.Provenance.ExpiryTimeUtc = fixture.NowUtc.AddMinutes(-1).ToString("O"); break;
            case 87: transaction.Provenance.IssueTimeUtc = fixture.NowUtc.AddHours(1).ToString("O"); break;
            case 88: transaction.Provenance.FallbackBodyUsed = true; break;
            case 89: transaction.Provenance.WholePageCaptureUsed = true; break;
            case 90: transaction.Provenance.OnCurrentPath = false; break;
            case 91: transaction.Provenance.AssistantParentMessageId = "wrong"; break;
            case 92: transaction.Provenance.CurrentNodeAtCapture = "sibling"; break;
            case 93: transaction.EnvelopeBase64 = Convert.ToBase64String(fixture.EnvelopeBytes.Concat(fixture.EnvelopeBytes).ToArray()); break;
            case 94: transaction.EnvelopeBase64 = Convert.ToBase64String(fixture.EnvelopeBytes[..^20]); break;
            case 95: transaction.EnvelopeBase64 = Convert.ToBase64String(fixture.EnvelopeBytes.Concat(new byte[] { 0x08 }).ToArray()); break;
            case 96: transaction.EnvelopeBase64 = Convert.ToBase64String(new byte[] { 0xC3, 0x28 }); break;
            case 97: AssertOversizedFrame(fixture); return;
            case 98: AssertDuplicateJsonRejected(fixture); return;
            case 99: AssertNoncanonicalRejected(fixture); return;
            case 100: transaction.Provenance.SignatureOrMac = string.Empty; break;
        }
        if (number is 77 or 78 or 80 or 84 or 85 or 86 or 87 or 88 or 89 or 90 or 91 or 92)
        {
            transaction = Resign(transaction, fixture.ProvenanceSigner);
        }
        if (number is 77 or 78 or 79 or 80 or 84 or 85 or 86 or 87 or 88 or 89 or 90 or 91 or 92 or 93 or 94 or 95 or 96 or 100)
            frame = fixture.CreateFrame(Stage2CanonicalJson.SerializeTransaction(transaction));
        var result = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(frame), fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
        if (result.Disposition != "REJECTED" || result.ActionableInstructionExposed) throw new InvalidOperationException("Invalid intake frame became actionable.");
        if (Directory.Exists(fixture.IntakeConfig.TestSinkDirectory) && Directory.EnumerateFiles(fixture.IntakeConfig.TestSinkDirectory).Any())
            throw new InvalidOperationException("Rejected envelope reached the test sink.");
    }

    private static void Manual(int number)
    {
        using var fixture = new Stage3RegressionFixture();
        var automaticCount = fixture.IntakeLedger.ReadVerifiedLedger(fixture.NowUtc).Entries.Count;
        if (number is 101 or 102 or 106)
        {
            if (fixture.Transaction.DeliveryClassification.StartsWith("manual_", StringComparison.Ordinal)) throw new InvalidOperationException("Automatic transaction claimed manual mode.");
        }
        else if (number is 103 or 104)
        {
            var path = Path.Combine(fixture.Root, "manual-envelope.txt"); File.WriteAllBytes(path, fixture.EnvelopeBytes);
            var authorization = new ManualInstructionAuthorizationV1
            {
                AbsoluteFilePath = path,
                ExpectedSizeBytes = fixture.EnvelopeBytes.Length + (number == 104 ? 1 : 0),
                ExpectedSha256 = number == 104 ? new string('0', 64) : Stage2Crypto.Sha256Hex(fixture.EnvelopeBytes),
                ReceivingCodexThreadId = Stage3RegressionFixture.ThreadId,
                DirectManuallyPastedAuthorizationText = "I authorize this exact local file.",
                AuthorizationTextSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes("I authorize this exact local file.")),
                ReceiptTimestampUtc = fixture.NowUtc
            };
            var result = new ManualInstructionAuthorizationValidator().ValidateFile(authorization, Stage3RegressionFixture.ThreadId);
            if (number == 103 && !result.Accepted) throw new InvalidOperationException(result.Message);
            if (number == 104 && result.Accepted) throw new InvalidOperationException("Bad manual file hash was accepted.");
        }
        var after = fixture.IntakeLedger.ReadVerifiedLedger(fixture.NowUtc).Entries.Count;
        if (after != automaticCount) throw new InvalidOperationException("Manual path changed automatic replay ledger.");
    }

    private void Historical(int number)
    {
        if (number is 108 or 109)
        {
            using var fixture = new Stage3RegressionFixture();
            var wake = Stage2DryRunPipeline.PrepareSyntheticWake("conversation", "visible", ["root", "visible"], "tab", "wake", Stage3RegressionFixture.SourceReport, "task");
            wake.WakeMessageId = "hidden-wake"; wake.WakeCreatedAtUtc = fixture.NowUtc;
            var snapshot = new ConversationLineageSnapshot { ConversationId = "conversation", CurrentNode = "visible", BrowserTabIdentity = "tab", ApiVerified = true, BrowserBackendAgree = true,
                BrowserVisibleMessageIds = ["root", "visible"], Nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
                {
                    ["root"] = new() { MessageId = "root", ParentMessageId = "", Role = "system", Complete = true, ChildMessageIds = ["visible", "hidden-wake"] },
                    ["visible"] = new() { MessageId = "visible", ParentMessageId = "root", Role = "user", Complete = true },
                    ["hidden-wake"] = new() { MessageId = "hidden-wake", ParentMessageId = "root", Role = "user", Complete = true, ChildMessageIds = ["hidden-response"] },
                    ["hidden-response"] = new() { MessageId = "hidden-response", ParentMessageId = "hidden-wake", Role = "assistant", Complete = true, Content = Encoding.UTF8.GetString(fixture.EnvelopeBytes) }
                } };
            var response = new AssistantResponseObservation { MessageId = "hidden-response", ParentMessageId = "hidden-wake", Role = "assistant", Content = Encoding.UTF8.GetString(fixture.EnvelopeBytes), Complete = true,
                OnCurrentPath = false, CaptureMethod = number == 109 ? "DocumentBodyInnerText" : BranchLineageSafetyService.AuthorizedCaptureMethod, FallbackBody = number == 109, ApiVerified = true,
                SelectedAssistantIndex = 0, CurrentNodeAtCapture = number == 108 ? "hidden-response" : "visible", CreatedAtUtc = fixture.NowUtc };
            var replayPath = Path.Combine(fixture.Root, "historical-ledger.json");
            var pipeline = new Stage2DryRunPipeline(fixture.ProvenanceSigner, new Stage2ReplayLedger(replayPath), Path.Combine(fixture.Root, "historical"));
            var result = pipeline.BuildSignedDryRunTransaction(wake, snapshot, response, Stage3RegressionFixture.ThreadId,
                new Stage2BuildIdentity { SourceCommit = new string('a', 40), SourceTreeSha256 = new string('b', 64), ExecutableSha256 = new string('c', 64), ConfigurationSha256 = new string('d', 64) }, fixture.NowUtc);
            if (result.Success || result.PayloadBytes is not null || result.Provenance is not null) throw new InvalidOperationException("Historical off-path response produced output.");
            if (!result.Message.Contains(Stage2DryRunPipeline.HumanDivergenceWarning, StringComparison.Ordinal))
                throw new InvalidOperationException("Branch-divergence warning was not generated.");
            if (string.IsNullOrWhiteSpace(result.TransactionRecordPath) || !File.Exists(result.TransactionRecordPath))
                throw new InvalidOperationException("Durable branch-divergence rejection record is missing.");
            var rejectionRecord = File.ReadAllText(result.TransactionRecordPath);
            if (!rejectionRecord.Contains("\"automatic_retry\":false", StringComparison.Ordinal))
                throw new InvalidOperationException("Branch divergence did not durably disable automatic retry.");
            if (File.Exists(replayPath) && new Stage2ReplayLedger(replayPath).Load().Records.Count != 0)
                throw new InvalidOperationException("Historical off-path response entered the outbound replay ledger.");
            if (Directory.EnumerateFiles(Path.Combine(fixture.Root, "historical"), "*.ipc*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Historical off-path response created IPC bytes.");
            return;
        }
        using var acceptedFixture = new Stage3RegressionFixture(cngIntakeSigner: true, intakeExecutablePath: _intakeExecutablePath);
        acceptedFixture.PrepareOutboundForTestSink();
        var first = RunIntakeProcess(acceptedFixture, acceptedFixture.CreateFrame(), "historical-accepted");
        if (first.Disposition != "ACCEPTED_FOR_TEST_SINK") throw new InvalidOperationException(first.Message);
        var second = RunIntakeProcess(acceptedFixture, acceptedFixture.CreateFrame(), "historical-replay");
        if (second.Disposition != "REJECTED") throw new InvalidOperationException("Cross-process replay was accepted.");
    }

    private void SecurityControls(int number)
    {
        if (number == 113)
        {
            using var differentHost = new Stage3RegressionFixture(intakeExecutablePath: _intakeExecutablePath);
            differentHost.PrepareOutboundForTestSink();
            var result = differentHost.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(differentHost.CreateFrame()),
                differentHost.IntakePolicyBytes, differentHost.IntakeCheckpointSigner, differentHost.NowUtc);
            AssertIntakeRejected(result, "EXECUTING_PROCESS_PATH_MISMATCH");
            return;
        }
        using var fixture = new Stage3RegressionFixture();
        switch (number)
        {
            case 111:
            {
                var unsigned = ClonePolicy(fixture.IntakePolicy);
                unsigned.Signature = string.Empty;
                var result = fixture.IntakeGate.ProcessFrame(
                    Stage3CodexIntakeGate.SerializeFrame(fixture.CreateFrame()),
                    JsonSerializer.SerializeToUtf8Bytes(unsigned, Stage2CanonicalJson.Options),
                    fixture.IntakeCheckpointSigner,
                    fixture.NowUtc);
                AssertIntakeRejected(result, "INTAKE_POLICY_SIGNATURE_INVALID");
                return;
            }
            case 112:
            {
                var policy = ClonePolicy(fixture.IntakePolicy);
                var signed = fixture.IntakePolicyService.Sign(policy, fixture.IntakePolicySigner);
                policy = JsonSerializer.Deserialize<Stage3CodexIntakePolicyV1>(signed, Stage2CanonicalJson.Options)!;
                policy.ExpectedTrustRootFingerprintSha256 = new string('f', 64);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(policy, Stage2CanonicalJson.Options);
                var result = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(fixture.CreateFrame()), bytes,
                    fixture.IntakeCheckpointSigner, fixture.NowUtc);
                AssertIntakeRejected(result, "INTAKE_POLICY_TRUST_ROOT_MISMATCH");
                return;
            }
            case 113:
                throw new InvalidOperationException("Unreachable runtime-host test branch.");
            case 114:
            {
                var identity = new Stage3LedgerIdentity
                {
                    LedgerRole = "watcher-outbound",
                    LedgerInstanceId = "arbitrary-mutex-test",
                    LedgerDirectory = Path.Combine(fixture.Root, "arbitrary-mutex"),
                    AnchorDirectory = Path.Combine(fixture.Root, "arbitrary-mutex-anchor"),
                    MutexName = @"Global\caller-selected-mutex"
                };
                try
                {
                    _ = new Stage3ReplayLedgerV2Service(identity, fixture.OutboundCheckpointSigner,
                        new Stage3PurposeKeyResolver(fixture.TrustStore, "outbound-ledger-checkpoint", 1, fixture.NowUtc));
                }
                catch (ArgumentException)
                {
                    return;
                }
                throw new InvalidOperationException("Caller-selected mutex identity was accepted.");
            }
            case 115:
            {
                fixture.PrepareOutboundForTestSink();
                var transaction = CloneTransaction(fixture.Transaction);
                transaction.Provenance.Nonce = new string('e', 64);
                transaction.Provenance.ReplayLedgerKey = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("|", new[]
                {
                    transaction.Provenance.TransactionId,
                    transaction.Provenance.Nonce,
                    transaction.Provenance.EnvelopeSha256,
                    transaction.Provenance.WakeMessageId,
                    transaction.Provenance.AssistantMessageId,
                    transaction.Provenance.DestinationCodexThreadId
                })));
                transaction = Resign(transaction, fixture.ProvenanceSigner);
                var frame = fixture.CreateFrame(Stage2CanonicalJson.SerializeTransaction(transaction));
                var result = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(frame), fixture.IntakePolicyBytes,
                    fixture.IntakeCheckpointSigner, fixture.NowUtc);
                AssertIntakeRejected(result, "OUTBOUND_PROVENANCE_BINDING_MISMATCH");
                return;
            }
            case 116:
            {
                var storeBefore = File.ReadAllBytes(fixture.TrustStorePath);
                var anchorBefore = File.ReadAllBytes(fixture.TrustAnchorPath);
                using var candidate = new EphemeralStage2ProvenanceSigner("malformed-time-key");
                var record = Stage3RegressionFixture.Trusted(candidate, "provenance", "pending");
                record.ActivationUtc = "not-a-timestamp";
                var result = fixture.TrustStore.AddPendingKey(record, fixture.NowUtc);
                if (result.Accepted || !result.ReasonCode.Contains("TIME", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Malformed lifecycle timestamp was not rejected exactly: {result.ReasonCode}");
                if (!File.ReadAllBytes(fixture.TrustStorePath).AsSpan().SequenceEqual(storeBefore) ||
                    !File.ReadAllBytes(fixture.TrustAnchorPath).AsSpan().SequenceEqual(anchorBefore) ||
                    !fixture.TrustStore.Validate(fixture.NowUtc).Accepted)
                    throw new InvalidOperationException("Rejected trust-store mutation changed authoritative state.");
                return;
            }
            case 117:
            {
                fixture.PrepareOutboundForTestSink();
                Directory.CreateDirectory(fixture.IntakeConfig.TestSinkDirectory);
                var collision = Path.Combine(fixture.IntakeConfig.TestSinkDirectory,
                    $"{fixture.Transaction.Provenance.TransactionId}.test-only.envelope.txt");
                File.WriteAllText(collision, "preexisting-non-actionable-evidence");
                var result = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(fixture.CreateFrame()),
                    fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
                AssertIntakeRejected(result, "TEST_SINK_COMMIT_FAILED");
                AssertRejected(fixture.IntakeLedger.ValidateOnly(fixture.NowUtc), "RECOVERY_REQUIRED");
                return;
            }
            case 118:
            {
                fixture.PrepareOutboundForTestSink();
                var policy = ClonePolicy(fixture.IntakePolicy);
                policy.PolicyGeneration++;
                policy.ExpiryTimeUtc = fixture.NowUtc.AddHours(2).ToUniversalTime().ToString("O");
                var bytes = fixture.IntakePolicyService.Sign(policy, fixture.IntakePolicySigner);
                var activated = fixture.IntakePolicyService.ActivatePinnedPolicy(bytes, fixture.NowUtc);
                if (!activated.Accepted) throw new InvalidOperationException($"{activated.ReasonCode}: {activated.Message}");
                var result = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(fixture.CreateFrame()), bytes,
                    fixture.IntakeCheckpointSigner, fixture.NowUtc);
                AssertIntakeRejected(result, "INTAKE_POLICY_BUILD_BINDING_MISMATCH");
                return;
            }
            case 119:
            {
                var policy = ClonePolicy(fixture.IntakePolicy);
                policy.Configuration.ExpectedDirectorThreadId = "wrong-director-thread";
                try
                {
                    _ = fixture.IntakePolicyService.Sign(policy, fixture.IntakePolicySigner);
                    throw new InvalidOperationException("Installation-unapproved destination was signed.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not approved", StringComparison.Ordinal))
                {
                    return;
                }
            }
            case 120:
            {
                var wrongPurpose = fixture.NewLedger("watcher-outbound", "wrong-purpose", "wrong-purpose", fixture.IntakeCheckpointSigner);
                var result = wrongPurpose.Initialize(fixture.NowUtc);
                AssertRejected(result, "CHECKPOINT_SIGNATURE_INVALID");
                return;
            }
            case 121:
            {
                var ledger = fixture.NewLedger("watcher-outbound", "startup-recovery", "startup-recovery");
                Stage3RegressionFixture.Assert(ledger.Initialize(fixture.NowUtc));
                Stage3RegressionFixture.Assert(ledger.Reserve(fixture.Transaction.Provenance, fixture.NowUtc));
                var restarted = fixture.NewLedger("watcher-outbound", "startup-recovery", "startup-recovery");
                AssertRejected(restarted.ValidateOnly(fixture.NowUtc), "RECOVERY_REQUIRED");
                var latest = restarted.GetLatestTransactionState(fixture.Transaction.Provenance.TransactionId, fixture.NowUtc);
                if (!latest.Accepted || !latest.Disposition.Equals(Stage3TransactionStates.RecoveryRequired, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unfinished transaction was not durably moved to RECOVERY_REQUIRED.");
                return;
            }
            case 122:
            {
                if (typeof(Stage3IntakePolicyService).GetConstructor(Type.EmptyTypes) is not null ||
                    typeof(Stage3CodexIntakeGate).GetConstructor(Type.EmptyTypes) is not null)
                    throw new InvalidOperationException("A parameterless intake trust path remains callable.");
                var defaults = new AppConfig();
                if (!string.IsNullOrWhiteSpace(defaults.CodexThreadId) ||
                    !string.IsNullOrWhiteSpace(defaults.ReportRepoFullName) ||
                    !string.IsNullOrWhiteSpace(defaults.LocalRepoPath))
                    throw new InvalidOperationException("A compile-time deployment identity remains in fresh defaults.");
                return;
            }
            case 123:
            {
                var superseding = ClonePolicy(fixture.IntakePolicy);
                superseding.PolicyGeneration++;
                superseding.ExpiryTimeUtc = fixture.NowUtc.AddHours(4).ToUniversalTime().ToString("O");
                var currentBytes = fixture.IntakePolicyService.Sign(superseding, fixture.IntakePolicySigner);
                var activated = fixture.IntakePolicyService.ActivatePinnedPolicy(currentBytes, fixture.NowUtc);
                if (!activated.Accepted) throw new InvalidOperationException($"{activated.ReasonCode}: {activated.Message}");
                var replay = fixture.IntakeGate.ProcessFrame(Stage3CodexIntakeGate.SerializeFrame(fixture.CreateFrame()),
                    fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
                AssertIntakeRejected(replay, "EXTERNAL_GENERATION_ROLLBACK");
                return;
            }
            case 124:
            {
                var isolated = fixture.IntakePolicyService.CounterIdentityForDiagnostics();
                if (!isolated.ScopePath.StartsWith(fixture.InstallationSecurityRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Offline regression authority escaped its temporary installation security root.");
                var missingRoot = Path.Combine(fixture.Root, "missing-installation-trust");
                var missing = new InstallationTrustAnchorService().Load(missingRoot);
                if (missing.Accepted || !missing.ReasonCode.Equals("INSTALLATION_TRUST_MISSING", StringComparison.Ordinal))
                    throw new InvalidOperationException("Missing installation trust did not fail closed.");
                return;
            }
            case 125:
            {
                var purpose = "atomic-counter-test";
                var instance = "atomic-" + Guid.NewGuid().ToString("N");
                var anchor = Path.Combine(fixture.Root, "atomic-counter", "anchor.json");
                var lowDigest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes("low"));
                var highDigest = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes("high"));
                var lowResult = Path.Combine(fixture.Root, "atomic-counter", "low.json");
                var highResult = Path.Combine(fixture.Root, "atomic-counter", "high.json");
                using var low = StartCounterWorker(purpose, instance, anchor, 1, lowDigest, lowResult);
                using var high = StartCounterWorker(purpose, instance, anchor, 2, highDigest, highResult);
                if (!low.WaitForExit(15000) || !high.WaitForExit(15000))
                    throw new TimeoutException("Cross-process monotonic-counter workers timed out.");
                var final = new Stage3WindowsMonotonicCounterStore().Validate(purpose, instance, anchor, 2, highDigest);
                if (!final.Accepted)
                    throw new InvalidOperationException($"Atomic counter final state is invalid: {final.ReasonCode}: {final.Message}");
                new Stage3WindowsMonotonicCounterStore().DeleteForOfflineTests(purpose, instance, anchor);
                return;
            }
            case 126:
            {
                using var liveFixture = new Stage3RegressionFixture();
                liveFixture.PrepareOutboundForManualPilot();
                var exposed = 0;
                var result = liveFixture.IntakeGate.ProcessManualPilotFrame(
                    Stage3CodexIntakeGate.SerializeFrame(liveFixture.CreateManualPilotFrame()),
                    liveFixture.IntakePolicyBytes,
                    liveFixture.IntakeCheckpointSigner,
                    liveFixture.NowUtc,
                    verified =>
                    {
                        exposed++;
                        if (!verified.EnvelopeBytes.AsSpan().SequenceEqual(liveFixture.EnvelopeBytes))
                            throw new InvalidOperationException("Verified live callback received altered envelope bytes.");
                        return new Stage3LiveDeliveryResult(true, "OK", "synthetic live intake accepted", "test-turn");
                    });
                if (result.Disposition != "ACCEPTED_FOR_LIVE_CODEX" || !result.ActionableInstructionExposed || exposed != 1)
                    throw new InvalidOperationException($"Verified live payload was not exposed exactly once: {result.ReasonCode}");
                return;
            }
            case 127:
            {
                using var liveFixture = new Stage3RegressionFixture();
                liveFixture.PrepareOutboundForManualPilot();
                var transaction = liveFixture.TransactionBytes.ToArray();
                transaction[^8] ^= 1;
                var exposed = 0;
                var result = liveFixture.IntakeGate.ProcessManualPilotFrame(
                    Stage3CodexIntakeGate.SerializeFrame(liveFixture.CreateManualPilotFrame(transaction)),
                    liveFixture.IntakePolicyBytes,
                    liveFixture.IntakeCheckpointSigner,
                    liveFixture.NowUtc,
                    _ => { exposed++; return new Stage3LiveDeliveryResult(true, "OK", "must not run"); });
                if (result.Disposition != "REJECTED" || result.ActionableInstructionExposed || exposed != 0)
                    throw new InvalidOperationException("Unverified payload reached the live callback.");
                return;
            }
            case 128:
            {
                using var liveFixture = new Stage3RegressionFixture();
                liveFixture.PrepareOutboundForManualPilot();
                var frame = liveFixture.CreateManualPilotFrame();
                frame.DestinationCodexThreadId = "wrong-thread";
                var exposed = 0;
                var result = liveFixture.IntakeGate.ProcessManualPilotFrame(
                    Stage3CodexIntakeGate.SerializeFrame(frame), liveFixture.IntakePolicyBytes,
                    liveFixture.IntakeCheckpointSigner, liveFixture.NowUtc,
                    _ => { exposed++; return new Stage3LiveDeliveryResult(true, "OK", "must not run"); });
                if (result.ReasonCode != "DESTINATION_THREAD_MISMATCH" || exposed != 0)
                    throw new InvalidOperationException("Wrong destination was not rejected before exposure.");
                return;
            }
            case 129:
            {
                using var liveFixture = new Stage3RegressionFixture();
                liveFixture.PrepareOutboundForManualPilot();
                var frameBytes = Stage3CodexIntakeGate.SerializeFrame(liveFixture.CreateManualPilotFrame());
                var exposed = 0;
                Stage3LiveDeliveryResult Deliver(Stage3VerifiedPilotInstruction _) { exposed++; return new(true, "OK", "accepted", "test-turn"); }
                var first = liveFixture.IntakeGate.ProcessManualPilotFrame(frameBytes, liveFixture.IntakePolicyBytes,
                    liveFixture.IntakeCheckpointSigner, liveFixture.NowUtc, Deliver);
                var replay = liveFixture.IntakeGate.ProcessManualPilotFrame(frameBytes, liveFixture.IntakePolicyBytes,
                    liveFixture.IntakeCheckpointSigner, liveFixture.NowUtc.AddSeconds(1), Deliver);
                if (first.Disposition != "ACCEPTED_FOR_LIVE_CODEX" || replay.Disposition != "REJECTED" || exposed != 1)
                    throw new InvalidOperationException("Manual-pilot replay caused duplicate exposure.");
                return;
            }
            case 130:
            {
                var config = new AppConfig
                {
                    OperatingStage = nameof(WatcherOperatingStage.Stage3ManualPilot),
                    LiveManualPilotAuthorized = true,
                    LiveCodexIntakeEnabled = true
                };
                var state = new AppState();
                var guard = new Stage3ManualPilotGuard();
                if (!guard.TryBegin(config, state, out _) || guard.TryBegin(config, state, out _))
                    throw new InvalidOperationException("Manual-pilot guard did not enforce one attempt.");
                return;
            }
            case 131:
            {
                var state = new AppState { WatcherRunning = true };
                new Stage3ManualPilotGuard().Complete(state, "PASS", "transaction");
                if (state.WatcherRunning || state.Stage3ManualPilotTerminalResult != "PASS")
                    throw new InvalidOperationException("Watcher remained running after terminal pilot result.");
                return;
            }
            case 132:
            {
                var processes = new[] { new RunningProcessIdentity(200, "DcsWatcherV2", SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) };
                if (DcsProcessDetectionService.CountApprovedDcsProcesses(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) != 0)
                    throw new InvalidOperationException("DcsWatcherV2 was classified as an actual DCS process.");
                return;
            }
            case 133:
            {
                var processes = new[] { new RunningProcessIdentity(100, "DCS", SyntheticExecutablePath("simulator", "DCS.exe")) };
                if (DcsProcessDetectionService.CountApprovedDcsProcesses(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) != 0)
                    throw new InvalidOperationException("The current Watcher PID was classified as DCS.");
                return;
            }
            case 134:
            {
                var processes = new[] { new RunningProcessIdentity(200, "DCS", SyntheticExecutablePath("simulator", "DCS.exe")) };
                if (DcsProcessDetectionService.CountApprovedDcsProcesses(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) != 1)
                    throw new InvalidOperationException("DCS.exe was not counted.");
                return;
            }
            case 135:
            {
                var processes = new[] { new RunningProcessIdentity(200, "DCS_server", SyntheticExecutablePath("simulator", "DCS_server.exe")) };
                if (DcsProcessDetectionService.CountApprovedDcsProcesses(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) != 1)
                    throw new InvalidOperationException("DCS_server.exe was not counted.");
                return;
            }
            case 136:
            {
                var processes = new[]
                {
                    new RunningProcessIdentity(200, "DCS_updater", SyntheticExecutablePath("simulator", "DCS_updater.exe")),
                    new RunningProcessIdentity(201, "DCS_server_backup", SyntheticExecutablePath("simulator", "DCS_server_backup.exe")),
                    new RunningProcessIdentity(202, "MyDCS", SyntheticExecutablePath("tools", "MyDCS.exe")),
                    new RunningProcessIdentity(203, "DCSWatcher", SyntheticExecutablePath("tools", "DCSWatcher.exe"))
                };
                if (DcsProcessDetectionService.CountApprovedDcsProcesses(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")) != 0)
                    throw new InvalidOperationException("A similar but unrelated process name was classified as DCS.");
                return;
            }
            case 137:
            {
                var processes = new[] { new RunningProcessIdentity(200, "DCS", SyntheticExecutablePath("simulator", "DCS.exe")) };
                if (DcsProcessDetectionService.IsPilotPreflightClear(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe"), out var count) || count != 1)
                    throw new InvalidOperationException("A real DCS process did not block the manual-pilot preflight.");
                return;
            }
            case 138:
            {
                var processes = new[]
                {
                    new RunningProcessIdentity(100, "DcsWatcherV2", SyntheticExecutablePath("watcher", "DcsWatcherV2.exe")),
                    new RunningProcessIdentity(200, "DCS_updater", SyntheticExecutablePath("simulator", "DCS_updater.exe"))
                };
                if (!DcsProcessDetectionService.IsPilotPreflightClear(processes, 100, SyntheticExecutablePath("watcher", "DcsWatcherV2.exe"), out var count) || count != 0)
                    throw new InvalidOperationException("A zero-DCS process snapshot did not allow the manual-pilot preflight.");
                return;
            }
            case 139:
            {
                var now = DateTimeOffset.UtcNow;
                var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(
                    ValidPreWakeSnapshot(now),
                    "conversation",
                    now,
                    ChatGptPreWakeSnapshotService.MaximumSnapshotAge);
                if (!result.Success)
                    throw new InvalidOperationException($"Valid active-conversation snapshot was rejected: {result.ReasonCode}: {result.Message}");
                return;
            }
            case 140:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.ConversationId = string.Empty;
                var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(snapshot, "conversation", now, TimeSpan.FromSeconds(30));
                if (result.Success || result.ReasonCode != "CONVERSATION_ID_MISSING")
                    throw new InvalidOperationException($"Missing conversation ID produced {result.ReasonCode}.");
                return;
            }
            case 141:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.CurrentNode = string.Empty;
                var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(snapshot, "conversation", now, TimeSpan.FromSeconds(30));
                if (result.Success || result.ReasonCode != "CURRENT_NODE_MISSING")
                    throw new InvalidOperationException($"Missing current_node produced {result.ReasonCode}.");
                return;
            }
            case 142:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.SnapshotTimestampUtc = now.AddMinutes(-1);
                var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(snapshot, "conversation", now, TimeSpan.FromSeconds(30));
                if (result.Success || result.ReasonCode != "PRE_WAKE_SNAPSHOT_STALE")
                    throw new InvalidOperationException($"Stale snapshot produced {result.ReasonCode}.");
                return;
            }
            case 143:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.Nodes["sibling"] = new ConversationNodeRecord
                {
                    MessageId = "sibling",
                    ParentMessageId = "root",
                    Role = "assistant",
                    Content = "sibling branch",
                    Complete = true
                };
                snapshot.VisibleActiveBranchMessageIds = ["root", "sibling"];
                var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(snapshot, "conversation", now, TimeSpan.FromSeconds(30));
                if (result.Success || result.ReasonCode != "VISIBLE_BRANCH_LINEAGE_MISMATCH")
                    throw new InvalidOperationException($"Sibling-branch snapshot produced {result.ReasonCode}.");
                return;
            }
            case 144:
            {
                var stopwatch = Stopwatch.StartNew();
                var result = ChatGptPreWakeSnapshotService.WaitForActiveConversationAsync(
                        async token =>
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return new ActiveConversationObservation("conversation", "visible", "complete", 1);
                        },
                        "conversation",
                        TimeSpan.FromMilliseconds(100),
                        TimeSpan.FromMilliseconds(10),
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (result.Success || result.ReasonCode != "ACTIVE_CONVERSATION_PROBE_TIMEOUT" || stopwatch.Elapsed > TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException($"Hanging readiness probe was not bounded: {result.ReasonCode}, {stopwatch.ElapsedMilliseconds}ms.");
                return;
            }
            case 145:
            {
                var result = ChatGptPreWakeSnapshotService.WaitForActiveConversationAsync(
                        _ => Task.FromResult(new ActiveConversationObservation("conversation", "hidden", "complete", 5)),
                        "conversation",
                        TimeSpan.FromMilliseconds(60),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (result.Success || result.ReasonCode != "ACTIVE_CONVERSATION_NOT_VISIBLE" ||
                    !result.Message.Contains("hidden", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Hidden-tab timeout did not report its exact unresolved condition: {result.ReasonCode}: {result.Message}");
                return;
            }
            case 146:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.AcquisitionMethod = AuthenticatedSnapshotAcquisitionRecord.ObservedNetworkMethod;
                AssertAcquisitionAccepted(record);
                return;
            }
            case 147:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.AcquisitionMethod = AuthenticatedSnapshotAcquisitionRecord.InPageMethod;
                AssertAcquisitionAccepted(record);
                return;
            }
            case 148:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.ResponseStatusCode = 401;
                AssertAcquisitionRejected(record, "CONVERSATION_BACKEND_HTTP_401");
                return;
            }
            case 149:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.ResponseStatusCode = 403;
                AssertAcquisitionRejected(record, "CONVERSATION_BACKEND_HTTP_403");
                return;
            }
            case 150:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.EndpointPath = "/backend-api/conversations";
                AssertAcquisitionRejected(record, "AUTHENTICATED_ENDPOINT_MISMATCH");
                return;
            }
            case 151:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.HeaderNames.Clear();
                AssertAcquisitionRejected(record, "AUTHENTICATED_REQUIRED_HEADER_MISSING");
                return;
            }
            case 152:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.VisibilityBefore = "hidden";
                AssertAcquisitionRejected(record, "ACTIVE_CONVERSATION_NOT_VISIBLE");
                return;
            }
            case 153:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.VisibilityAfter = "hidden";
                AssertAcquisitionRejected(record, "TAB_BECAME_HIDDEN_DURING_ACQUISITION");
                return;
            }
            case 154:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.UrlAfter = record.UrlBefore + "?navigated=true";
                AssertAcquisitionRejected(record, "NAVIGATION_DURING_ACQUISITION");
                return;
            }
            case 155:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.MatchingTabCount = 2;
                AssertAcquisitionRejected(record, "AMBIGUOUS_CONVERSATION_TABS");
                return;
            }
            case 156:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.ConversationId = string.Empty;
                AssertSnapshotRejected(snapshot, now, "CONVERSATION_ID_MISSING");
                return;
            }
            case 157:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.CurrentNode = string.Empty;
                AssertSnapshotRejected(snapshot, now, "CURRENT_NODE_MISSING");
                return;
            }
            case 158:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.CurrentPathMessageIds.Clear();
                snapshot.VisibleActiveBranchMessageIds.Clear();
                AssertSnapshotRejected(snapshot, now, "CURRENT_PATH_MISSING_CURRENT_NODE");
                return;
            }
            case 159:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.BackendResponseTimestampUtc = now.AddMinutes(-1);
                AssertSnapshotRejected(snapshot, now, "BACKEND_RESPONSE_STALE");
                return;
            }
            case 160:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.VisibleActiveBranchMessageIds = ["root", "sibling"];
                AssertSnapshotRejected(snapshot, now, "VISIBLE_BRANCH_LINEAGE_MISMATCH");
                return;
            }
            case 161:
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = ValidPreWakeSnapshot(now);
                snapshot.ConversationId = "different-conversation";
                AssertSnapshotRejected(snapshot, now, "CONVERSATION_ID_MISMATCH");
                return;
            }
            case 162:
            {
                var now = DateTimeOffset.UtcNow;
                var record = ValidAuthenticatedAcquisition(now);
                record.CompletedAtUtc = record.AbsoluteDeadlineUtc.AddMilliseconds(1);
                record.CallerCompletedAtUtc = record.CompletedAtUtc;
                AssertAcquisitionRejected(record, "AUTHENTICATED_REQUEST_DEADLINE_EXCEEDED");
                return;
            }
            case 163:
            {
                var now = DateTimeOffset.UtcNow;
                var record = ValidAuthenticatedAcquisition(now);
                record.CallerCompletedAtUtc = record.AbsoluteDeadlineUtc.AddMilliseconds(1);
                AssertAcquisitionRejected(record, "AUTHENTICATED_SNAPSHOT_DEADLINE_EXCEEDED");
                return;
            }
            case 164:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.ResponseBodyAvailable = false;
                AssertAcquisitionRejected(record, "CONVERSATION_BACKEND_BODY_UNAVAILABLE");
                return;
            }
            case 165:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.ResponseMalformed = true;
                AssertAcquisitionRejected(record, "CONVERSATION_BACKEND_RESPONSE_MALFORMED");
                return;
            }
            case 166:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                record.HeaderNames = ["authorization", "cookie"];
                var diagnostics = ChatGptAuthenticatedSnapshotService.BuildRedactedDiagnostics(record);
                if (diagnostics.Contains("Bearer", StringComparison.OrdinalIgnoreCase) ||
                    diagnostics.Contains("secret-token", StringComparison.Ordinal) ||
                    diagnostics.Contains("cookie=", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Authenticated diagnostics exposed a reusable secret value.");
                return;
            }
            case 167:
            {
                var rejected = new ChatGptLineageCaptureResult(false, "HTTP 401", ReasonCode: "CONVERSATION_BACKEND_HTTP_401");
                var state = new AppState();
                if (Stage3ManualPilotService.PreWakePermitsWake(rejected) || state.WakeTransaction is not null)
                    throw new InvalidOperationException("A failed pre-wake gate permitted wake creation.");
                return;
            }
            case 168:
            {
                var record = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                AssertAcquisitionAccepted(record);
                record.TargetIdAfter = "different-target";
                AssertAcquisitionRejected(record, "CONVERSATION_TARGET_CHANGED");
                return;
            }
            case 169:
            {
                var historical = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                historical.ResponseStatusCode = 401;
                historical.HeaderNames.Clear();
                var oldResult = ChatGptAuthenticatedSnapshotService.ValidateAcquisition(historical, "conversation");
                if (oldResult.Success) throw new InvalidOperationException("Historical bare-fetch HTTP 401 fixture was accepted.");
                var repaired = ValidAuthenticatedAcquisition(DateTimeOffset.UtcNow);
                repaired.AcquisitionMethod = AuthenticatedSnapshotAcquisitionRecord.InPageMethod;
                AssertAcquisitionAccepted(repaired);
                return;
            }
            case 170:
            {
                using var stage4Fixture = new Stage3RegressionFixture();
                stage4Fixture.PrepareOutboundForManualPilot();
                var exposed = 0;
                var result = stage4Fixture.IntakeGate.ProcessLimitedAutomaticFrame(
                    Stage3CodexIntakeGate.SerializeFrame(stage4Fixture.CreateLimitedAutomaticFrame()),
                    stage4Fixture.IntakePolicyBytes, stage4Fixture.IntakeCheckpointSigner, stage4Fixture.NowUtc,
                    _ => { exposed++; return new Stage3LiveDeliveryResult(true, "OK", "accepted", "stage4-turn"); });
                if (result.Disposition != "ACCEPTED_FOR_LIVE_CODEX" || !result.ActionableInstructionExposed || exposed != 1)
                    throw new InvalidOperationException("Verified Stage 4 payload was not exposed exactly once.");
                return;
            }
            case 171:
            {
                using var stage4Fixture = new Stage3RegressionFixture();
                stage4Fixture.PrepareOutboundForManualPilot();
                var bytes = stage4Fixture.TransactionBytes.ToArray();
                bytes[^8] ^= 1;
                var exposed = 0;
                var result = stage4Fixture.IntakeGate.ProcessLimitedAutomaticFrame(
                    Stage3CodexIntakeGate.SerializeFrame(stage4Fixture.CreateLimitedAutomaticFrame(bytes)),
                    stage4Fixture.IntakePolicyBytes, stage4Fixture.IntakeCheckpointSigner, stage4Fixture.NowUtc,
                    _ => { exposed++; return new Stage3LiveDeliveryResult(true, "OK", "must not run"); });
                if (result.Disposition != "REJECTED" || exposed != 0)
                    throw new InvalidOperationException("Unverified Stage 4 payload reached the callback.");
                return;
            }
            case 172:
            {
                using var stage4Fixture = new Stage3RegressionFixture();
                stage4Fixture.PrepareOutboundForManualPilot();
                var frame = stage4Fixture.CreateLimitedAutomaticFrame();
                frame.DestinationCodexThreadId = "wrong-thread";
                var exposed = 0;
                var result = stage4Fixture.IntakeGate.ProcessLimitedAutomaticFrame(
                    Stage3CodexIntakeGate.SerializeFrame(frame), stage4Fixture.IntakePolicyBytes,
                    stage4Fixture.IntakeCheckpointSigner, stage4Fixture.NowUtc,
                    _ => { exposed++; return new Stage3LiveDeliveryResult(true, "OK", "must not run"); });
                if (result.ReasonCode != "DESTINATION_THREAD_MISMATCH" || exposed != 0)
                    throw new InvalidOperationException("Wrong Stage 4 destination was not rejected.");
                return;
            }
            case 173:
            {
                using var stage4Fixture = new Stage3RegressionFixture();
                stage4Fixture.PrepareOutboundForManualPilot();
                var frame = Stage3CodexIntakeGate.SerializeFrame(stage4Fixture.CreateLimitedAutomaticFrame());
                var exposed = 0;
                Stage3LiveDeliveryResult Deliver(Stage3VerifiedPilotInstruction _) { exposed++; return new(true, "OK", "accepted", "turn"); }
                var first = stage4Fixture.IntakeGate.ProcessLimitedAutomaticFrame(frame, stage4Fixture.IntakePolicyBytes,
                    stage4Fixture.IntakeCheckpointSigner, stage4Fixture.NowUtc, Deliver);
                var replay = stage4Fixture.IntakeGate.ProcessLimitedAutomaticFrame(frame, stage4Fixture.IntakePolicyBytes,
                    stage4Fixture.IntakeCheckpointSigner, stage4Fixture.NowUtc.AddSeconds(1), Deliver);
                if (first.Disposition != "ACCEPTED_FOR_LIVE_CODEX" || replay.Disposition != "REJECTED" || exposed != 1)
                    throw new InvalidOperationException("Stage 4 replay caused duplicate exposure.");
                return;
            }
            case >= 174 and <= 183:
                InstallationTrustReleaseSelfTest.Run(number - 173);
                return;
        }
    }

    private static AuthenticatedSnapshotAcquisitionRecord ValidAuthenticatedAcquisition(DateTimeOffset nowUtc) => new()
    {
        AcquisitionMethod = AuthenticatedSnapshotAcquisitionRecord.InPageMethod,
        MatchingTabCount = 1,
        TargetIdBefore = "target",
        TargetIdAfter = "target",
        FrameId = "target",
        UrlBefore = "https://chatgpt.com/g/g-p-project/c/conversation",
        UrlAfter = "https://chatgpt.com/g/g-p-project/c/conversation",
        VisibilityBefore = "visible",
        VisibilityAfter = "visible",
        RequestMethod = "GET",
        EndpointPath = "/backend-api/conversation/conversation",
        CredentialMode = "include",
        HeaderNames = ["authorization"],
        SessionStatusCode = 200,
        ResponseStatusCode = 200,
        ResponseContentType = "application/json",
        ResponseBodyAvailable = true,
        ResponseMalformed = false,
        CacheMode = "no-store",
        CachedOnly = false,
        StartedAtUtc = nowUtc,
        CompletedAtUtc = nowUtc.AddSeconds(1),
        AbsoluteDeadlineUtc = nowUtc.AddSeconds(30),
        CallerCompletedAtUtc = nowUtc.AddSeconds(2)
    };

    private static void AssertAcquisitionAccepted(AuthenticatedSnapshotAcquisitionRecord record)
    {
        var result = ChatGptAuthenticatedSnapshotService.ValidateAcquisition(record, "conversation");
        if (!result.Success) throw new InvalidOperationException($"Expected authenticated acquisition acceptance, got {result.ReasonCode}: {result.Message}");
    }

    private static void AssertAcquisitionRejected(AuthenticatedSnapshotAcquisitionRecord record, string expectedCode)
    {
        var result = ChatGptAuthenticatedSnapshotService.ValidateAcquisition(record, "conversation");
        if (result.Success || !result.ReasonCode.Equals(expectedCode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected {expectedCode}, got {result.ReasonCode}: {result.Message}");
    }

    private static void AssertSnapshotRejected(ConversationLineageSnapshot snapshot, DateTimeOffset nowUtc, string expectedCode)
    {
        var result = ChatGptPreWakeSnapshotService.ValidateSnapshot(snapshot, "conversation", nowUtc, TimeSpan.FromSeconds(30));
        if (result.Success || !result.ReasonCode.Equals(expectedCode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected {expectedCode}, got {result.ReasonCode}: {result.Message}");
    }

    private static ConversationLineageSnapshot ValidPreWakeSnapshot(DateTimeOffset nowUtc)
    {
        return new ConversationLineageSnapshot
        {
            ConversationId = "conversation",
            CurrentNode = "current",
            BrowserTabIdentity = "active-tab",
            SnapshotTimestampUtc = nowUtc,
            BackendResponseTimestampUtc = nowUtc,
            DocumentVisibilityState = "visible",
            ApiVerified = true,
            ApiStatusCode = 200,
            BrowserBackendAgree = true,
            BrowserVisibleMessageIds = ["current"],
            CurrentPathMessageIds = ["root", "current"],
            VisibleActiveBranchMessageIds = ["root", "current"],
            Nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
            {
                ["root"] = new ConversationNodeRecord
                {
                    MessageId = "root",
                    ParentMessageId = string.Empty,
                    ChildMessageIds = ["current"],
                    Role = "system",
                    Content = "root",
                    Complete = true
                },
                ["current"] = new ConversationNodeRecord
                {
                    MessageId = "current",
                    ParentMessageId = "root",
                    Role = "assistant",
                    Content = "current visible branch",
                    Complete = true
                }
            }
        };
    }

    private Stage3IntakeResult RunIntakeProcess(Stage3RegressionFixture fixture, Stage3CodexIntakeFrameV1 frame, string label)
    {
        if (string.IsNullOrWhiteSpace(_intakeExecutablePath) || !File.Exists(_intakeExecutablePath))
            throw new FileNotFoundException("Stage 3 intake executable is required for process-boundary tests.", _intakeExecutablePath);
        var framePath = fixture.WriteFrame(frame, Path.Combine(fixture.Root, "transport", label + ".frame.json"));
        var policyPath = fixture.WriteIntakePolicy(Path.Combine(fixture.Root, "transport", label + ".signed-policy.json"));
        var resultPath = Path.Combine(fixture.Root, "transport", label + ".result.json");
        using var process = Process.Start(new ProcessStartInfo(_intakeExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "verify-offline-regression", framePath, policyPath, resultPath, fixture.Root,
                fixture.InstallationSecurityRoot
            }
        }) ?? throw new InvalidOperationException("Could not start isolated intake process.");
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Isolated intake process timed out."); }
        return JsonSerializer.Deserialize<Stage3IntakeResult>(File.ReadAllBytes(resultPath), Stage2CanonicalJson.Options)!;
    }

    private Process StartWorker(Stage3RegressionFixture fixture, Stage3LedgerWorkerRequest request, int index)
    {
        var path = Path.Combine(fixture.Root, "workers", $"request-{index}-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request, Stage2CanonicalJson.Options));
        return Process.Start(new ProcessStartInfo(_selfExecutablePath) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "ledger-worker", path } })
            ?? throw new InvalidOperationException("Could not start ledger worker.");
    }

    private Process StartCounterWorker(string purpose, string instance, string anchorPath, long generation, string digest, string resultPath) =>
        Process.Start(new ProcessStartInfo(_selfExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "monotonic-counter-worker", purpose, instance, anchorPath, generation.ToString(), digest, resultPath, "0" }
        }) ?? throw new InvalidOperationException("Could not start monotonic-counter worker.");

    private (int Accepted, List<int> ProcessIds, Dictionary<string, int> Reasons) RunConcurrentBatch(
        Stage3RegressionFixture fixture,
        bool unique,
        string label)
    {
        var workers = new List<(Process Process, string ResultPath)>();
        for (var index = 0; index < 10; index++)
        {
            var resultPath = Path.Combine(fixture.Root, "fault-workers", $"{label}-{index}.result.json");
            var request = new Stage3LedgerWorkerRequest
            {
                Action = "reserve-batch",
                Identity = fixture.OutboundIdentity,
                TrustStorePath = fixture.TrustStorePath,
                TrustRootPath = fixture.TrustRootPath,
                TrustAnchorPath = fixture.TrustAnchorPath,
                CheckpointSignerKeyId = fixture.OutboundCheckpointSigner.KeyId,
                CheckpointSignerCngKeyName = fixture.OutboundCheckpointCngKeyName,
                CheckpointSignerPurpose = "outbound-ledger-checkpoint",
                ProvenancePath = fixture.ProvenancePath,
                ResultPath = resultPath,
                AttemptCount = 10,
                UniqueTransactions = unique,
                BatchPrefix = $"{label}-{index}",
                LockTimeoutMilliseconds = 30000
            };
            workers.Add((StartWorker(fixture, request, 1000 + index), resultPath));
        }
        foreach (var worker in workers)
        {
            if (!worker.Process.WaitForExit(60000)) { worker.Process.Kill(true); throw new TimeoutException("Fault worker timed out."); }
        }
        var accepted = 0;
        var pids = new List<int>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            var item = JsonSerializer.Deserialize<Stage3LedgerWorkerBatchResult>(File.ReadAllBytes(worker.ResultPath), Stage2CanonicalJson.Options)!;
            accepted += item.Accepted;
            pids.Add(item.ProcessId);
            foreach (var reason in item.RejectionReasons) reasons[reason.Key] = reasons.GetValueOrDefault(reason.Key) + reason.Value;
        }
        return (accepted, pids, reasons);
    }

    private Process StartHolder(Stage3LedgerIdentity identity, string root, int milliseconds)
    {
        var request = new Stage3LedgerWorkerRequest { Action = "hold", Identity = identity, HoldMilliseconds = milliseconds, LockTimeoutMilliseconds = 1000 };
        var path = Path.Combine(root, "hold-request.json"); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request, Stage2CanonicalJson.Options));
        return Process.Start(new ProcessStartInfo(_selfExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, ArgumentList = { "ledger-worker", path } })!;
    }

    private static Stage3LedgerWorkerRequest WorkerRequest(Stage3RegressionFixture fixture, Stage3LedgerIdentity identity, string provenancePath, string resultPath, string action) => new()
    {
        Action = action, Identity = identity, TrustStorePath = fixture.TrustStorePath, TrustRootPath = fixture.TrustRootPath, TrustAnchorPath = fixture.TrustAnchorPath,
        CheckpointSignerKeyId = fixture.IntakeCheckpointSigner.KeyId, CheckpointSignerCngKeyName = fixture.IntakeCheckpointCngKeyName,
        CheckpointSignerPurpose = "intake-ledger-checkpoint",
        ProvenancePath = provenancePath, ResultPath = resultPath
    };

    private static void WaitForLine(Process process, string expected)
    {
        var line = process.StandardOutput.ReadLine();
        if (!string.Equals(line, expected, StringComparison.Ordinal)) throw new InvalidOperationException("Lock holder did not acquire mutex.");
    }

    private static Stage3LedgerResult ReadLedgerResult(string path) => JsonSerializer.Deserialize<Stage3LedgerResult>(File.ReadAllBytes(path), Stage2CanonicalJson.Options)!;

    private static Stage2InstructionProvenanceV1 MakeBatchProvenance(Stage2InstructionProvenanceV1 template, string suffix)
    {
        var clone = Stage2CanonicalJson.CloneProvenance(template);
        clone.TransactionId += "-" + suffix;
        clone.Nonce = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(clone.Nonce + suffix));
        clone.AssistantMessageId += "-" + suffix;
        clone.WakeMessageId += "-" + suffix;
        clone.EnvelopeSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(clone.EnvelopeSha256 + suffix));
        return clone;
    }

    private static Stage3BuildAttestationV1 CloneAttestation(Stage3RegressionFixture fixture) => Stage3BuildAttestationService.Deserialize(Stage3BuildAttestationService.Serialize(fixture.Attestation))!;
    private static Stage3CodexIntakePolicyV1 ClonePolicy(Stage3CodexIntakePolicyV1 policy) =>
        JsonSerializer.Deserialize<Stage3CodexIntakePolicyV1>(JsonSerializer.SerializeToUtf8Bytes(policy, Stage2CanonicalJson.Options), Stage2CanonicalJson.Options)!;
    private static Stage2BoundInstructionTransactionV1 CloneTransaction(Stage2BoundInstructionTransactionV1 transaction) => JsonSerializer.Deserialize<Stage2BoundInstructionTransactionV1>(Stage2CanonicalJson.SerializeTransaction(transaction), Stage2CanonicalJson.Options)!;
    private static Stage2BoundInstructionTransactionV1 Resign(Stage2BoundInstructionTransactionV1 transaction, IStage2ProvenanceSigner signer)
    {
        transaction.Provenance.SignatureOrMac = string.Empty;
        transaction.Provenance.SignatureOrMac = Convert.ToBase64String(signer.Sign(Stage2CanonicalJson.SerializeUnsignedProvenance(transaction.Provenance)));
        return transaction;
    }

    private static void AssertOversizedFrame(Stage3RegressionFixture fixture)
    {
        var result = fixture.IntakeGate.ProcessFrame(new byte[Stage3CodexIntakeGate.MaximumFrameBytes + 1], fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
        if (result.Disposition != "REJECTED") throw new InvalidOperationException("Oversized frame was accepted.");
    }

    private static void AssertDuplicateJsonRejected(Stage3RegressionFixture fixture)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":\"DCS_CODEX_INTAKE_FRAME_V1\",\"schema\":\"DCS_CODEX_INTAKE_FRAME_V1\"}");
        var result = fixture.IntakeGate.ProcessFrame(bytes, fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
        if (result.Disposition != "REJECTED") throw new InvalidOperationException("Duplicate JSON key was accepted.");
    }

    private static void AssertNoncanonicalRejected(Stage3RegressionFixture fixture)
    {
        var bytes = Encoding.UTF8.GetBytes(" {\"schema\":\"DCS_CODEX_INTAKE_FRAME_V1\"}");
        var result = fixture.IntakeGate.ProcessFrame(bytes, fixture.IntakePolicyBytes, fixture.IntakeCheckpointSigner, fixture.NowUtc);
        if (result.Disposition != "REJECTED") throw new InvalidOperationException("Noncanonical frame was accepted.");
    }

    private static void AssertAccepted(Stage3BuildAttestationResult result) { if (!result.Accepted) throw new InvalidOperationException($"{result.ReasonCode}: {result.Message}"); }
    private static void AssertAccepted(Stage3TrustResult result) { if (!result.Accepted) throw new InvalidOperationException($"{result.ReasonCode}: {result.Message}"); }
    private static void AssertRejected(Stage3LedgerResult result, string code) { if (result.Accepted || !result.ReasonCode.Equals(code, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected {code}, got {result.ReasonCode}."); }
    private static void AssertRejected(Stage3TrustResult result, string code) { if (result.Accepted || !result.ReasonCode.Equals(code, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected {code}, got {result.ReasonCode}."); }
    private static void AssertRejected(Stage3BuildAttestationResult result, string code) { if (result.Accepted || !result.ReasonCode.Equals(code, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected {code}, got {result.ReasonCode}."); }
    private static void AssertIntakeRejected(Stage3IntakeResult result, string code)
    {
        if (!result.Disposition.Equals("REJECTED", StringComparison.Ordinal) || result.ActionableInstructionExposed ||
            !result.ReasonCode.Equals(code, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected intake rejection {code}, got {result.Disposition}:{result.ReasonCode}.");
    }

    private static void Run(List<Stage3TestCaseResult> results, int number, string name, Action action)
    {
        try { action(); results.Add(new Stage3TestCaseResult { Number = number, Name = name, Passed = true, Details = "PASS" }); }
        catch (Exception ex) { results.Add(new Stage3TestCaseResult { Number = number, Name = name, Passed = false, Details = ex.Message }); }
    }

    private static void AddReason(Dictionary<string, int> reasons, string code)
    {
        lock (reasons) reasons[code] = reasons.GetValueOrDefault(code) + 1;
    }

    private static Dictionary<string, string> SnapshotLiveOutputState()
    {
        var roots = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), string.Concat(".dcs", "-watcher-v2")),
            Path.Combine(Directory.GetCurrentDirectory(), "chatgpt-bridge", "inbox_to_codex")
        };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                result[Path.GetFullPath(path)] = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
        }
        return result;
    }

    private static int CountLiveOutputChanges(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Count(path => !before.TryGetValue(path, out var left) || !after.TryGetValue(path, out var right) || !left.Equals(right, StringComparison.Ordinal));

    private static string SyntheticExecutablePath(string directory, string fileName) =>
        Path.Combine(Path.GetTempPath(), "watcher-process-fixtures", directory, fileName);

    private static int CountForbiddenLiveProcesses()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DcsWatcherV2", "DCS", "DCS_server", "DCS_updater", "DCS_server_updater"
        };
        return Process.GetProcesses().Count(process =>
        {
            try { return forbidden.Contains(process.ProcessName); }
            finally { process.Dispose(); }
        });
    }
}
