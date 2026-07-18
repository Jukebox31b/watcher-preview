using System.Diagnostics;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record Stage4CycleResult(bool Success, string Code, string Message, string TransactionId = "", string TaskId = "");

public sealed class Stage4LimitedAutomaticService
{
    private readonly StateService _stateService;
    private readonly LogService _log;
    private readonly ChatGptDirectorBridge _chat;
    private readonly LedgerService _ledger;
    private readonly InstallationTrustContext? _trustContext;

    public Stage4LimitedAutomaticService(
        StateService stateService,
        LogService log,
        ChatGptDirectorBridge chat,
        LedgerService ledger,
        InstallationTrustContext? trustContext)
    {
        _stateService = stateService;
        _log = log;
        _chat = chat;
        _ledger = ledger;
        _trustContext = trustContext;
    }

    public async Task<Stage4CycleResult> RunOneAsync(
        AppConfig config,
        AppState state,
        ReportCandidate report,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunOneCoreAsync(config, state, report, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Stage4LastResult = "STAGE4_CANCELLED: Operator stop cancelled the active automatic transaction.";
            state.Stage4LastProgressAtUtc = DateTimeOffset.UtcNow;
            _log.Warning(state.Stage4LastResult, "Stage4");
            return new Stage4CycleResult(false, "STAGE4_CANCELLED", "Operator stop cancelled the active automatic transaction.", state.Stage4LastTransactionId);
        }
        catch (Exception ex)
        {
            return Fail(state, "STAGE4_PROCESS_FAILURE", ex.Message);
        }
    }

    private async Task<Stage4CycleResult> RunOneCoreAsync(
        AppConfig config,
        AppState state,
        ReportCandidate report,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(config, out var safetyReason))
            return Fail(state, "STAGE4_NOT_AUTHORIZED", safetyReason);
        if (_trustContext is null)
            return Fail(state, "INSTALLATION_TRUST_REQUIRED", "Validated installation trust is required before Stage 4 can inspect or expose a payload.");
        if (!HasVerifiedBoundedAuthorization(out var authorizationReason))
            return Fail(state, "BOUNDED_GRANT_REQUIRED", authorizationReason);
        if (state.Stage4Halted)
            return new Stage4CycleResult(false, "STAGE4_HALTED", state.Stage4LastResult);
        if (state.ActiveTaskLock.IsActive)
            return new Stage4CycleResult(false, "ACTIVE_TASK_PRESENT", $"Waiting for terminal report for {state.ActiveTaskLock.ActiveTaskId}.");
        if (!File.Exists(config.Stage4IntakeExecutablePath))
            return Fail(state, "INTAKE_EXECUTABLE_MISSING", config.Stage4IntakeExecutablePath);
        if (DcsProcessDetectionService.CountRunningDcsProcesses() != 0)
            return Fail(state, "DCS_PROCESS_PRESENT", "A real DCS process is running; Stage 4 stopped fail-closed.");

        var preWake = await _chat.GetCurrentLineageSnapshotAsync(config, cancellationToken, _log);
        if (!preWake.Success || preWake.Snapshot is null)
            return Fail(state, string.IsNullOrWhiteSpace(preWake.ReasonCode) ? "PRE_WAKE_LINEAGE_REJECTED" : preWake.ReasonCode, preWake.Message);

