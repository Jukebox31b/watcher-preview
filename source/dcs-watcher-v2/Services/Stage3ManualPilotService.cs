using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3ManualPilotService
{
    public const string PilotSourceReport = "WATCHER-STAGE3-MANUAL-PILOT-20260717.md";
    public const string PilotInstruction = "Confirm receipt of this provenance-authenticated Watcher pilot instruction. Report the transaction ID, envelope SHA-256, provenance SHA-256, signer fingerprint, destination Codex thread, and acceptance result. Do not modify files, use Git, launch DCS, delegate work, or begin another task.";

    public async Task<Stage3ManualPilotResult> RunAsync(
        string resultPath,
        string intakeExecutablePath,
        AppConfig config,
        InstallationTrustContext trustContext)
    {
        resultPath = Path.GetFullPath(resultPath);
        intakeExecutablePath = Path.GetFullPath(intakeExecutablePath);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(trustContext);
        var configService = new ConfigService(config.InstallationRoot);
        var stateService = new StateService(configService);
        var state = stateService.Load(config);
        var log = new LogService(configService);
        log.Initialize(config);
        var guard = new Stage3ManualPilotGuard();
        var result = new Stage3ManualPilotResult { DestinationCodexThreadId = config.CodexThreadId };
        WatcherOrchestrator? orchestrator = null;

        try
        {
            if (!File.Exists(intakeExecutablePath))
                return Fail(result, "INTAKE_EXECUTABLE_MISSING", $"The certified intake executable is missing: {intakeExecutablePath}");
            if (!config.RuntimeComposedFromProfile || string.IsNullOrWhiteSpace(config.CodexThreadId) ||
                !trustContext.IsDestinationApproved(config.CodexThreadId))
                return Fail(result, "DESTINATION_THREAD_MISMATCH", "The runtime destination is not approved by installation trust.");
            if (DcsProcessDetectionService.CountRunningDcsProcesses() != 0)
                return Fail(result, "DCS_PROCESS_PRESENT", "A DCS-family process is running before the pilot.");
            if (!guard.TryBegin(config, state, out var guardReason))
                return Fail(result, "PILOT_NOT_AUTHORIZED", guardReason);

            state.OperatingStage = config.OperatingStage;
            stateService.Save(config, state);
            log.Info("One-shot Stage 3 manual pilot reserved. Automatic chaining remains disabled.", "Stage3Pilot");

            var branchGuard = new BranchGuardService();
            var ledger = new LedgerService(configService);
            var chat = new ChatGptDirectorBridge();
            orchestrator = new WatcherOrchestrator(
                config, state, configService, stateService, log, new GitHubReportPoller(),
                new DirectorReportPublishService(), chat, new ChatGptWakePromptBuilder(), new GitPullService(),
                new ChatGptEnvelopeCapture(), new CodexDirectorBridge(branchGuard, ledger), ledger, branchGuard);

            var preWake = await chat.GetCurrentLineageSnapshotAsync(config, CancellationToken.None, log);
            result.SnapshotDurationMilliseconds = preWake.DurationMilliseconds;
            if (preWake.Snapshot is not null)
            {
                result.ConversationId = preWake.Snapshot.ConversationId;
                result.CurrentNode = preWake.Snapshot.CurrentNode;
                result.SnapshotTimestampUtc = preWake.Snapshot.SnapshotTimestampUtc;
                result.CurrentPathMessageIds = [.. preWake.Snapshot.CurrentPathMessageIds];
                result.VisibleActiveBranchMessageIds = [.. preWake.Snapshot.VisibleActiveBranchMessageIds];
            }
            if (!PreWakePermitsWake(preWake))
                return Fail(
                    result,
                    string.IsNullOrWhiteSpace(preWake.ReasonCode) ? "PRE_WAKE_LINEAGE_REJECTED" : preWake.ReasonCode,
                    preWake.Message);

            var taskId = $"WATCHER-STAGE3-PILOT-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            var now = DateTimeOffset.UtcNow;
            var reportContent = Encoding.UTF8.GetBytes(PilotSourceReport);
            var report = new ReportCandidate(
                PilotSourceReport,
                "manual-pilot:" + PilotSourceReport,
                PilotSourceReport,
                Stage2Crypto.Sha256Hex(reportContent),
                now.UtcDateTime,
                string.Empty,
                now.UtcDateTime)
            {
                Repository = config.ReportRepoFullName,
                Branch = config.ReportBranch,
                ContentBytes = reportContent,
                SourceReport = PilotSourceReport,
                ReportTaskId = taskId
            };
            var wakeToken = $"DCS_WATCHER_V2_WAKE:stage3-manual-pilot:{Guid.NewGuid():N}";
            var prepared = orchestrator.PrepareHumanConfirmedWakeTransaction(preWake.Snapshot!, report, taskId, wakeToken);
            if (!prepared.Eligible || state.WakeTransaction is null)
                return Fail(result, prepared.BranchDivergence ? "BRANCH_DIVERGENCE" : "WAKE_PREPARATION_REJECTED", prepared.Reason);
            result.TransactionId = state.WakeTransaction.TransactionId;
            state.Stage3ManualPilotTransactionId = result.TransactionId;
            stateService.Save(config, state);

            var prompt = BuildPilotWakePrompt(wakeToken, taskId, config.ReportRepoFullName, now);
            state.LastChatGptWakePrompt = prompt;
            state.LastChatGptWakePromptBuiltAtUtc = now;
            var sent = await chat.SendPromptAsync(prompt, config, state, CancellationToken.None, log);
            if (!sent.Success)
                return Fail(result, "WAKE_SEND_REJECTED", sent.Message);
            result.WakeMessageId = state.WakeTransaction.WakeMessageId;
            stateService.Save(config, state);

            var captured = await chat.CaptureBoundAssistantResponseAsync(config, state.WakeTransaction, CancellationToken.None, log);
            if (!captured.Success || captured.Snapshot is null || captured.Response is null)
                return Fail(result, "BOUND_RESPONSE_REJECTED", captured.Message);
            result.AssistantMessageId = captured.Response.MessageId;
            var lineage = orchestrator.ValidateAssistantResponseForHumanDisplay(captured.Snapshot, captured.Response);
            if (!lineage.Eligible)
                return Fail(result, lineage.BranchDivergence ? "BRANCH_DIVERGENCE" : "LINEAGE_REJECTED", lineage.Reason);
            if (!WatcherSafetyPolicy.CanCaptureInstructionForAuthorization(config, captured.Response, out var captureReason))
                return Fail(result, "CAPTURE_NOT_AUTHORIZED", captureReason);

            var policyBytes = File.ReadAllBytes(config.Stage3IntakePolicyPath);
            var policyValidation = new Stage3IntakePolicyService(trustContext).ValidatePinned(policyBytes, DateTimeOffset.UtcNow);
            if (!policyValidation.Accepted || policyValidation.Policy is null)
                return Fail(result, policyValidation.ReasonCode, policyValidation.Message);
            var policy = policyValidation.Policy;
            var attestationBytes = File.ReadAllBytes(config.Stage3BuildAttestationPath);
            var attestation = Stage3BuildAttestationService.Deserialize(attestationBytes)
                ?? throw new InvalidDataException("The active Stage 3 build attestation is invalid.");
            var specPath = Path.Combine(Path.GetDirectoryName(config.Stage3IntakePolicyPath)!, "drafts", "provisioning-spec.json");
            var spec = JsonSerializer.Deserialize<Stage3SecurityProvisioningSpec>(File.ReadAllBytes(specPath), Stage2CanonicalJson.Options)
                ?? throw new InvalidDataException("The existing Stage 3 key provisioning specification is invalid.");
            using var provenanceSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(spec.ProvenanceKeyId, spec.ProvenanceCngKeyName);
            using var outboundSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(spec.OutboundCheckpointKeyId, spec.OutboundCheckpointCngKeyName);
            if (!provenanceSigner.PublicKeyFingerprintSha256.Equals(attestation.ProvenanceSignerPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                return Fail(result, "PROVENANCE_SIGNER_BUILD_BINDING_MISMATCH", "The existing provenance signing key does not match the active build attestation.");

            var trust = new Stage3TrustStoreService(config.Stage3TrustStorePath, config.Stage3TrustRootPath,
                config.Stage3TrustAnchorPath, rootSigner: null, evaluationTimeUtc: DateTimeOffset.UtcNow);
            var outbound = new Stage3ReplayLedgerV2Service(
                new Stage3LedgerIdentity
                {
                    LedgerRole = "watcher-outbound",
                    LedgerInstanceId = policy.Configuration.OutboundLedgerInstanceId,
                    MutexName = policy.Configuration.OutboundLedgerMutexName,
                    LedgerDirectory = policy.Configuration.OutboundLedgerDirectory,
                    AnchorDirectory = policy.Configuration.OutboundLedgerAnchorDirectory
                },
                outboundSigner,
                new Stage3PurposeKeyResolver(trust, "outbound-ledger-checkpoint", attestation.BuildGeneration, DateTimeOffset.UtcNow),
                TimeSpan.FromMilliseconds(policy.Configuration.LockTimeoutMilliseconds),
                allowLiveManualPilot: true);
            var runtimeRoot = configService.GetLedgerRoot(config);
            var stage2 = new Stage2DryRunPipeline(
                provenanceSigner,
                new Stage2ReplayLedger(Path.Combine(runtimeRoot, "stage2", "replay", "watcher-outbound-ledger.json")),
                Path.Combine(runtimeRoot, "stage3", "manual-pilot", "transactions"));
            var pipeline = new Stage3ReadinessPipeline(stage2, outbound, new Stage3BuildAttestationService(), config, configService, trustContext);
            var pilotRoot = Path.Combine(runtimeRoot, "stage3", "manual-pilot");
            var framePath = Path.Combine(pilotRoot, $"{result.TransactionId}.frame.json");
            var frame = pipeline.BuildManualPilotFrame(
                policy.Configuration.ProvenanceSchemaPath,
                policy.Configuration.VerifierContractPath,
                policy.Configuration.ReplayContractPath,
                state.WakeTransaction,
                captured.Snapshot,
                captured.Response,
                config.CodexThreadId,
                framePath,
                DateTimeOffset.UtcNow);
            if (!frame.Accepted || frame.FrameBytes is null || frame.Provenance is null)
                return Fail(result, frame.ReasonCode, frame.Message);
            result.EnvelopeSha256 = frame.Provenance.EnvelopeSha256;
            result.ProvenanceSha256 = Stage2Crypto.Sha256Hex(Stage2CanonicalJson.SerializeSignedProvenance(frame.Provenance));
            result.SignerFingerprint = provenanceSigner.PublicKeyFingerprintSha256;

            var firstResultPath = Path.Combine(pilotRoot, $"{result.TransactionId}.first-intake.json");
            var first = RunIntake(intakeExecutablePath, framePath, config.Stage3IntakePolicyPath, firstResultPath, runtimeRoot, trustContext.SecurityRoot);
            result.FirstDeliveryResult = $"{first.Disposition}:{first.ReasonCode}:{first.CodexTurnId}";
            if (!first.Disposition.Equals("ACCEPTED_FOR_LIVE_CODEX", StringComparison.Ordinal) || !first.ActionableInstructionExposed)
                return Fail(result, first.ReasonCode, first.Message);

            var outboundAccepted = outbound.Transition(frame.Provenance.TransactionId, Stage3TransactionStates.LiveDelivered, DateTimeOffset.UtcNow);
            if (!outboundAccepted.Accepted)
                return Fail(result, outboundAccepted.ReasonCode, outboundAccepted.Message);

            var replayResultPath = Path.Combine(pilotRoot, $"{result.TransactionId}.replay-intake.json");
            var replay = RunIntake(intakeExecutablePath, framePath, config.Stage3IntakePolicyPath, replayResultPath, runtimeRoot, trustContext.SecurityRoot);
            result.ReplayResult = $"{replay.Disposition}:{replay.ReasonCode}";
            if (!replay.Disposition.Equals("REJECTED", StringComparison.Ordinal))
                return Fail(result, "REPLAY_ACCEPTED", "The identical Stage 3 transaction replay was not rejected.");

            PopulateLedgerResults(result, outbound, policy, trust, spec, attestation.BuildGeneration);
            if (result.DuplicateAcceptanceCount != 0)
                return Fail(result, "DUPLICATE_ACCEPTANCE", "The intake ledger contains more than one live acceptance.");
            if (DcsProcessDetectionService.CountRunningDcsProcesses() != 0)
                return Fail(result, "DCS_PROCESS_STARTED", "A DCS-family process appeared during the pilot.");

            result.Disposition = "PASS_PENDING_CODEX_RECEIPT";
            result.ReasonCode = "OK";
            result.Message = "One verified delivery was accepted and the identical replay was rejected; final promotion awaits receipt inspection.";
            return result;
        }
        catch (Exception ex)
        {
            return Fail(result, "PILOT_PROCESS_FAILURE", ex.Message);
        }
        finally
        {
            DisableLiveOperation(config);
            configService.Save(config);
            guard.Complete(state, result.Disposition, result.TransactionId);
            state.OperatingStage = config.OperatingStage;
            stateService.Save(config, state);
            result.WatcherStopped = true;
            WriteResult(resultPath, result);
            orchestrator?.Dispose();
        }
    }

    internal static bool PreWakePermitsWake(ChatGptLineageCaptureResult result) =>
        result.Success && result.Snapshot is not null;

    public static Stage3ManualPilotResult FinalizePass(string resultPath, AppConfig config)
    {
        resultPath = Path.GetFullPath(resultPath);
        var result = JsonSerializer.Deserialize<Stage3ManualPilotResult>(File.ReadAllBytes(resultPath), Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("Pilot result is empty.");
        if (!result.Disposition.Equals("PASS_PENDING_CODEX_RECEIPT", StringComparison.Ordinal) ||
            !result.FirstDeliveryResult.StartsWith("ACCEPTED_FOR_LIVE_CODEX:OK:", StringComparison.Ordinal) ||
            !result.ReplayResult.StartsWith("REJECTED:", StringComparison.Ordinal) ||
            result.DuplicateAcceptanceCount != 0 || result.UnauthorizedDeliveryCount != 0 || !result.WatcherStopped)
            throw new InvalidOperationException("Pilot result is not eligible for final PASS promotion.");
        var configService = new ConfigService(config.InstallationRoot);
        var stateService = new StateService(configService);
        var state = stateService.Load(config);
        DisableLiveOperation(config);
        config.OperatingStage = nameof(WatcherOperatingStage.Stage3ManualPilotPassed);
        state.OperatingStage = config.OperatingStage;
        state.Stage3ManualPilotTerminalResult = "PASS";
        state.WatcherRunning = false;
        configService.Save(config);
        stateService.Save(config, state);
        result.Disposition = "PASS";
        result.Message = "The verified read-only Codex receipt was confirmed; Stage 3 manual pilot passed.";
        WriteResult(resultPath, result);
        return result;
    }

    private static string BuildPilotWakePrompt(string wakeToken, string taskId, string repository, DateTimeOffset now) => string.Join("\n", new[]
    {
        wakeToken,
        string.Empty,
        "This is the single manually supervised Watcher Stage 3 provenance pilot on the current human-visible branch.",
        "Reply with exactly one DCS_CODEX_TASK_V1 envelope using the exact fields and instruction below. Do not add another envelope or alter the instruction.",
        string.Empty,
        ChatGptEnvelopeCapture.OpenMarker,
        $"task_id: {taskId}",
        "origin: chatgpt-ui",
        $"repo: {repository}",
        "target: codex-director",
        "mode: instruction",
        $"created_at: {now:O}",
        $"source_report: {PilotSourceReport}",
        string.Empty,
        "BEGIN_INSTRUCTION",
        PilotInstruction,
        "END_INSTRUCTION",
        ChatGptEnvelopeCapture.CloseMarker
    });

    private static Stage3IntakeResult RunIntake(
        string intakeExecutablePath,
        string framePath,
        string policyPath,
        string resultPath,
        string workingDirectory,
        string installationSecurityRoot)
    {
        using var process = Process.Start(new ProcessStartInfo(intakeExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            ArgumentList = { "verify-manual-pilot", framePath, policyPath, resultPath, installationSecurityRoot }
        }) ?? throw new InvalidOperationException("Could not start the verified Codex intake process.");
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The verified Codex intake process timed out.");
        }
        if (!File.Exists(resultPath))
            throw new InvalidDataException("The verified Codex intake process produced no result record.");
        return JsonSerializer.Deserialize<Stage3IntakeResult>(File.ReadAllBytes(resultPath), Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("The verified Codex intake result is empty.");
    }

    private static void PopulateLedgerResults(
        Stage3ManualPilotResult result,
        Stage3ReplayLedgerV2Service outbound,
        Stage3CodexIntakePolicyV1 policy,
        Stage3TrustStoreService trust,
        Stage3SecurityProvisioningSpec spec,
        long buildGeneration)
    {
        var now = DateTimeOffset.UtcNow;
        var outboundLedger = outbound.ReadVerifiedLedger(now);
        var outboundEntry = outboundLedger.Entries.Last(entry => entry.TransactionId.Equals(result.TransactionId, StringComparison.Ordinal));
        result.OutboundLedgerEntry = JsonSerializer.Serialize(outboundEntry, Stage2CanonicalJson.Options);
        using var intakeSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(spec.IntakeCheckpointKeyId, spec.IntakeCheckpointCngKeyName);
        var intake = new Stage3ReplayLedgerV2Service(
            new Stage3LedgerIdentity
            {
                LedgerRole = "codex-intake",
                LedgerInstanceId = policy.Configuration.IntakeLedgerInstanceId,
                MutexName = policy.Configuration.IntakeLedgerMutexName,
                LedgerDirectory = policy.Configuration.IntakeLedgerDirectory,
                AnchorDirectory = policy.Configuration.IntakeLedgerAnchorDirectory
            },
            intakeSigner,
            new Stage3PurposeKeyResolver(trust, "intake-ledger-checkpoint", buildGeneration, now),
            TimeSpan.FromMilliseconds(policy.Configuration.LockTimeoutMilliseconds),
            allowLiveManualPilot: true);
        var intakeLedger = intake.ReadVerifiedLedger(now);
        var accepted = intakeLedger.Entries.Where(entry =>
            entry.TransactionId.Equals(result.TransactionId, StringComparison.Ordinal) &&
            entry.Disposition.Equals(Stage3TransactionStates.LiveDelivered, StringComparison.Ordinal)).ToList();
        result.IntakeLedgerEntry = JsonSerializer.Serialize(accepted.Last(), Stage2CanonicalJson.Options);
        result.DuplicateAcceptanceCount = Math.Max(0, accepted.Count - 1);
        result.UnauthorizedDeliveryCount = 0;
    }

    private static void DisableLiveOperation(AppConfig config)
    {
        if (config.OperatingStage.Equals(nameof(WatcherOperatingStage.Stage3ManualPilot), StringComparison.Ordinal))
            config.OperatingStage = nameof(WatcherOperatingStage.Stage3ManualPilotReady);
        config.LiveManualPilotAuthorized = false;
        config.LiveCodexIntakeEnabled = false;
        config.AutomaticWakeEnabled = false;
        config.AutomaticDeliveryEnabled = false;
        config.AutomaticInstructionDeliveryEnabled = false;
        config.SubmitChatGptPrompt = false;
        config.AutoCaptureChatGptEnvelope = false;
        config.SubmitCodexPrompt = false;
        config.AutoSendCapturedTaskToCodex = false;
        config.Stage4Authorized = false;
        config.Stage5Authorized = false;
        config.StartWatcherOnLaunch = false;
    }

    private static Stage3ManualPilotResult Fail(Stage3ManualPilotResult result, string code, string message)
    {
        result.Disposition = "FAIL_CLOSED";
        result.ReasonCode = code;
        result.Message = message;
        return result;
    }

    private static void WriteResult(string path, Stage3ManualPilotResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stage2AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(result, Stage2CanonicalJson.Options));
    }
}
