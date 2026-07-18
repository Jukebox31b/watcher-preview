using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class WatcherOrchestrator : IDisposable
{
    public const string TestChatGptWakeRouteLog = "[ROUTE] TestChatGptWake -> ChatGptDirectorBridge";
    public const string TestCodexWakeRouteLog = "[ROUTE] TestCodexWake -> CodexDirectorBridge";
    public const string SendLatestTaskToCodexRouteLog = "[ROUTE] SendLatestTaskToCodex -> CodexDirectorBridge";
    public const string ResendLatestReportAction = "resend-latest-report";
    public const string WakeNewestReportAction = "wake-newest-report-once";

    private readonly ConfigService _configService;
    private readonly StateService _stateService;
    private readonly LogService _logService;
    private readonly GitHubReportPoller _reportPoller;
    private readonly DirectorReportPublishService _directorReportPublishService;
    private readonly ChatGptDirectorBridge _chatGptDirectorBridge;
    private readonly ChatGptWakePromptBuilder _wakePromptBuilder;
    private readonly GitPullService _gitPullService;
    private readonly ChatGptEnvelopeCapture _envelopeCapture;
    private readonly CodexDirectorBridge _codexDirectorBridge;
    private readonly LedgerService _ledgerService;
    private readonly BranchGuardService _branchGuardService;
    private readonly InstallationTrustContext? _trustContext;
    private readonly ReportIngestionVerifier _reportIngestionVerifier = new();
    private readonly BranchLineageSafetyService _branchLineageSafetyService = new();
    private readonly TransactionReplayGuardService _transactionReplayGuardService = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly WatcherLifecycleController _lifecycle = new();
    private readonly HashSet<string> _usedHumanConfirmationNonces = new(StringComparer.Ordinal);
    private readonly object _confirmationGate = new();
    private readonly PreparedHumanActionCoordinator _humanActions = new();
    private System.Threading.Timer? _timer;
    private CapturedTaskEnvelope? _latestEnvelope;

    public WatcherOrchestrator(
        AppConfig config,
        AppState state,
        ConfigService configService,
        StateService stateService,
        LogService logService,
        GitHubReportPoller reportPoller,
        DirectorReportPublishService directorReportPublishService,
        ChatGptDirectorBridge chatGptDirectorBridge,
        ChatGptWakePromptBuilder wakePromptBuilder,
        GitPullService gitPullService,
        ChatGptEnvelopeCapture envelopeCapture,
        CodexDirectorBridge codexDirectorBridge,
        LedgerService ledgerService,
        BranchGuardService branchGuardService,
        InstallationTrustContext? trustContext = null)
    {
        Config = config;
        State = state;
        _configService = configService;
        _stateService = stateService;
        _logService = logService;
        _reportPoller = reportPoller;
        _directorReportPublishService = directorReportPublishService;
        _chatGptDirectorBridge = chatGptDirectorBridge;
        _wakePromptBuilder = wakePromptBuilder;
        _gitPullService = gitPullService;
        _envelopeCapture = envelopeCapture;
        _codexDirectorBridge = codexDirectorBridge;
        _ledgerService = ledgerService;
        _branchGuardService = branchGuardService;
        _trustContext = trustContext;
    }

    public event EventHandler? StateChanged;

    public AppConfig Config { get; }
    public AppState State { get; }

    public void Start()
    {
        if (State.WatcherRunning)
        {
            _logService.Info("Watcher is already running.");
            return;
        }

        var lifecycleToken = _lifecycle.Start();
        State.WatcherRunning = true;
        WindowsPowerAwakeService.KeepSystemAwake(_logService);
        var dueTime = TimeSpan.Zero;
        var period = TimeSpan.FromSeconds(Math.Max(5, Config.PollSeconds));
        _timer = new System.Threading.Timer(_ => _ = RunPollCycleAsync("timer", lifecycleToken), null, dueTime, period);
        _logService.Info($"Watcher started with {Config.PollSeconds}s poll interval.");
        SaveState();
        OnStateChanged();
    }

    public void Stop()
    {
        _lifecycle.Stop();
        _timer?.Dispose();
        _timer = null;
        State.WatcherRunning = false;
        WindowsPowerAwakeService.ClearKeepAwake(_logService);
        _logService.Info("Watcher stopped.");
        SaveState();
        OnStateChanged();
    }

    public async Task<BranchGuardResult> CheckBranchAsync(CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var result = await _branchGuardService.CheckAsync(Config, command.Token);
        command.Token.ThrowIfCancellationRequested();
        State.CurrentBranch = result.Branch;

        if (result.IsMain)
        {
            State.ErrorCount++;
            _logService.Error(result.Message, "BranchGuard");
        }
        else if (result.IsBlocked)
        {
            _logService.Warning(result.Message, "BranchGuard");
        }
        else
        {
            _logService.Info(result.Message, "BranchGuard");
        }

        await SaveStateAsync();
        OnStateChanged();
        return result;
    }

    public async Task<ReportScanResult> ScanNowAsync(CancellationToken cancellationToken = default)
    {
        return await RunPollCycleAsync("manual", cancellationToken);
    }

    public async Task<ReportScanResult> RunPollCycleAsync(string reason, CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var effectiveToken = command.Token;
        try
        {
            if (!await _scanLock.WaitAsync(0, effectiveToken))
            {
                return new ReportScanResult(false, false, false, null, null, "Scan skipped because another scan is already running.");
            }
        }
        catch (OperationCanceledException)
        {
            return CancelledPollResult(reason);
        }

        try
        {
            effectiveToken.ThrowIfCancellationRequested();
            State.LastPollCycleAtUtc = DateTimeOffset.UtcNow;
            State.LastActionableLane = PollActionLane.None.ToString();
            State.LastPollCycleSummary = "Poll cycle started.";
            State.ReportWakePending = false;
            State.CapturePending = false;
            State.ReconcileCodexDeliveryPending();
            _logService.Info($"Poll cycle started. reason={reason}", "Poll");
            var branch = await _branchGuardService.CheckAsync(Config, effectiveToken);
            effectiveToken.ThrowIfCancellationRequested();
            State.CurrentBranch = branch.Branch;
            if (branch.IsMain)
            {
                State.ErrorCount++;
                _logService.Error(branch.Message, "BranchGuard");
            }
            else if (branch.IsBlocked)
            {
                _logService.Warning(branch.Message, "BranchGuard");
            }
            else
            {
                _logService.Info(branch.Message, "BranchGuard");
            }

            await RunOptionalGitPullAsync(branch, effectiveToken);
            _logService.Info("Report publishing is outside Watcher's remediated operating boundary; no local report commit or push will be attempted.", "Safety");

            _logService.Info(
                $"GitHub report scan started. mode={Config.ReportPollMode} repo={Config.ReportRepoFullName} branch={Config.ReportBranch} folder={Config.ReportFolder}",
                "GitHub");
            var result = await _reportPoller.ScanAsync(
                Config,
                State,
                cancellationToken: effectiveToken);
            effectiveToken.ThrowIfCancellationRequested();
            LogScanResult(result);
            if (result.NewestCandidate is not null)
            {
                LogNewestReportDecision(result.NewestCandidate);
            }

            RecordDetectedReport(result.NewestCandidate ?? result.Candidate);

            RecordReportIngestion(result.NewestCandidate ?? result.Candidate);
            if (WatcherSafetyPolicy.ResolveStage(Config) == WatcherOperatingStage.Stage4LimitedAutomatic)
            {
                var candidate = result.Candidate ?? result.NewestCandidate;
                if (candidate is null)
                {
                    State.LastPollCycleSummary = "Stage 4 idle: no report candidate is available.";
                }
                else if (State.ActiveTaskLock.IsActive)
                {
                    var ingestion = _reportIngestionVerifier.Verify(Config, State, candidate, DateTimeOffset.UtcNow);
                    State.LastReportIngestion = ingestion;
                    if (!ingestion.Eligible)
                    {
                        State.LastPollCycleSummary = $"Stage 4 waiting for terminal report for {State.ActiveTaskLock.ActiveTaskId}: {ingestion.RejectionReason}";
                    }
                    else
                    {
                        _reportIngestionVerifier.TryCloseActiveTask(State, ingestion);
                        var cycle = await CreateStage4Service().RunOneAsync(Config, State, candidate, effectiveToken);
                        State.LastPollCycleSummary = $"Stage 4 {cycle.Code}: {cycle.Message}";
                    }
                }
                else if (!State.Stage4BootstrapConsumed)
                {
                    var bootstrap = _reportIngestionVerifier.VerifyStage4Bootstrap(Config, State, candidate, DateTimeOffset.UtcNow);
                    State.LastReportIngestion = bootstrap;
                    if (!bootstrap.Eligible)
                    {
                        State.LastPollCycleSummary = $"Stage 4 bootstrap rejected: {bootstrap.RejectionReason}";
                    }
                    else
                    {
                        State.Stage4BootstrapConsumed = true;
                        State.Stage4BootstrapConsumedSha256 = candidate.Fingerprint;
                        State.ConsumedReportSha256.Add(candidate.Fingerprint);
                        var cycle = await CreateStage4Service().RunOneAsync(Config, State, candidate, effectiveToken);
                        State.LastPollCycleSummary = $"Stage 4 {cycle.Code}: {cycle.Message}";
                    }
                }
                else
                {
                    State.LastPollCycleSummary = "Stage 4 idle: bootstrap is consumed and no active task is awaiting a report.";
                }

                if (State.Stage4Halted && Config.Stage4StopOnFailure)
                {
                    _lifecycle.Stop();
                    _timer?.Dispose();
                    _timer = null;
                    State.WatcherRunning = false;
                    WindowsPowerAwakeService.ClearKeepAwake(_logService);
                    _logService.Error("Stage 4 stopped fail-closed. " + State.Stage4LastResult, "Stage4");
                }
                await SaveStateAsync();
                OnStateChanged();
                return result;
            }
            State.LastActionableLane = PollActionLane.GitHubReport.ToString();
            State.ReportWakePending = false;
            State.CapturePending = false;
            State.CodexDeliveryPending = false;
            State.LastPollCycleSummary = State.LastReportIngestion is null
                ? "Stage 1 detect-only: no report candidate was available."
                : State.LastReportIngestion.Eligible
                    ? $"Stage 1 detect-only: authenticated report displayed; no wake or delivery: {State.LastReportIngestion.ReportPath}"
                    : $"Stage 1 detect-only: report rejected: {State.LastReportIngestion.RejectionReason}";
            _logService.Info(State.LastPollCycleSummary, "Safety");

            await SaveStateAsync();
            OnStateChanged();
            return result;
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            return CancelledPollResult(reason);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private Stage4LimitedAutomaticService CreateStage4Service() => new(
        _stateService,
        _logService,
        _chatGptDirectorBridge,
        _ledgerService,
        _trustContext);

    public async Task<PreparedHumanActionResult> PrepareHumanActionAsync(
        string action,
        WatcherProfileV1 profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var token = command.Token;
        if (action is not (ResendLatestReportAction or WakeNewestReportAction))
        {
            return RejectPreparation("HUMAN_ACTION_INVALID: unsupported action.");
        }
        if (!TryValidateRuntimeProfile(profile, out var bindingReason))
        {
            return RejectPreparation(bindingReason);
        }

        var candidatesResult = await _reportPoller.GetCandidatesAsync(Config, token);
        token.ThrowIfCancellationRequested();
        if (!candidatesResult.Success || candidatesResult.Candidates.Count == 0)
        {
            var message = candidatesResult.Success
                ? "HUMAN_ACTION_NO_REPORT: no report candidate is available."
                : "HUMAN_ACTION_REPORT_SCAN_FAILED: " + candidatesResult.Message;
            State.ClearChatGptWakePromptPreview(message);
            await SaveStateAsync();
            OnStateChanged();
            return RejectPreparation(message);
        }

        var report = candidatesResult.Candidates[0];
        if (action == WakeNewestReportAction && State.WasReportWoken(report.FileName))
        {
            var duplicate = $"HUMAN_ACTION_DUPLICATE: newest report was already woken: {report.FileName}";
            State.ClearChatGptWakePromptPreview(duplicate);
            await SaveStateAsync();
            OnStateChanged();
            return RejectPreparation(duplicate);
        }

        var wakeToken = BuildWakeToken(report.Fingerprint[..Math.Min(12, report.Fingerprint.Length)]);
        var prompt = _wakePromptBuilder.Build(Config, report, wakeToken);
        var prepared = PreparedHumanAction.Create(
            action,
            report,
            prompt,
            wakeToken,
            profile,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            Config.ChatGptDirectorUrl,
            Config.CodexThreadId);
        if (!_humanActions.TryPrepare(prepared.Nonce, out var leaseReason))
        {
            return RejectPreparation(leaseReason);
        }

        State.RecordWakePromptBuilt(prepared.Report, prepared.Prompt, prepared.WakeToken, prepared.IssuedAtUtc);
        _logService.Info(
            $"HUMAN_ACTION_PREPARED action={prepared.Action} nonce={prepared.Nonce} report={prepared.Report.RelativePath} report_sha256={prepared.Report.Fingerprint} prompt_sha256={prepared.PromptSha256} wake_token={prepared.WakeToken} profile={prepared.ProfileId} destination={prepared.DestinationDisplay} policy={prepared.PolicyDisplay} issued={prepared.IssuedAtUtc:O} expires={prepared.ExpiresAtUtc:O}",
            "HumanConfirmation");
        await SaveStateAsync();
        OnStateChanged();
        return new PreparedHumanActionResult(true, prepared, "Prepared action is awaiting explicit human confirmation.");
    }

    public async Task<ReportScanResult> ExecutePreparedHumanActionAsync(
        PreparedHumanAction prepared,
        HumanConfirmationRecord confirmation,
        WatcherProfileV1 currentProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(currentProfile);
        if (!_humanActions.TryBegin(prepared.Nonce, out var leaseReason))
        {
            return RejectPreparedExecution(prepared, leaseReason);
        }

        try
        {
            if (!TryValidateRuntimeProfile(currentProfile, out var runtimeReason))
            {
                return RejectPreparedExecution(prepared, runtimeReason);
            }
            if (!ValidatePreparedHumanAction(
                    prepared,
                    confirmation,
                    currentProfile,
                    DateTimeOffset.UtcNow,
                    _usedHumanConfirmationNonces,
                    _confirmationGate,
                    reserve: true,
                    out var confirmationReason))
            {
                return RejectPreparedExecution(prepared, confirmationReason);
            }

            _logService.Info(
                $"HUMAN_ACTION_CONFIRMED action={prepared.Action} nonce={prepared.Nonce} report_sha256={prepared.Report.Fingerprint} prompt_sha256={prepared.PromptSha256}",
                "HumanConfirmation");
            using var command = _lifecycle.BeginCommand(cancellationToken);
            var execution = await ExecuteFrozenWakePromptAsync(
                prepared,
                isResend: prepared.Action == ResendLatestReportAction,
                command.Token);
            await SaveStateAsync();
            OnStateChanged();
            return new ReportScanResult(
                true,
                execution.Delivered,
                false,
                execution.Delivered ? prepared.Report : null,
                prepared.Report,
                execution.Message);
        }
        finally
        {
            _humanActions.Complete(prepared.Nonce);
        }
    }

    public void CancelPreparedHumanAction(PreparedHumanAction prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var cancelled = _humanActions.Cancel(prepared.Nonce);
        _logService.Info(
            $"HUMAN_ACTION_{(cancelled ? "CANCELLED" : "CANCEL_REJECTED")} action={prepared.Action} nonce={prepared.Nonce} report_sha256={prepared.Report.Fingerprint}; no delivery attempted.",
            "HumanConfirmation");
    }

    private PreparedHumanActionResult RejectPreparation(string reason)
    {
        _logService.Warning("HUMAN_ACTION_PREPARE_REJECTED " + reason, "HumanConfirmation");
        return new PreparedHumanActionResult(false, null, reason);
    }

    private ReportScanResult RejectPreparedExecution(PreparedHumanAction prepared, string reason)
    {
        State.LastChatGptWakeResult = reason;
        _logService.Warning(
            $"HUMAN_ACTION_CONFIRM_REJECTED action={prepared.Action} nonce={prepared.Nonce} reason={reason}",
            "HumanConfirmation");
        return new ReportScanResult(true, false, true, null, prepared.Report, reason);
    }

    private bool TryValidateRuntimeProfile(WatcherProfileV1 profile, out string reason)
    {
        var runtimeProfileId = string.IsNullOrWhiteSpace(Config.RuntimeProfileId)
            ? Config.ActiveProfileId
            : Config.RuntimeProfileId;
        if (!runtimeProfileId.Equals(profile.Identity.ProfileId, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_PROFILE_MISMATCH: the active runtime profile changed.";
            return false;
        }
        if (!Config.CodexThreadId.Equals(profile.Destination.DestinationIdentity, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_DESTINATION_MISMATCH: the runtime destination changed.";
            return false;
        }
        var profileDirectorIdentity = !string.IsNullOrWhiteSpace(profile.Director.ConversationIdentity)
            ? profile.Director.ConversationIdentity
            : profile.Director.Adapter.Settings.TryGetValue("conversation_url", out var configuredDirector)
                ? configuredDirector
                : string.Empty;
        if (!Config.ChatGptDirectorUrl.Equals(profileDirectorIdentity, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_DESTINATION_MISMATCH: the runtime Director destination changed.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public Task<ReportScanResult> ResendLatestReportAsync(CancellationToken cancellationToken = default)
    {
        return ResendLatestReportCoreAsync(null, cancellationToken);
    }

    public Task<ReportScanResult> ResendLatestReportAsync(
        HumanConfirmationRecord confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return ResendLatestReportCoreAsync(confirmation, cancellationToken);
    }

    private async Task<ReportScanResult> ResendLatestReportCoreAsync(
        HumanConfirmationRecord? confirmation,
        CancellationToken cancellationToken)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var token = command.Token;
        _logService.Info("Manual resend requested for newest report only.", "GitHub");
        var branch = await CheckBranchAsync(token);
        await RunOptionalGitPullAsync(branch, token);
        var result = await _reportPoller.ScanAsync(
            Config,
            State,
            includeCompleted: true,
            cancellationToken: token);
        token.ThrowIfCancellationRequested();
        LogScanResult(result);

        if (result.FoundNewReport && result.Candidate is not null)
        {
            await BuildAndRecordWakePromptAsync(
                result.Candidate,
                isResend: true,
                ResendLatestReportAction,
                confirmation,
                token);
        }
        else
        {
            State.ClearChatGptWakePromptPreview(result.Message);
        }

        await SaveStateAsync();
        OnStateChanged();
        return result;
    }

    public async Task BaselineExistingReportsAsync(CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var token = command.Token;
        _logService.Info("Baseline Existing Reports requested.", "GitHub");
        var candidatesResult = await _reportPoller.GetCandidatesAsync(Config, token);
        token.ThrowIfCancellationRequested();
        if (!candidatesResult.Success)
        {
            State.ErrorCount++;
            _logService.Error($"Baseline failed: {candidatesResult.Message}", "GitHub");
            await SaveStateAsync();
            OnStateChanged();
            return;
        }

        if (candidatesResult.Candidates.Count == 0)
        {
            _logService.Info("Baseline found no existing reports.", "GitHub");
            State.ClearChatGptWakePromptPreview("Baseline found no existing reports.");
            await SaveStateAsync();
            OnStateChanged();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var report in candidatesResult.Candidates)
        {
            token.ThrowIfCancellationRequested();
            State.MarkReportBaselineSeen(report, now);
        }

        var newest = candidatesResult.Candidates[0];
        var identity = ReportIdentity.FromCandidate(newest);
        State.LastPollCycleAtUtc = now;
        State.LastActionableLane = PollActionLane.None.ToString();
        State.LastPollCycleSummary = $"Baselined {candidatesResult.Candidates.Count} existing report(s). Newest={newest.FileName} sortKey={identity.SortKey}";
        State.ClearChatGptWakePromptPreview(State.LastPollCycleSummary);
        _logService.Info(State.LastPollCycleSummary, "GitHub");
        _logService.Info($"Highest report seen: {State.HighestReportFileNameSeen} sortKey={State.HighestReportSortKeySeen} workItem={State.HighestReportWorkItemSeen}", "GitHub");
        await SaveStateAsync();
        OnStateChanged();
    }

    public Task<ReportScanResult> WakeNewestReportOnceAsync(CancellationToken cancellationToken = default)
    {
        return WakeNewestReportOnceCoreAsync(null, cancellationToken);
    }

    public Task<ReportScanResult> WakeNewestReportOnceAsync(
        HumanConfirmationRecord confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return WakeNewestReportOnceCoreAsync(confirmation, cancellationToken);
    }

    private async Task<ReportScanResult> WakeNewestReportOnceCoreAsync(
        HumanConfirmationRecord? confirmation,
        CancellationToken cancellationToken)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        var token = command.Token;
        _logService.Info("Wake Newest Report Once requested.", "GitHub");
        var candidatesResult = await _reportPoller.GetCandidatesAsync(Config, token);
        token.ThrowIfCancellationRequested();
        if (!candidatesResult.Success)
        {
            State.ErrorCount++;
            _logService.Error($"Wake newest failed: {candidatesResult.Message}", "GitHub");
            await SaveStateAsync();
            OnStateChanged();
            return new ReportScanResult(false, false, false, null, null, candidatesResult.Message);
        }

        if (candidatesResult.Candidates.Count == 0)
        {
            const string message = "Wake newest found no reports.";
            _logService.Info(message, "GitHub");
            State.ClearChatGptWakePromptPreview(message);
            await SaveStateAsync();
            OnStateChanged();
            return new ReportScanResult(false, false, false, null, null, message);
        }

        var newest = candidatesResult.Candidates[0];
        LogNewestReportDecision(newest);
        if (State.WasReportWoken(newest.FileName))
        {
            var duplicate = $"Wake newest suppressed; newest report was already woken: {newest.FileName}";
            _logService.Warning(duplicate, "GitHub");
            State.ClearChatGptWakePromptPreview(duplicate);
            await SaveStateAsync();
            OnStateChanged();
            return new ReportScanResult(true, false, true, null, newest, duplicate);
        }

        await BuildAndRecordWakePromptAsync(
            newest,
            isResend: false,
            WakeNewestReportAction,
            confirmation,
            token);
        await SaveStateAsync();
        OnStateChanged();
        return new ReportScanResult(true, true, false, newest, newest, $"Wake newest attempted: {newest.RelativePath}");
    }

    public EnvelopeCaptureResult CaptureEnvelopeFromText(string rawText)
    {
        const string message = "Direct text capture is diagnostic-only and cannot authorize or persist an instruction.";
        _logService.Warning(message, "Safety");
        return new EnvelopeCaptureResult(false, null, message);
    }

    public SafetyValidationResult PrepareHumanConfirmedWakeTransaction(
        ConversationLineageSnapshot preWakeSnapshot,
        ReportCandidate report,
        string intendedActiveTask,
        string wakeToken)
    {
        return new SafetyValidationResult(
            false,
            false,
            "A source-bound HumanConfirmationRecord is required; boolean or implicit confirmation is rejected.");
    }

    public SafetyValidationResult PrepareHumanConfirmedWakeTransaction(
        ConversationLineageSnapshot preWakeSnapshot,
        ReportCandidate report,
        string intendedActiveTask,
        string wakeToken,
        string action,
        string exactPrompt,
        HumanConfirmationRecord confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!TryValidateHumanConfirmation(
                confirmation,
                action,
                report,
                exactPrompt,
                DateTimeOffset.UtcNow,
                reserve: false,
                out var confirmationReason))
        {
            return new SafetyValidationResult(false, false, confirmationReason);
        }

        if (WatcherSafetyPolicy.ResolveStage(Config) < WatcherOperatingStage.Stage2SignedDryRun)
        {
            return new SafetyValidationResult(false, false, "Stage 1 detect-only prohibits wake transaction preparation.");
        }

        if (!preWakeSnapshot.ApiVerified || !preWakeSnapshot.BrowserBackendAgree ||
            string.IsNullOrWhiteSpace(preWakeSnapshot.ConversationId) ||
            string.IsNullOrWhiteSpace(preWakeSnapshot.CurrentNode) ||
            string.IsNullOrWhiteSpace(preWakeSnapshot.BrowserTabIdentity))
        {
            return new SafetyValidationResult(false, true, BranchLineageSafetyService.DivergenceWarning + ": pre-wake lineage is incomplete or inconsistent.");
        }

        var ancestry = BranchLineageSafetyService.BuildAncestry(preWakeSnapshot, preWakeSnapshot.CurrentNode).ToList();
        if (ancestry.Count == 0 || !preWakeSnapshot.BrowserVisibleMessageIds.Contains(preWakeSnapshot.CurrentNode, StringComparer.Ordinal))
        {
            return new SafetyValidationResult(false, true, BranchLineageSafetyService.DivergenceWarning + ": current_node is not on the browser-visible branch.");
        }

        var replay = _transactionReplayGuardService.TryReserveWakeToken(State, wakeToken);
        if (!replay.Eligible)
        {
            return replay;
        }

        State.WakeTransaction = new WakeTransactionRecord
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            Nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            ConversationId = preWakeSnapshot.ConversationId,
            CurrentNodeBeforeWake = preWakeSnapshot.CurrentNode,
            VisibleBranchAncestry = ancestry,
            VisibleParentMessageId = preWakeSnapshot.CurrentNode,
            BrowserTabIdentity = preWakeSnapshot.BrowserTabIdentity,
            WakeToken = wakeToken,
            IntendedSourceReport = report.FileName,
            IntendedActiveTask = intendedActiveTask,
            HumanConfirmed = true,
            Status = $"human-confirmed-preflight:{confirmation.Nonce}"
        };
        return new SafetyValidationResult(true, false, "Human-confirmed wake transaction prepared; no wake has been posted.");
    }

    public SafetyValidationResult ValidateAssistantResponseForHumanDisplay(
        ConversationLineageSnapshot snapshot,
        AssistantResponseObservation response)
    {
        if (State.WakeTransaction is null)
        {
            return new SafetyValidationResult(false, false, "No bound wake transaction exists.");
        }

        var result = _branchLineageSafetyService.Validate(State.WakeTransaction, snapshot, response);
        State.BranchDivergenceDetected = result.BranchDivergence;
        State.TransactionAudit = new TransactionAuditState
        {
            ConversationId = snapshot.ConversationId,
            CurrentNode = snapshot.CurrentNode,
            WakeMessageId = State.WakeTransaction.WakeMessageId,
            ResponseMessageId = response.MessageId,
            ResponseParentId = response.ParentMessageId,
            OnCurrentPath = response.OnCurrentPath,
            CaptureMethod = response.CaptureMethod,
            FallbackBody = response.FallbackBody,
            ApiVerification = response.ApiVerified,
            EnvelopeTaskId = result.EnvelopeTaskId,
            EnvelopeSha256 = result.EnvelopeSha256,
            DeliveryMode = Config.InstructionDeliveryMode,
            HumanConfirmation = State.WakeTransaction.HumanConfirmed,
            ActiveTask = State.ActiveTaskLock.ActiveTaskId,
            TerminalReportStatus = State.TransactionAudit.TerminalReportStatus,
            EligibilityResult = result.Eligible ? "Eligible for human display only" : "Rejected: " + result.Reason,
            VisibleWarning = result.BranchDivergence ? BranchLineageSafetyService.DivergenceWarning : string.Empty
        };

        if (result.Eligible)
        {
            var deliveryMode = Enum.TryParse<AuthorizedInstructionDeliveryMode>(Config.InstructionDeliveryMode, true, out var parsedMode)
                ? parsedMode
                : AuthorizedInstructionDeliveryMode.HashBoundFile;
            State.LatestInstructionProvenance = _branchLineageSafetyService.BuildProvenance(
                State.WakeTransaction,
                snapshot,
                response,
                result,
                deliveryMode,
                Config.CodexThreadId);
        }
        else
        {
            State.LatestInstructionProvenance = null;
            if (result.BranchDivergence)
            {
                State.WakeTransaction.Status = "branch-divergence-terminal";
                _logService.Error(BranchLineageSafetyService.DivergenceWarning, "Safety");
            }
        }

        SaveState();
        OnStateChanged();
        return result;
    }

    public async Task<EnvelopeCaptureResult> CaptureLatestTaskFromChatGptAsync(
        bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        command.Token.ThrowIfCancellationRequested();
        const string message = "Instruction capture is disabled in Stage 2 signed dry-run. Whole-page and fallbackBody capture cannot authorize a task.";
        State.MarkCaptureFailed(message);
        _logService.Warning(message, "Safety");
        await SaveStateAsync();
        OnStateChanged();
        return new EnvelopeCaptureResult(false, null, message);
    }

    private async Task<EnvelopeCaptureResult> ReadLatestEnvelopeFromChatGptAsync(
        bool automatic,
        bool countFailures,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        const string message = "Automatic ChatGPT instruction capture is disabled; backend lineage validation and human display are separate Stage 2 operations.";
        return new EnvelopeCaptureResult(false, null, message);
    }

    public async Task<CodexSendResult> SendLatestTaskToCodexAsync(
        bool manualRetry = false,
        CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        command.Token.ThrowIfCancellationRequested();
        _logService.Info(SendLatestTaskToCodexRouteLog, "Route");
        WatcherSafetyPolicy.CanAutomaticallyDeliver(Config, out var message);
        _logService.Warning(message, "Safety");
        State.CodexDeliveryPending = false;
        await SaveStateAsync();
        OnStateChanged();
        return new CodexSendResult(false, State.PendingCodexTaskPath, message);
    }

    private bool ShouldReadChatGptEnvelopeDuringPoll()
    {
        return false;
    }

    private bool IsLateChatGptEnvelopeSweepDue()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(
            Math.Max(60, Config.PollSeconds),
            Config.ChatGptLateEnvelopeSweepSeconds));

        return State.LastCaptureAttemptAtUtc is null ||
               DateTimeOffset.UtcNow - State.LastCaptureAttemptAtUtc.Value >= interval;
    }

    public async Task TestChatGptWakeAsync(CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        command.Token.ThrowIfCancellationRequested();
        _logService.Info(TestChatGptWakeRouteLog, "Route");
        const string message = "Stage 2 signed dry-run prohibits test and production ChatGPT wakes.";
        _logService.Warning(message, "Safety");
        State.LastChatGptWakeResult = message;
        await SaveStateAsync();
        OnStateChanged();
    }

    public async Task<CodexSendResult> TestCodexWakeAsync(CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        command.Token.ThrowIfCancellationRequested();
        _logService.Info(TestCodexWakeRouteLog, "Route");
        WatcherSafetyPolicy.CanAutomaticallyDeliver(Config, out var message);
        _logService.Warning(message, "Safety");
        await SaveStateAsync();
        OnStateChanged();
        return new CodexSendResult(false, null, message);
    }

    public Task<WindowListResult> ListVisibleWindowsAsync(CancellationToken cancellationToken = default)
    {
        using var command = _lifecycle.BeginCommand(cancellationToken);
        command.Token.ThrowIfCancellationRequested();
        var result = _codexDirectorBridge.ListVisibleWindows(Config, _logService);
        command.Token.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    public void SaveConfig()
    {
        _ledgerService.EnsureLedger(Config);
        _configService.Save(Config);
        _logService.Info($"Config saved to {_configService.GetConfigPath(Config)}.", "Config");
    }

    public void Dispose()
    {
        Stop();
        _lifecycle.Dispose();
        _scanLock.Dispose();
    }

    private void SaveState()
    {
        _stateService.Save(Config, State);
        _logService.Info("State saved.", "State");
    }

    private Task SaveStateAsync()
    {
        SaveState();
        return Task.CompletedTask;
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunOptionalGitPullAsync(
        BranchGuardResult branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Config.GitPullBeforeScan)
        {
            _logService.Info("git pull skipped because Git pull before scan is disabled.", "Git");
            return;
        }

        if (branch.IsBlocked)
        {
            _logService.Warning($"git pull skipped because branch guard is blocking this repo: {branch.Message}", "Git");
            return;
        }

        _logService.Info($"git pull started: origin {Config.AllowedBranch} --ff-only", "Git");
        var pull = await _gitPullService.PullFastForwardOnlyAsync(Config, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (pull.Success)
        {
            _logService.Info($"{pull.Message} {OneLine(pull.Output)}", "Git");
        }
        else
        {
            State.ErrorCount++;
            var details = string.Join(" ", new[] { pull.Message, OneLine(pull.Output), OneLine(pull.Error) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            _logService.Error(details, "Git");
        }
    }

    private void LogScanResult(ReportScanResult result)
    {
        if (result.NewestCandidate is not null)
        {
            _logService.Info($"Newest report found: {result.NewestCandidate.RelativePath}", "GitHub");
        }

        if (result.DuplicateSuppressed && result.NewestCandidate is not null)
        {
            _logService.Info($"Report already completed: {result.NewestCandidate.RelativePath}", "GitHub");
        }
        else if (!result.FoundAnyReport)
        {
            _logService.Info($"No report found. {result.Message}", "GitHub");
        }
        else if (result.FoundNewReport && result.Candidate is not null)
        {
            _logService.Info($"New report selected: {result.Candidate.RelativePath}", "GitHub");
        }
        else
        {
            _logService.Info(result.Message, "GitHub");
        }
    }

    private void LogNewestReportDecision(ReportCandidate newest)
    {
        var identity = ReportIdentity.FromCandidate(newest);
        _logService.Info(
            $"Newest report candidate: file={identity.FileName} sortKey={identity.SortKey} highWater={State.HighestReportSortKeySeen} highWaterFile={State.HighestReportFileNameSeen} lastWoken={State.LastReportWokenFileName}",
            "GitHub");
    }

    private void RecordDetectedReport(ReportCandidate? report)
    {
        if (report is null)
        {
            State.LastDetectedGitHubReportTaskId = string.Empty;
            State.LastDetectedGitHubReportWorkItem = string.Empty;
            _logService.Info("Task ID parsed from report: none", "Poll");
            return;
        }

        var workItem = WorkItemIdParser.Parse(report.FileName);
        State.LastDetectedGitHubReportTaskId = workItem?.ToString() ?? string.Empty;
        State.LastDetectedGitHubReportWorkItem = workItem?.SortKey ?? string.Empty;
        _logService.Info(
            workItem is null
                ? $"Task ID parsed from report {report.FileName}: unknown"
                : $"Task ID parsed from report {report.FileName}: {workItem} sort={workItem.SortKey}",
            "Poll");
    }

    private void RecordDetectedEnvelope(CapturedTaskEnvelope envelope)
    {
        var workItem = WorkItemIdParser.Parse(envelope.TaskId);
        State.LastDetectedChatGptEnvelopeTaskId = envelope.TaskId;
        State.LastDetectedChatGptEnvelopeWorkItem = workItem?.SortKey ?? string.Empty;
        _logService.Info(
            workItem is null
                ? $"Task ID parsed from envelope {envelope.TaskId}: unknown"
                : $"Task ID parsed from envelope {envelope.TaskId}: {workItem} sort={workItem.SortKey}",
            "Poll");
    }

    private void RecordReportIngestion(ReportCandidate? candidate)
    {
        if (candidate is null)
        {
            State.LastReportIngestion = null;
            return;
        }

        var record = _reportIngestionVerifier.Verify(Config, State, candidate, DateTimeOffset.UtcNow);
        State.LastReportIngestion = record;
        State.ReportIngestionHistory.Add(record);
        if (State.ReportIngestionHistory.Count > 200)
        {
            State.ReportIngestionHistory.RemoveRange(0, State.ReportIngestionHistory.Count - 200);
        }

        State.TransactionAudit.ActiveTask = State.ActiveTaskLock.ActiveTaskId;
        State.TransactionAudit.TerminalReportStatus = record.Eligible
            ? $"Eligible: {record.ReportPath} sha256={record.ReportSha256}"
            : $"Rejected: {record.RejectionReason}";

        if (record.Eligible)
        {
            _logService.Info(
                $"Report authenticated. repo={record.Repository} branch={record.Branch} path={record.ReportPath} commit={record.ReportCommit} blob={record.ReportBlobIdentity} sha256={record.ReportSha256}",
                "ReportAuth");
        }
        else
        {
            _logService.Warning(
                $"Report ineligible. path={record.ReportPath} sha256={record.ReportSha256} reason={record.RejectionReason}",
                "ReportAuth");
        }
    }

    private bool TryValidateHumanConfirmation(
        HumanConfirmationRecord confirmation,
        string action,
        ReportCandidate report,
        string exactPrompt,
        DateTimeOffset nowUtc,
        bool reserve,
        out string reason)
    {
        var profileId = !string.IsNullOrWhiteSpace(Config.RuntimeProfileId)
            ? Config.RuntimeProfileId
            : Config.ActiveProfileId;
        return ValidateAndReserveHumanConfirmation(
            confirmation,
            action,
            report,
            profileId,
            exactPrompt,
            nowUtc,
            _usedHumanConfirmationNonces,
            _confirmationGate,
            reserve,
            out reason);
    }

    internal static bool ValidateAndReserveHumanConfirmation(
        HumanConfirmationRecord? confirmation,
        string action,
        ReportCandidate report,
        string profileId,
        string exactPrompt,
        DateTimeOffset nowUtc,
        ISet<string> usedNonces,
        object confirmationGate,
        bool reserve,
        out string reason)
    {
        if (confirmation is null)
        {
            reason = "HUMAN_CONFIRMATION_REQUIRED: no confirmation record was supplied.";
            return false;
        }

        if (!confirmation.Schema.Equals(HumanConfirmationRecord.SchemaName, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_SCHEMA_INVALID: unsupported confirmation schema.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(action) ||
            !confirmation.Action.Equals(action, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_ACTION_MISMATCH: confirmation is bound to a different action.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileId) ||
            !confirmation.ProfileId.Equals(profileId, StringComparison.Ordinal))
        {
            reason = "HUMAN_CONFIRMATION_PROFILE_MISMATCH: confirmation is bound to a different profile.";
            return false;
        }

        if (!confirmation.SourceReportFingerprint.Equals(report.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
            !NormalizeConfirmationPath(confirmation.SourceReportPath)
                .Equals(NormalizeConfirmationPath(report.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            reason = "HUMAN_CONFIRMATION_SOURCE_MISMATCH: confirmation is bound to a different report.";
            return false;
        }

        var promptHash = HumanConfirmationRecord.ComputePromptSha256(exactPrompt);
        if (!confirmation.PromptSha256.Equals(promptHash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "HUMAN_CONFIRMATION_PROMPT_MISMATCH: exact prompt hash does not match.";
            return false;
        }

        var now = nowUtc.ToUniversalTime();
        var issued = confirmation.IssuedAtUtc.ToUniversalTime();
        var expires = confirmation.ExpiresAtUtc.ToUniversalTime();
        if (issued == default || expires == default || expires <= issued ||
            expires - issued > TimeSpan.FromMinutes(15) ||
            issued > now.AddMinutes(1) ||
            issued < now.Subtract(TimeSpan.FromMinutes(15)) ||
            expires <= now)
        {
            reason = "HUMAN_CONFIRMATION_STALE: confirmation timestamp or expiry is invalid.";
            return false;
        }

        if (confirmation.Nonce.Length != 64 ||
            confirmation.Nonce.Any(character => !Uri.IsHexDigit(character)))
        {
            reason = "HUMAN_CONFIRMATION_NONCE_INVALID: nonce must be 32 random bytes encoded as hex.";
            return false;
        }

        lock (confirmationGate)
        {
            if (usedNonces.Contains(confirmation.Nonce))
            {
                reason = "HUMAN_CONFIRMATION_REPLAYED: confirmation nonce was already used.";
                return false;
            }

            if (reserve)
            {
                usedNonces.Add(confirmation.Nonce);
            }
        }

        reason = reserve
            ? "Human confirmation validated and reserved for one use."
            : "Human confirmation validated; reservation pending send.";
        return true;
    }

    internal static bool ValidatePreparedHumanAction(
        PreparedHumanAction? prepared,
        HumanConfirmationRecord? confirmation,
        WatcherProfileV1 currentProfile,
        DateTimeOffset nowUtc,
        ISet<string> usedNonces,
        object confirmationGate,
        bool reserve,
        out string reason)
    {
        if (prepared is null || !prepared.HasValidIntegrity())
        {
            reason = "HUMAN_ACTION_MUTATED: the prepared action integrity seal is invalid.";
            return false;
        }
        if (!prepared.MatchesProfile(currentProfile))
        {
            reason = "HUMAN_CONFIRMATION_PROFILE_BINDING_MISMATCH: profile, destination, or policy changed after preparation.";
            return false;
        }
        if (confirmation is null ||
            !confirmation.Nonce.Equals(prepared.Nonce, StringComparison.Ordinal) ||
            !confirmation.WakeToken.Equals(prepared.WakeToken, StringComparison.Ordinal) ||
            !confirmation.DirectorIdentity.Equals(prepared.DirectorIdentity, StringComparison.Ordinal) ||
            !confirmation.DestinationAdapterId.Equals(prepared.DestinationAdapterId, StringComparison.Ordinal) ||
            !confirmation.DestinationIdentity.Equals(prepared.DestinationIdentity, StringComparison.Ordinal) ||
            confirmation.PolicyKind != prepared.PolicyKind ||
            confirmation.PolicyGeneration != prepared.PolicyGeneration ||
            confirmation.RequireVisibleHumanApproval != prepared.RequireVisibleHumanApproval ||
            confirmation.IssuedAtUtc.ToUniversalTime() != prepared.IssuedAtUtc ||
            confirmation.ExpiresAtUtc.ToUniversalTime() != prepared.ExpiresAtUtc)
        {
            reason = "HUMAN_ACTION_MUTATED: confirmation fields do not match the prepared action.";
            return false;
        }

        return ValidateAndReserveHumanConfirmation(
            confirmation,
            prepared.Action,
            prepared.Report,
            prepared.ProfileId,
            prepared.Prompt,
            nowUtc,
            usedNonces,
            confirmationGate,
            reserve,
            out reason);
    }

    private static string NormalizeConfirmationPath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim('/');
    }

    private async Task BuildAndRecordWakePromptAsync(
        ReportCandidate report,
        bool isResend,
        string action,
        HumanConfirmationRecord? confirmation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = confirmation is not null && State.WakeTransaction is not null
            ? State.WakeTransaction.WakeToken
            : BuildWakeToken(report.Fingerprint[..Math.Min(12, report.Fingerprint.Length)]);
        var prompt = _wakePromptBuilder.Build(Config, report, token);
        var builtAtUtc = DateTimeOffset.UtcNow;
        State.RecordWakePromptBuilt(report, prompt, token, builtAtUtc);
        _logService.Info($"ChatGPT wake prompt built for {report.FileName}.", "ChatGPT");

        if (confirmation is null)
        {
            State.LastChatGptWakeResult = "A source-bound HumanConfirmationRecord is required; prompt preview only.";
            _logService.Warning(State.LastChatGptWakeResult, "Safety");
            return;
        }

        if (!TryValidateHumanConfirmation(
                confirmation,
                action,
                report,
                prompt,
                DateTimeOffset.UtcNow,
                reserve: false,
                out var confirmationReason))
        {
            State.LastChatGptWakeResult = confirmationReason;
            _logService.Warning(State.LastChatGptWakeResult, "Safety");
            return;
        }

        if (!WatcherSafetyPolicy.CanPostWake(Config, State.WakeTransaction, out var safetyReason))
        {
            State.LastChatGptWakeResult = safetyReason;
            _logService.Warning(State.LastChatGptWakeResult, "Safety");
            return;
        }

        if (!State.WakeTransaction!.IntendedSourceReport.Equals(report.FileName, StringComparison.OrdinalIgnoreCase))
        {
            State.LastChatGptWakeResult = "Prepared wake source report does not match the selected report.";
            _logService.Warning(State.LastChatGptWakeResult, "Safety");
            return;
        }

        if (!Config.SubmitChatGptPrompt)
        {
            _logService.Info("Submit ChatGPT prompt is unchecked; prompt preview only. Report remains uncompleted.", "ChatGPT");
            return;
        }

        if (!TryValidateHumanConfirmation(
                confirmation,
                action,
                report,
                prompt,
                DateTimeOffset.UtcNow,
                reserve: true,
                out confirmationReason))
        {
            State.LastChatGptWakeResult = confirmationReason;
            _logService.Warning(State.LastChatGptWakeResult, "Safety");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _chatGptDirectorBridge.SendPromptAsync(
            prompt,
            Config,
            State,
            cancellationToken,
            _logService);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Success)
        {
            var completedAtUtc = State.LastChatGptWakeSentAtUtc ?? DateTimeOffset.UtcNow;
            var record = State.MarkReportCompleted(report, prompt, completedAtUtc, isResend, result.Token);
            State.ReportWakePending = false;
            State.LastActionableLane = PollActionLane.None.ToString();
            State.LastPollCycleSummary = $"ChatGPT wake confirmed for {report.FileName}; capture pending.";
            _logService.Info($"Report marked completed after confirmed ChatGPT wake; wake count {record.WakeCount}.", "ChatGPT");
            await SaveStateAsync();
            OnStateChanged();

            if (Config.AutoCaptureChatGptEnvelope)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captureResult = await CaptureLatestTaskFromChatGptAsync(
                    automatic: true,
                    cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var capturedCurrentReport = captureResult.Success &&
                                            captureResult.Envelope is not null &&
                                            State.IsSourceReportMatch(report, captureResult.Envelope.SourceReport);
                State.ReportWakePending = false;
                State.CapturePending = false;
                State.CodexDeliveryPending = !string.IsNullOrWhiteSpace(State.PendingCodexTaskId) &&
                                             !State.IsCodexTaskSent(State.PendingCodexTaskId);
                State.LastActionableLane = PollActionLane.None.ToString();
                if (capturedCurrentReport)
                {
                    State.LastPollCycleSummary = State.CodexDeliveryPending
                        ? $"ChatGPT wake confirmed for {report.FileName}; captured {captureResult.Envelope!.TaskId} and Codex delivery is pending."
                        : $"ChatGPT wake confirmed for {report.FileName}; captured {captureResult.Envelope!.TaskId} and Codex delivery is complete.";
                }
                else
                {
                    State.LastPollCycleSummary = $"ChatGPT wake confirmed for {report.FileName}; capture failed: {captureResult.Message}. Report will retry after failed-capture cooldown.";
                    _logService.Warning(State.LastPollCycleSummary, "ChatGPT");
                }

                await SaveStateAsync();
                OnStateChanged();
            }

            return;
        }

        if (result.Busy)
        {
            var busyFor = State.RecordReportBusy(report, DateTimeOffset.UtcNow);
            _logService.Warning($"ChatGPT wake deferred because Director appears busy; report remains uncompleted. Busy duration for report: {busyFor.TotalSeconds:N0}s.", "ChatGPT");
            if (Config.ChatGptStopIfBusyAfterTimeout && busyFor.TotalSeconds >= Config.ChatGptBusyRecoverySeconds)
            {
                _logService.Warning("Busy recovery threshold reached. Stop-if-busy is enabled, but this first implementation does not click Stop automatically.", "ChatGPT");
            }

            return;
        }

        State.ErrorCount++;
        _logService.Error("ChatGPT wake was not confirmed; report remains uncompleted.", "ChatGPT");
    }

    private async Task<(bool Delivered, string Message)> ExecuteFrozenWakePromptAsync(
        PreparedHumanAction prepared,
        bool isResend,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = prepared.Report;
        var prompt = prepared.Prompt;
        State.RecordWakePromptBuilt(report, prompt, prepared.WakeToken, prepared.IssuedAtUtc);

        if (!WatcherSafetyPolicy.CanPostWake(Config, State.WakeTransaction, out var safetyReason))
        {
            State.LastChatGptWakeResult = safetyReason;
            _logService.Warning($"HUMAN_ACTION_DELIVERY_BLOCKED nonce={prepared.Nonce} reason={safetyReason}", "HumanConfirmation");
            return (false, safetyReason);
        }
        if (!State.WakeTransaction!.WakeToken.Equals(prepared.WakeToken, StringComparison.Ordinal) ||
            !State.WakeTransaction.IntendedSourceReport.Equals(report.FileName, StringComparison.OrdinalIgnoreCase))
        {
            State.LastChatGptWakeResult = "Prepared wake transaction does not match the frozen token and source report.";
            _logService.Warning($"HUMAN_ACTION_DELIVERY_BLOCKED nonce={prepared.Nonce} reason={State.LastChatGptWakeResult}", "HumanConfirmation");
            return (false, State.LastChatGptWakeResult);
        }
        if (!Config.SubmitChatGptPrompt)
        {
            State.LastChatGptWakeResult = "Submit ChatGPT prompt is unchecked; confirmed prompt remains preview-only.";
            _logService.Warning($"HUMAN_ACTION_DELIVERY_BLOCKED nonce={prepared.Nonce} reason={State.LastChatGptWakeResult}", "HumanConfirmation");
            return (false, State.LastChatGptWakeResult);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logService.Info(
            $"HUMAN_ACTION_DELIVERY_ATTEMPTED nonce={prepared.Nonce} report_sha256={report.Fingerprint} prompt_sha256={prepared.PromptSha256}",
            "HumanConfirmation");
        var result = await _chatGptDirectorBridge.SendPromptAsync(
            prompt,
            Config,
            State,
            cancellationToken,
            _logService);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Success)
        {
            var completedAtUtc = State.LastChatGptWakeSentAtUtc ?? DateTimeOffset.UtcNow;
            var record = State.MarkReportCompleted(report, prompt, completedAtUtc, isResend, result.Token);
            State.ReportWakePending = false;
            State.LastActionableLane = PollActionLane.None.ToString();
            State.LastPollCycleSummary = $"ChatGPT wake confirmed for {report.FileName}; capture pending.";
            _logService.Info($"HUMAN_ACTION_DELIVERED nonce={prepared.Nonce} wake_count={record.WakeCount}", "HumanConfirmation");
            await SaveStateAsync();
            OnStateChanged();

            if (Config.AutoCaptureChatGptEnvelope)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captureResult = await CaptureLatestTaskFromChatGptAsync(
                    automatic: true,
                    cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var capturedCurrentReport = captureResult.Success &&
                                            captureResult.Envelope is not null &&
                                            State.IsSourceReportMatch(report, captureResult.Envelope.SourceReport);
                State.ReportWakePending = false;
                State.CapturePending = false;
                State.CodexDeliveryPending = !string.IsNullOrWhiteSpace(State.PendingCodexTaskId) &&
                                             !State.IsCodexTaskSent(State.PendingCodexTaskId);
                State.LastActionableLane = PollActionLane.None.ToString();
                State.LastPollCycleSummary = capturedCurrentReport
                    ? State.CodexDeliveryPending
                        ? $"ChatGPT wake confirmed for {report.FileName}; captured {captureResult.Envelope!.TaskId} and Codex delivery is pending."
                        : $"ChatGPT wake confirmed for {report.FileName}; captured {captureResult.Envelope!.TaskId} and Codex delivery is complete."
                    : $"ChatGPT wake confirmed for {report.FileName}; capture failed: {captureResult.Message}. Report will retry after failed-capture cooldown.";
                if (!capturedCurrentReport)
                {
                    _logService.Warning(State.LastPollCycleSummary, "ChatGPT");
                }
                await SaveStateAsync();
                OnStateChanged();
            }
            return (true, $"Confirmed prepared action delivered without repolling: {report.RelativePath}");
        }

        if (result.Busy)
        {
            var busyFor = State.RecordReportBusy(report, DateTimeOffset.UtcNow);
            _logService.Warning(
                $"HUMAN_ACTION_DELIVERY_BUSY nonce={prepared.Nonce} busy_seconds={busyFor.TotalSeconds:N0}; report remains uncompleted.",
                "HumanConfirmation");
            return (false, "Director is busy; the frozen prepared action was not delivered.");
        }

        State.ErrorCount++;
        _logService.Error($"HUMAN_ACTION_DELIVERY_FAILED nonce={prepared.Nonce}; report remains uncompleted.", "HumanConfirmation");
        return (false, "The frozen prepared action was not confirmed delivered.");
    }

    private ReportScanResult CancelledPollResult(string reason)
    {
        var message = $"Poll cycle cancelled by operator stop. reason={reason}";
        State.LastPollCycleSummary = message;
        State.LastActionableLane = PollActionLane.None.ToString();
        State.ReportWakePending = false;
        State.CapturePending = false;
        _logService.Warning(message, "Poll");
        OnStateChanged();
        return new ReportScanResult(false, false, false, null, null, message);
    }

    private static string OneLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.ReplaceLineEndings(" ").Trim();
    }

    private static string BuildWakeToken(string fingerprintPrefix)
    {
        var safePrefix = string.IsNullOrWhiteSpace(fingerprintPrefix) ? "test" : fingerprintPrefix;
        return $"DCS_WATCHER_V2_WAKE:{safePrefix}:{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
    }

    private void PersistCapturedEnvelope(CapturedTaskEnvelope envelope)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        envelope.CapturedAtUtc = capturedAtUtc;
        var envelopePath = _ledgerService.SaveEnvelope(Config, envelope);
        var taskPath = _ledgerService.SaveTaskFile(Config, envelope);
        var metadataPath = _ledgerService.SaveTaskMetadata(Config, envelope, envelopePath, taskPath, capturedAtUtc);

        _latestEnvelope = envelope;
        State.MarkTaskCaptured(envelope, envelopePath, taskPath, metadataPath, capturedAtUtc);
        _logService.Info($"Captured task saved. task_id={envelope.TaskId} envelope={envelopePath} task={taskPath} metadata={metadataPath}", "Envelope");
    }

    private void LogEnvelopeDiagnostics(EnvelopeExtractionDiagnostics? diagnostics, string? selectedTaskId = null)
    {
        if (diagnostics is null)
        {
            return;
        }

        var selected = !string.IsNullOrWhiteSpace(selectedTaskId)
            ? selectedTaskId
            : diagnostics.SelectedTaskId;
        var rejection = string.IsNullOrWhiteSpace(diagnostics.RejectionReason)
            ? "none"
            : diagnostics.RejectionReason;
        _logService.Info(
            $"Envelope diagnostics: scope={diagnostics.CaptureScope} assistantMessages={diagnostics.AssistantMessageCount} selectedAssistantIndex={diagnostics.SelectedAssistantMessageIndex} selectedAssistantEnvelopeCount={diagnostics.SelectedAssistantEnvelopeCount} assistantEnvelopeCount={diagnostics.AssistantEnvelopeCount} bodyEnvelopeCount={diagnostics.BodyEnvelopeCount} selectedEnvelopeCount={diagnostics.EnvelopeCount} fallbackBody={diagnostics.UsedBodyFallback} selectedTaskId={selected} skippedDuplicateTaskIds={diagnostics.SkippedDuplicateTaskIds} rejection={rejection}",
            "Envelope");
    }
}

internal sealed class WatcherLifecycleController : IDisposable
{
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _retiredSources = [];
    private CancellationTokenSource? _currentSource = new();
    private bool _disposed;

    public CancellationToken CurrentToken
    {
        get
        {
            lock (_gate)
            {
                return _currentSource?.Token ?? default;
            }
        }
    }

    public CancellationToken Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentSource is { IsCancellationRequested: false })
            {
                return _currentSource.Token;
            }

            if (_currentSource is not null)
            {
                _retiredSources.Add(_currentSource);
            }
            _currentSource = new CancellationTokenSource();
            return _currentSource.Token;
        }
    }

    public WatcherCommandScope BeginCommand(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentSource is null || _currentSource.IsCancellationRequested)
            {
                if (_currentSource is not null)
                {
                    _retiredSources.Add(_currentSource);
                }
                _currentSource = new CancellationTokenSource();
            }

            var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_currentSource.Token, cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_currentSource.Token);
            return new WatcherCommandScope(linked);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _currentSource;
        }
        source?.Cancel();
    }

    public void Dispose()
    {
        List<CancellationTokenSource> sources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            sources = [.. _retiredSources];
            if (_currentSource is not null)
            {
                sources.Add(_currentSource);
            }
            _retiredSources.Clear();
            _currentSource = null;
        }

        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            finally
            {
                source.Dispose();
            }
        }
    }
}

internal sealed class WatcherCommandScope : IDisposable
{
    private readonly CancellationTokenSource _source;

    internal WatcherCommandScope(CancellationTokenSource source)
    {
        _source = source;
    }

    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        _source.Dispose();
    }
}