        var wakeToken = $"DCS_WATCHER_V2_WAKE:stage4:{Guid.NewGuid():N}";
        var replay = new TransactionReplayGuardService().TryReserveWakeToken(state, wakeToken);
        if (!replay.Eligible) return Fail(state, "WAKE_TOKEN_REJECTED", replay.Reason);
        var ancestry = BranchLineageSafetyService.BuildAncestry(preWake.Snapshot, preWake.Snapshot.CurrentNode).ToList();
        state.WakeTransaction = new WakeTransactionRecord
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            Nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            ConversationId = preWake.Snapshot.ConversationId,
            CurrentNodeBeforeWake = preWake.Snapshot.CurrentNode,
            VisibleBranchAncestry = ancestry,
            VisibleParentMessageId = preWake.Snapshot.CurrentNode,
            BrowserTabIdentity = preWake.Snapshot.BrowserTabIdentity,
            WakeToken = wakeToken,
            IntendedSourceReport = report.FileName,
            IntendedActiveTask = report.ReportTaskId,
            HumanConfirmed = false,
            Status = "automatic-bounded-grant-preflight"
        };
        state.Stage4LastTransactionId = state.WakeTransaction.TransactionId;
        _stateService.Save(config, state);

        var prompt = new ChatGptWakePromptBuilder().Build(config, report, wakeToken, requestFollowOnInstruction: true);
        state.LastChatGptWakePrompt = prompt;
        state.LastChatGptWakePromptBuiltAtUtc = DateTimeOffset.UtcNow;
        var sent = await _chat.SendPromptAsync(prompt, config, state, cancellationToken, _log);
        if (!sent.Success) return Fail(state, sent.Busy ? "CHATGPT_BUSY" : "WAKE_SEND_REJECTED", sent.Message);
        state.MarkReportCompleted(report, prompt, state.LastChatGptWakeSentAtUtc ?? DateTimeOffset.UtcNow, false, sent.Token);
        _stateService.Save(config, state);

        var captured = await _chat.CaptureBoundAssistantResponseAsync(config, state.WakeTransaction, cancellationToken, _log);
        if (!captured.Success || captured.Snapshot is null || captured.Response is null)
            return Fail(state, "BOUND_RESPONSE_REJECTED", captured.Message);
        var lineage = new BranchLineageSafetyService().Validate(state.WakeTransaction, captured.Snapshot, captured.Response);
        if (!lineage.Eligible)
            return Fail(state, lineage.BranchDivergence ? "BRANCH_DIVERGENCE" : "LINEAGE_REJECTED", lineage.Reason);
        if (!WatcherSafetyPolicy.CanCaptureInstructionForAuthorization(config, captured.Response, out var captureReason))
            return Fail(state, "CAPTURE_NOT_AUTHORIZED", captureReason);

        var parsed = new ChatGptEnvelopeCapture().TryCapture(captured.Response.Content, config, state);
        if (!parsed.Success || parsed.Envelope is null)
            return Fail(state, "ENVELOPE_REJECTED", parsed.Message);
        var envelope = parsed.Envelope;
        var capturedAt = DateTimeOffset.UtcNow;
        var envelopePath = _ledger.SaveEnvelope(config, envelope);
        var taskPath = _ledger.SaveTaskFile(config, envelope);
        var metadataPath = _ledger.SaveTaskMetadata(config, envelope, envelopePath, taskPath, capturedAt);
        state.MarkTaskCaptured(envelope, envelopePath, taskPath, metadataPath, capturedAt);
        _stateService.Save(config, state);

        cancellationToken.ThrowIfCancellationRequested();
        var policyBytes = await File.ReadAllBytesAsync(config.Stage3IntakePolicyPath, cancellationToken);
        var policyResult = new Stage3IntakePolicyService(_trustContext).ValidatePinned(policyBytes, DateTimeOffset.UtcNow);
        if (!policyResult.Accepted || policyResult.Policy is null)
            return Fail(state, policyResult.ReasonCode, policyResult.Message);
        var policy = policyResult.Policy;
        var attestation = Stage3BuildAttestationService.Deserialize(await File.ReadAllBytesAsync(config.Stage3BuildAttestationPath, cancellationToken))
            ?? throw new InvalidDataException("Active build attestation is invalid.");
        var specPath = Path.Combine(Path.GetDirectoryName(config.Stage3IntakePolicyPath)!, "drafts", "provisioning-spec.json");
        var spec = JsonSerializer.Deserialize<Stage3SecurityProvisioningSpec>(await File.ReadAllBytesAsync(specPath, cancellationToken), Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("Security provisioning specification is invalid.");
        using var provenanceSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(spec.ProvenanceKeyId, spec.ProvenanceCngKeyName);
        using var outboundSigner = WindowsCngStage2ProvenanceSigner.OpenExisting(spec.OutboundCheckpointKeyId, spec.OutboundCheckpointCngKeyName);
        var trust = new Stage3TrustStoreService(config.Stage3TrustStorePath, config.Stage3TrustRootPath,
            config.Stage3TrustAnchorPath, null, DateTimeOffset.UtcNow);
        var outbound = new Stage3ReplayLedgerV2Service(new Stage3LedgerIdentity
        {
            LedgerRole = "watcher-outbound",
            LedgerInstanceId = policy.Configuration.OutboundLedgerInstanceId,
            MutexName = policy.Configuration.OutboundLedgerMutexName,
            LedgerDirectory = policy.Configuration.OutboundLedgerDirectory,
            AnchorDirectory = policy.Configuration.OutboundLedgerAnchorDirectory
        }, outboundSigner, new Stage3PurposeKeyResolver(trust, "outbound-ledger-checkpoint", attestation.BuildGeneration, DateTimeOffset.UtcNow),
            TimeSpan.FromMilliseconds(policy.Configuration.LockTimeoutMilliseconds), allowLiveManualPilot: true);
        var runtimeRoot = _ledger.GetLedgerRoot(config);
        var stage2 = new Stage2DryRunPipeline(provenanceSigner,
            new Stage2ReplayLedger(Path.Combine(runtimeRoot, "stage2", "replay", "watcher-outbound-ledger.json")),
            Path.Combine(runtimeRoot, "stage4", "transactions"));
        var framePath = Path.Combine(runtimeRoot, "stage4", $"{state.WakeTransaction.TransactionId}.frame.json");
        var frame = new Stage3ReadinessPipeline(stage2, outbound, new Stage3BuildAttestationService(), config, _stateService.ConfigService, _trustContext).BuildLimitedAutomaticFrame(
            policy.Configuration.ProvenanceSchemaPath, policy.Configuration.VerifierContractPath, policy.Configuration.ReplayContractPath,
            state.WakeTransaction, captured.Snapshot, captured.Response, config.CodexThreadId, framePath, DateTimeOffset.UtcNow);
        if (!frame.Accepted || frame.Provenance is null)
            return Fail(state, frame.ReasonCode, frame.Message);

        var intakeResultPath = Path.Combine(runtimeRoot, "stage4", $"{state.WakeTransaction.TransactionId}.intake.json");
        var intake = await RunIntakeAsync(
            config.Stage4IntakeExecutablePath,
            framePath,
            config.Stage3IntakePolicyPath,
            intakeResultPath,
            runtimeRoot,
            _trustContext.SecurityRoot,
            cancellationToken);
        if (!intake.Disposition.Equals("ACCEPTED_FOR_LIVE_CODEX", StringComparison.Ordinal) || !intake.ActionableInstructionExposed)
            return Fail(state, intake.ReasonCode, intake.Message);
        var delivered = outbound.Transition(frame.Provenance.TransactionId, Stage3TransactionStates.LiveDelivered, DateTimeOffset.UtcNow);
        if (!delivered.Accepted) return Fail(state, delivered.ReasonCode, delivered.Message);

        var active = new ActiveTaskLockService().TryActivate(state, envelope.TaskId, frame.Provenance.EnvelopeSha256,
            report.FileName, config.CodexThreadId, string.Empty, DateTimeOffset.UtcNow);
        if (!active.Eligible) return Fail(state, "ACTIVE_TASK_LOCK_REJECTED", active.Reason);
        state.PendingCodexTaskId = string.Empty;
        state.PendingCodexTaskPath = string.Empty;
        state.CodexDeliveryPending = false;
        state.Stage4DeliveredCount++;
        state.Stage4LastProgressAtUtc = DateTimeOffset.UtcNow;
        state.Stage4LastResult = $"DELIVERED {envelope.TaskId} transaction={frame.Provenance.TransactionId}";
        _stateService.Save(config, state);
        return new Stage4CycleResult(true, "OK", state.Stage4LastResult, frame.Provenance.TransactionId, envelope.TaskId);
    }

    internal static bool HasVerifiedBoundedAuthorization(out string reason)
    {
        reason = "No separately verified signed bounded-autopilot grant is installed. Stage 4 automatic authorization remains fail-closed.";
        return false;
    }

    private static async Task<Stage3IntakeResult> RunIntakeAsync(
        string executable,
        string frame,
        string policy,
        string result,
        string workingDirectory,
        string installationSecurityRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            ArgumentList = { "verify-limited-automatic", frame, policy, result, installationSecurityRoot }
        }) ?? throw new InvalidOperationException("Could not start verified intake process.");
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException("Verified intake process timed out and was terminated.");
        }
        return JsonSerializer.Deserialize<Stage3IntakeResult>(await File.ReadAllBytesAsync(result, cancellationToken), Stage2CanonicalJson.Options)
            ?? throw new InvalidDataException("Verified intake produced no result.");
    }

    private Stage4CycleResult Fail(AppState state, string code, string message)
    {
        state.Stage4LastResult = $"{code}: {message}";
        state.Stage4LastProgressAtUtc = DateTimeOffset.UtcNow;
        if (code is not "ACTIVE_TASK_PRESENT" and not "CHATGPT_BUSY") state.Stage4Halted = true;
        _log.Error(state.Stage4LastResult, "Stage4");
        return new Stage4CycleResult(false, code, message, state.Stage4LastTransactionId);
    }
}
