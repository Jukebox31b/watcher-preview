using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record CodexSendResult(
    bool Success,
    string? TaskFilePath,
    string Message);

public sealed record CodexDeliveryPayload(
    bool Success,
    string Prompt,
    string PromptMode,
    string TaskFilePath,
    string Message);

public sealed class CodexDirectorBridge
{
    private static readonly ConcurrentDictionary<int, Process> ActiveCliRecoveries = new();
    private static readonly object CliRecoveryLogLock = new();
    public const string IpcOnlyTransport = "IpcOnly";
    public const string IpcThenUiFallbackTransport = "IpcThenUiFallback";
    public const string UiPasteOnlyTransport = "UiPasteOnly";
    public const string VerbatimIpcMode = "VerbatimIpc";
    public const string FileHandoffMode = "FileHandoff";
    public const string FullPasteMode = "FullPaste";
    public const string UiPasteFallbackMode = "UiPasteFallback";
    public const string IpcThenUiFallbackMode = "IpcThenUiFallback";
    public const string TestCodexWakeIpcRouteLog = "[ROUTE] TestCodexWake -> CodexDirectorBridge -> CodexIpcClient";
    public const string SendLatestTaskToCodexIpcRouteLog = "[ROUTE] SendLatestTaskToCodex -> CodexDirectorBridge -> CodexIpcClient";

    private readonly BranchGuardService _branchGuardService;
    private readonly LedgerService _ledgerService;
    private readonly CodexIpcClient _codexIpcClient;

    public CodexDirectorBridge(BranchGuardService branchGuardService, LedgerService ledgerService)
        : this(branchGuardService, ledgerService, new CodexIpcClient())
    {
    }

    public CodexDirectorBridge(
        BranchGuardService branchGuardService,
        LedgerService ledgerService,
        CodexIpcClient codexIpcClient)
    {
        _branchGuardService = branchGuardService;
        _ledgerService = ledgerService;
        _codexIpcClient = codexIpcClient;
    }

    public async Task<CodexSendResult> SendPendingTaskAsync(
        AppConfig config,
        AppState state,
        LogService log,
        CancellationToken cancellationToken = default)
    {
        if (!WatcherSafetyPolicy.CanAutomaticallyDeliver(config, out var safetyReason))
        {
            log.Warning(safetyReason, "Safety");
            return new CodexSendResult(false, state.PendingCodexTaskPath, safetyReason);
        }

        var taskId = state.PendingCodexTaskId;
        var transport = ResolveDeliveryTransport(config);

        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(state.PendingCodexTaskPath))
        {
            var missing = "No pending Codex task exists.";
            log.Error(missing, "Codex");
            state.MarkCodexDeliveryFailed(taskId, transport, string.Empty, missing);
            return new CodexSendResult(false, null, missing);
        }

        if (state.IsCodexTaskSent(taskId))
        {
            var duplicate = $"Codex delivery refused; task_id already sent: {taskId}";
            log.Warning(duplicate, "Codex");
            state.MarkCodexDeliveryFailed(taskId, transport, state.LastCodexHandoffPromptPath, duplicate);
            return new CodexSendResult(false, state.PendingCodexTaskPath, duplicate);
        }

        var pendingRecord = GetPendingCapturedTaskRecord(state, taskId);
        if (state.IsStaleCodexTask(pendingRecord, out var staleReason))
        {
            var stale = $"Codex delivery refused; stale or repeated captured task: {taskId}. {staleReason}";
            log.Warning(stale, "Codex");
            state.MarkCodexTaskSuppressed(taskId, staleReason);
            state.MarkCodexDeliveryFailed(taskId, transport, state.LastCodexHandoffPromptPath, stale);
            return new CodexSendResult(false, state.PendingCodexTaskPath, stale);
        }

        var payload = BuildPendingTaskDeliveryPayload(config, state);
        if (!payload.Success)
        {
            log.Error(payload.Message, "Codex");
            state.MarkCodexDeliveryFailed(taskId, transport, string.Empty, payload.Message);
            return new CodexSendResult(false, state.PendingCodexTaskPath, payload.Message);
        }

        var mode = payload.PromptMode;
        var modeLabel = $"{mode}/{transport}";
        var prompt = payload.Prompt;
        var handoffPath = _ledgerService.SaveCodexHandoffPrompt(config, taskId, prompt);
        state.MarkCodexDeliveryAttempt(taskId, modeLabel, prompt, handoffPath, $"DELIVERY_ATTEMPTED: {mode} prompt built.");
        log.Info(
            mode.Equals(FileHandoffMode, StringComparison.OrdinalIgnoreCase) || mode.Equals(FullPasteMode, StringComparison.OrdinalIgnoreCase)
                ? $"Codex file-handoff prompt saved: {handoffPath}"
                : $"Codex verbatim envelope prompt saved for audit: {handoffPath}",
            "Codex");

        var delivery = await DeliverPromptAsync(
            config,
            state,
            taskId,
            prompt,
            handoffPath,
            mode,
            SendLatestTaskToCodexIpcRouteLog,
            log,
            cancellationToken);
        if (!delivery.Success)
        {
            if (delivery.IpcRequestSent)
            {
                var suppressedRetryMessage =
                    $"DELIVERY_UNCONFIRMED_SUPPRESSED: Codex IPC request was sent for {taskId}, but confirmation failed; automatic retry suppressed. {delivery.Message}";
                log.Warning(suppressedRetryMessage, "Codex");
                state.MarkCodexDeliveryRequestSentUnconfirmed(
                    taskId,
                    $"{mode}/{delivery.TransportUsed}",
                    handoffPath,
                    suppressedRetryMessage);
                return new CodexSendResult(false, payload.TaskFilePath, suppressedRetryMessage);
            }

            state.MarkCodexDeliveryFailed(taskId, $"{mode}/{delivery.TransportUsed}", handoffPath, delivery.Message);
            return new CodexSendResult(false, payload.TaskFilePath, delivery.Message);
        }

        state.MarkCodexDeliverySucceeded(taskId, $"{mode}/{delivery.TransportUsed}", handoffPath, delivery.Message);
        log.Info(delivery.Message, "Codex");
        return new CodexSendResult(true, payload.TaskFilePath, delivery.Message);
    }

    public CodexDeliveryPayload BuildPendingTaskDeliveryPayload(AppConfig config, AppState state)
    {
        var taskId = state.PendingCodexTaskId;
        var mode = ResolvePromptDeliveryMode(config);

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new CodexDeliveryPayload(false, string.Empty, mode, string.Empty, "Pending task id is empty.");
        }

        if (IsVerbatimDeliveryMode(mode))
        {
            var rawEnvelope = LoadRawCapturedEnvelope(config, state, taskId);
            if (!rawEnvelope.Success)
            {
                return rawEnvelope;
            }

            return rawEnvelope with { PromptMode = mode };
        }

        var validation = ValidatePendingTaskFile(config, state.PendingCodexTaskPath, state.PendingCodexTaskId);
        if (!validation.Success)
        {
            return new CodexDeliveryPayload(false, string.Empty, mode, state.PendingCodexTaskPath, validation.Message);
        }

        var prompt = BuildCodexHandoffPrompt(
            config,
            taskId,
            validation.TaskFilePath,
            validation.MetadataFilePath,
            validation.TaskFileSha256,
            mode,
            fullPasteText: mode.Equals(FullPasteMode, StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllText(validation.TaskFilePath)
                : null);

        return new CodexDeliveryPayload(true, prompt, mode, validation.TaskFilePath, "File handoff prompt built.");
    }

    public async Task<CodexSendResult> SendTestWakeAsync(
        AppConfig config,
        AppState state,
        LogService log,
        CancellationToken cancellationToken = default)
    {
        if (!WatcherSafetyPolicy.CanAutomaticallyDeliver(config, out var safetyReason))
        {
            log.Warning(safetyReason, "Safety");
            return new CodexSendResult(false, null, safetyReason);
        }

        var taskId = $"TEST-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var transport = ResolveDeliveryTransport(config);
        var prompt = "DCS Watcher v2 test wake. No action required.";
        var handoffPath = _ledgerService.SaveCodexHandoffPrompt(config, taskId, prompt);
        state.MarkCodexDeliveryAttempt(taskId, $"Test/{transport}", prompt, handoffPath, "DELIVERY_ATTEMPTED: test prompt built.");
        log.Info($"Codex test handoff prompt saved: {handoffPath}", "Codex");

        var delivery = await DeliverPromptAsync(
            config,
            state,
            taskId,
            prompt,
            handoffPath,
            "Test",
            TestCodexWakeIpcRouteLog,
            log,
            cancellationToken);
        if (!delivery.Success)
        {
            state.MarkCodexDeliveryFailed(taskId, $"Test/{delivery.TransportUsed}", handoffPath, delivery.Message);
            return new CodexSendResult(false, null, delivery.Message);
        }

        state.MarkCodexDeliverySucceeded(taskId, $"Test/{delivery.TransportUsed}", handoffPath, delivery.Message);
        log.Info(delivery.Message, "Codex");
        return new CodexSendResult(true, null, delivery.Message);
    }

    public string BuildCodexHandoffPrompt(
        AppConfig config,
        string taskId,
        string absoluteTaskFilePath,
        string absoluteMetadataFilePath,
        string taskFileSha256,
        string deliveryMode,
        string? fullPasteText = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Wake up. Read and execute this DCS Watcher v2 captured ChatGPT Director task file.");
        builder.AppendLine();
        builder.AppendLine("Task ID:");
        builder.AppendLine(taskId);
        builder.AppendLine();
        builder.AppendLine("Task file:");
        builder.AppendLine(absoluteTaskFilePath);
        builder.AppendLine();
        builder.AppendLine("Task file SHA256:");
        builder.AppendLine(taskFileSha256);
        builder.AppendLine();
        builder.AppendLine("Metadata file:");
        builder.AppendLine(absoluteMetadataFilePath);
        builder.AppendLine();
        builder.AppendLine("Repo/worktree:");
        builder.AppendLine(config.LocalRepoPath);
        builder.AppendLine();
        builder.AppendLine("Required branch:");
        builder.AppendLine(config.AllowedBranch);
        builder.AppendLine();
        builder.AppendLine("Important:");
        builder.AppendLine("- Work only from the chatgpt-codex-bridge-app branch/worktree above.");
        builder.AppendLine("- Do not touch main.");
        builder.AppendLine("- Do not create chatgpt-bridge/inbox_to_codex files.");
        builder.AppendLine("- Consume the full instruction from the task file exactly as written.");
        builder.AppendLine("- When finished, write the required Codex Director report to:");
        builder.AppendLine("  chatgpt-bridge/reports_from_codex");
        builder.AppendLine("  using the report path specified inside the task file.");
        builder.AppendLine("- Do not wake another worker after publishing the GPT-facing report unless the task file explicitly authorizes it.");
        builder.AppendLine();
        builder.AppendLine("After reading the task file, acknowledge the Task ID you are executing before beginning work.");

        if (deliveryMode.Equals("FullPaste", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(fullPasteText))
        {
            builder.AppendLine();
            builder.AppendLine("Full task text follows because delivery mode is FullPaste:");
            builder.AppendLine();
            builder.AppendLine(fullPasteText);
        }

        return builder.ToString();
    }

    public Task<WindowSearchResult> TryFindCodexWindowAsync(AppConfig config)
    {
        return Task.FromResult(TryFindCodexWindow(config));
    }

    public WindowListResult ListVisibleWindows(AppConfig config, LogService log)
    {
        var entries = EnumerateVisibleWindows();
        var lines = entries.Select(FormatWindow).ToArray();
        var logsPath = _ledgerService.GetLogsPath(config);
        Directory.CreateDirectory(logsPath);
        var path = Path.Combine(logsPath, $"window-list-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllLines(path, lines);

        log.Info($"Visible top-level windows ({entries.Count}) saved to {path}", "Windows");
        foreach (var line in lines)
        {
            log.Info(line, "Windows");
        }

        log.Info("If Codex wake fails, choose a unique substring from the actual Codex window title and set Codex window title contains.", "Windows");
        return new WindowListResult(entries, path);
    }

    private async Task<CodexDeliveryAttemptResult> DeliverPromptAsync(
        AppConfig config,
        AppState state,
        string taskId,
        string prompt,
        string handoffPath,
        string promptMode,
        string ipcRouteLog,
        LogService log,
        CancellationToken cancellationToken)
    {
        var transport = ResolveDeliveryTransport(config);
        state.LastCodexDeliveryTransportUsed = transport;
        state.LastCodexThreadId = config.CodexThreadId;
        state.LastCodexFallbackUsed = false;

        if (ShouldAttemptIpc(config))
        {
            var ipcGuard = RequireCodexThreadIdForIpc(config, state, log, taskId, transport);
            if (ipcGuard is not null)
            {
                return new CodexDeliveryAttemptResult(false, ipcGuard.Message, transport, FallbackUsed: false);
            }

            log.Info(ipcRouteLog, "Route");
            state.MarkCodexIpcAttempt(config.CodexThreadId, transport);
            var ipc = await _codexIpcClient.StartTurnAsync(config, prompt, log, cancellationToken);
            RecordIpcResult(state, ipc);

            if (ipc.Confirmed)
            {
                var message = $"DELIVERY_CONFIRMED: Codex IPC confirmed turn start for thread {config.CodexThreadId}.";
                return new CodexDeliveryAttemptResult(true, message, "IPC", FallbackUsed: false);
            }

            if (IsNoOwnerRejection(ipc) && config.CodexCliFallbackOnNoOwner)
            {
                var cliRecovery = await TryStartCodexCliRecoveryAsync(
                    config,
                    taskId,
                    handoffPath,
                    log,
                    cancellationToken);
                if (cliRecovery.Success)
                {
                    return new CodexDeliveryAttemptResult(
                        true,
                        cliRecovery.Message,
                        "CodexCliRecovery",
                        FallbackUsed: true);
                }

                log.Warning(cliRecovery.Message, "Codex");
                return new CodexDeliveryAttemptResult(
                    false,
                    cliRecovery.Message,
                    "CodexCliRecovery",
                    FallbackUsed: true,
                    IpcRequestSent: ShouldSuppressAutomaticRetry(ipc));
            }

            if (ipc.SafeToRetry && config.CodexOpenThreadUrlOnIpcFailure)
            {
                log.Warning("Codex IPC definitively rejected the request; reopening the configured thread before one immediate retry.", "Codex");
                await TryOpenCodexThreadUrlAsync(config, log, cancellationToken);
                state.MarkCodexIpcAttempt(config.CodexThreadId, transport);
                ipc = await _codexIpcClient.StartTurnAsync(config, prompt, log, cancellationToken);
                RecordIpcResult(state, ipc);
                if (ipc.Confirmed)
                {
                    var recoveredMessage = $"DELIVERY_CONFIRMED: Codex IPC reclaimed thread {config.CodexThreadId} and confirmed turn start.";
                    return new CodexDeliveryAttemptResult(true, recoveredMessage, "IPC", FallbackUsed: false);
                }
            }

            log.Warning($"Codex IPC unconfirmed: {ipc.Message}", "Codex");
            if (transport.Equals(IpcOnlyTransport, StringComparison.OrdinalIgnoreCase))
            {
                log.Info("UI fallback skipped because CodexDeliveryTransport=IpcOnly.", "Codex");
                return new CodexDeliveryAttemptResult(
                    false,
                    $"DELIVERY_FAILED: IPC delivery was not confirmed. {ipc.Message}",
                    transport,
                    FallbackUsed: false,
                    IpcRequestSent: ShouldSuppressAutomaticRetry(ipc));
            }

            if (config.CodexOpenThreadUrlOnIpcFailure)
            {
                await TryOpenCodexThreadUrlAsync(config, log, cancellationToken);
            }
            else
            {
                log.Info("codex://threads fallback skipped because CodexOpenThreadUrlOnIpcFailure is disabled.", "Codex");
            }

            if (!ShouldAttemptUiFallback(config, afterIpcFailure: true))
            {
                log.Info("UI fallback skipped by transport/config.", "Codex");
                return new CodexDeliveryAttemptResult(
                    false,
                    $"DELIVERY_FAILED: IPC delivery was not confirmed. {ipc.Message}",
                    transport,
                    FallbackUsed: false,
                    IpcRequestSent: ShouldSuppressAutomaticRetry(ipc));
            }

            state.MarkCodexFallbackUsed(true);
            log.Warning("UI paste fallback attempted after IPC failure.", "Codex");
            var uiAfterIpc = await PastePromptAndSendAsync(config, prompt, log, cancellationToken);
            if (!uiAfterIpc.Success)
            {
                return new CodexDeliveryAttemptResult(false, uiAfterIpc.Message, "UIFallback", FallbackUsed: true);
            }

            return new CodexDeliveryAttemptResult(
                true,
                "DELIVERY_ATTEMPTED: Codex UI fallback pasted prompt and sent keystroke after IPC was unconfirmed.",
                "UIFallback",
                FallbackUsed: true);
        }

        log.Info("Codex IPC skipped because CodexDeliveryTransport=UiPasteOnly.", "Codex");
        if (!ShouldAttemptUiFallback(config, afterIpcFailure: false))
        {
            var message = "DELIVERY_FAILED: CodexDeliveryTransport=UiPasteOnly but UI paste fallback is disabled.";
            log.Error(message, "Codex");
            return new CodexDeliveryAttemptResult(false, message, transport, FallbackUsed: false);
        }

        state.MarkCodexFallbackUsed(true);
        var ui = await PastePromptAndSendAsync(config, prompt, log, cancellationToken);
        if (!ui.Success)
        {
            return new CodexDeliveryAttemptResult(false, ui.Message, transport, FallbackUsed: true);
        }

        return new CodexDeliveryAttemptResult(
            true,
            "DELIVERY_ATTEMPTED: Codex UI paste transport pasted prompt and sent keystroke.",
            transport,
            FallbackUsed: true);
    }

    private async Task<CodexUiDeliveryResult> PastePromptAndSendAsync(
        AppConfig config,
        string prompt,
        LogService log,
        CancellationToken cancellationToken)
    {
        var window = await FindOrLaunchCodexWindowAsync(config, log, cancellationToken);
        if (!window.Success)
        {
            LogWindowCandidates(window.Candidates, log);
            var message = $"DELIVERY_FAILED: {window.Message}";
            log.Error(message, "Codex");
            return new CodexUiDeliveryResult(false, message);
        }

        log.Info($"Codex window found: {window.Title}", "Codex");
        var focused = FocusCodexWindow(window.Handle, config.CodexFocusRetryCount);
        if (!focused)
        {
            var message = "DELIVERY_FAILED: Codex window was found but could not be focused.";
            log.Error(message, "Codex");
            return new CodexUiDeliveryResult(false, message);
        }

        if (!config.CodexUseClipboardFallback)
        {
            var message = "DELIVERY_FAILED: UI Automation text insertion is not available and clipboard fallback is disabled.";
            log.Error(message, "Codex");
            return new CodexUiDeliveryResult(false, message);
        }

        log.Warning("Using clipboard fallback for Codex prompt insertion.", "Codex");
        var paste = await RunClipboardPasteAsync(prompt, config, cancellationToken);
        if (!paste.Success)
        {
            var message = $"DELIVERY_FAILED: {paste.Message}";
            log.Error(message, "Codex");
            return new CodexUiDeliveryResult(false, message);
        }

        try
        {
            SendConfiguredKeystroke(config);
        }
        catch (Exception ex)
        {
            var message = $"DELIVERY_FAILED: send keystroke failed: {ex.Message}";
            log.Error(message, "Codex");
            return new CodexUiDeliveryResult(false, message);
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, config.CodexDeliveryConfirmationSeconds)), cancellationToken);
        return new CodexUiDeliveryResult(true, "DELIVERY_ATTEMPTED: prompt pasted and send keystroke sent.");
    }

    public PendingTaskValidationResult ValidatePendingTaskFile(AppConfig config, string taskPath, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskPath))
        {
            return PendingTaskValidationResult.Fail("Pending task file path is empty.");
        }

        var fullTaskPath = Path.GetFullPath(taskPath);
        if (!File.Exists(fullTaskPath))
        {
            return PendingTaskValidationResult.Fail($"Pending task file does not exist: {fullTaskPath}");
        }

        var repoRoot = Path.GetFullPath(config.LocalRepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullTaskPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return PendingTaskValidationResult.Fail($"Pending task file is outside LocalRepoPath: {fullTaskPath}");
        }

        var metadataPath = Path.Combine(
            Path.GetDirectoryName(fullTaskPath)!,
            $"{Path.GetFileNameWithoutExtension(fullTaskPath)}.metadata.json");
        if (!File.Exists(metadataPath))
        {
            return PendingTaskValidationResult.Fail($"Task metadata file does not exist: {metadataPath}");
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return PendingTaskValidationResult.Fail("Pending task id is empty.");
        }

        return PendingTaskValidationResult.Ok(fullTaskPath, metadataPath, ComputeSha256(fullTaskPath));
    }

    private CodexDeliveryPayload LoadRawCapturedEnvelope(AppConfig config, AppState state, string taskId)
    {
        var envelopePath = string.Empty;
        if (GetPendingCapturedTaskRecord(state, taskId) is { } record &&
            !string.IsNullOrWhiteSpace(record.EnvelopePath))
        {
            envelopePath = record.EnvelopePath;
        }

        if (string.IsNullOrWhiteSpace(envelopePath))
        {
            return new CodexDeliveryPayload(false, string.Empty, VerbatimIpcMode, string.Empty, $"Raw captured envelope path is not recorded for task_id {taskId}.");
        }

        var fullEnvelopePath = Path.GetFullPath(envelopePath);
        if (!File.Exists(fullEnvelopePath))
        {
            return new CodexDeliveryPayload(false, string.Empty, VerbatimIpcMode, fullEnvelopePath, $"Raw captured envelope file does not exist: {fullEnvelopePath}");
        }

        var ledgerRoot = Path.GetFullPath(_ledgerService.GetLedgerRoot(config)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullEnvelopePath.StartsWith(ledgerRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new CodexDeliveryPayload(false, string.Empty, VerbatimIpcMode, fullEnvelopePath, $"Raw captured envelope file is outside the Watcher ledger: {fullEnvelopePath}");
        }

        var prompt = File.ReadAllText(fullEnvelopePath);
        return new CodexDeliveryPayload(true, prompt, VerbatimIpcMode, fullEnvelopePath, "Raw captured envelope loaded for verbatim IPC delivery.");
    }

    private static CapturedTaskRecord? GetPendingCapturedTaskRecord(AppState state, string taskId)
    {
        if (state.CapturedTasks.TryGetValue(taskId, out var record))
        {
            return record;
        }

        if (state.LastCapturedTaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(state.LastCapturedEnvelopePath))
        {
            return new CapturedTaskRecord
            {
                TaskId = taskId,
                SourceReport = state.LastCapturedSourceReport,
                CreatedAt = state.LastCapturedTaskCreatedAt,
                EnvelopePath = state.LastCapturedEnvelopePath,
                InstructionPath = state.LastCapturedInstructionPath
            };
        }

        return null;
    }

    public static string ResolvePromptDeliveryMode(AppConfig config)
    {
        return (config.CodexDeliveryMode ?? string.Empty).Trim() switch
        {
            "Full Paste" => FullPasteMode,
            "FullPaste" => FullPasteMode,
            "File Handoff" => FileHandoffMode,
            "File handoff, advanced" => FileHandoffMode,
            "FileHandoff" => FileHandoffMode,
            "UI paste fallback" => UiPasteFallbackMode,
            "UiPasteFallback" => UiPasteFallbackMode,
            "UiPasteOnly" => UiPasteFallbackMode,
            "IpcThenUiFallback" => IpcThenUiFallbackMode,
            "Verbatim IPC" => VerbatimIpcMode,
            "VerbatimIpc" => VerbatimIpcMode,
            "Auto" => VerbatimIpcMode,
            "" => VerbatimIpcMode,
            _ => VerbatimIpcMode
        };
    }

    public static string ResolveDeliveryTransport(AppConfig config)
    {
        var mode = ResolvePromptDeliveryMode(config);
        if (mode.Equals(VerbatimIpcMode, StringComparison.OrdinalIgnoreCase))
        {
            return IpcOnlyTransport;
        }

        if (mode.Equals(UiPasteFallbackMode, StringComparison.OrdinalIgnoreCase))
        {
            return UiPasteOnlyTransport;
        }

        if (mode.Equals(IpcThenUiFallbackMode, StringComparison.OrdinalIgnoreCase))
        {
            return IpcThenUiFallbackTransport;
        }

        var transport = config.CodexDeliveryTransport?.Trim() ?? string.Empty;
        return transport switch
        {
            IpcOnlyTransport => IpcOnlyTransport,
            IpcThenUiFallbackTransport => IpcThenUiFallbackTransport,
            UiPasteOnlyTransport => UiPasteOnlyTransport,
            _ => IpcThenUiFallbackTransport
        };
    }

    public static bool ShouldAttemptIpc(AppConfig config)
    {
        return !ResolveDeliveryTransport(config).Equals(UiPasteOnlyTransport, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldAttemptUiFallback(AppConfig config, bool afterIpcFailure)
    {
        var transport = ResolveDeliveryTransport(config);
        if (transport.Equals(UiPasteOnlyTransport, StringComparison.OrdinalIgnoreCase))
        {
            return config.CodexUiPasteFallbackEnabled;
        }

        return afterIpcFailure &&
               transport.Equals(IpcThenUiFallbackTransport, StringComparison.OrdinalIgnoreCase) &&
               config.CodexUiPasteFallbackEnabled;
    }

    public static bool IsIpcDeliveryEnabled(AppConfig config)
    {
        return ShouldAttemptIpc(config);
    }

    public static bool IsIpcDeliveryEnabled(string deliveryMode)
    {
        return !string.IsNullOrWhiteSpace(deliveryMode) &&
               deliveryMode.Contains("IPC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVerbatimDeliveryMode(string mode)
    {
        return mode.Equals(VerbatimIpcMode, StringComparison.OrdinalIgnoreCase) ||
               mode.Equals(IpcThenUiFallbackMode, StringComparison.OrdinalIgnoreCase) ||
               mode.Equals(UiPasteFallbackMode, StringComparison.OrdinalIgnoreCase);
    }

    private static CodexSendResult? RequireCodexThreadIdForIpc(
        AppConfig config,
        AppState state,
        LogService log,
        string taskId,
        string mode)
    {
        if (!ShouldAttemptIpc(config))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(config.CodexThreadId))
        {
            log.Info($"Codex IPC delivery configured for thread id {config.CodexThreadId}.", "Codex");
            return null;
        }

        const string message = "DELIVERY_FAILED: CodexThreadId is required for IPC delivery.";
        log.Error(message, "Codex");
        state.MarkCodexIpcResult(false, "Failed", message);
        state.MarkCodexDeliveryFailed(taskId, mode, string.Empty, message);
        return new CodexSendResult(false, null, message);
    }

    private static void RecordIpcResult(AppState state, CodexIpcResult ipc)
    {
        var result = ipc.Confirmed
            ? "Confirmed turn"
            : ipc.PipeUnavailable
                ? "Pipe unavailable"
                : ipc.Connected
                    ? "Failed"
                    : "Failed";
        state.MarkCodexIpcResult(ipc.Confirmed, result, ipc.Confirmed ? string.Empty : ipc.Error ?? ipc.Message);
    }

    public static bool ShouldSuppressAutomaticRetry(CodexIpcResult ipc)
    {
        return ipc.RequestSent && !ipc.SafeToRetry;
    }

    public static bool IsNoOwnerRejection(CodexIpcResult ipc)
    {
        return ipc.SafeToRetry &&
               (ipc.Message.Contains("could not find an owner", StringComparison.OrdinalIgnoreCase) ||
                (ipc.Error?.Contains("no-client-found", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static async Task TryOpenCodexThreadUrlAsync(
        AppConfig config,
        LogService log,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.CodexThreadId))
        {
            log.Error("DELIVERY_FAILED: CodexThreadId is required before opening codex://threads fallback.", "Codex");
            return;
        }

        var url = $"codex://threads/{Uri.EscapeDataString(config.CodexThreadId.Trim())}";
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            log.Warning($"Fallback codex://threads opened: {url}", "Codex");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception ex)
        {
            log.Error($"codex://threads fallback failed: {ex.Message}", "Codex");
        }
    }

    private static async Task<CodexCliRecoveryResult> TryStartCodexCliRecoveryAsync(
        AppConfig config,
        string taskId,
        string promptPath,
        LogService log,
        CancellationToken cancellationToken)
    {
        var cliPath = ResolveCodexCliPath(config);
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
        {
            return new CodexCliRecoveryResult(false, "Codex CLI recovery unavailable: codex.exe was not found.");
        }

        if (!File.Exists(promptPath))
        {
            return new CodexCliRecoveryResult(false, $"Codex CLI recovery unavailable: prompt file not found: {promptPath}");
        }

        var logDirectory = Path.GetDirectoryName(promptPath) ?? Path.GetTempPath();
        var recoveryLogPath = Path.Combine(logDirectory, $"{taskId}.cli-recovery.jsonl");
        var executionRoot = Directory.Exists(config.ReportGitRoot)
            ? config.ReportGitRoot
            : config.WorkspaceRoot;
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(executionRoot) ? executionRoot : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(executionRoot);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("resume");
        startInfo.ArgumentList.Add(config.CodexThreadId.Trim());
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("--json");

        return await RunCodexCliRecoveryProcessAsync(
            startInfo,
            taskId,
            config.CodexThreadId,
            promptPath,
            recoveryLogPath,
            TimeSpan.FromSeconds(Math.Max(5, config.CodexCliStartTimeoutSeconds)),
            () => log.Warning($"Codex CLI no-owner recovery started for {taskId}; log={recoveryLogPath}", "Codex"),
            cancellationToken);
    }

    private static async Task<CodexCliRecoveryResult> RunCodexCliRecoveryProcessAsync(
        ProcessStartInfo startInfo,
        string taskId,
        string threadId,
        string promptPath,
        string recoveryLogPath,
        TimeSpan startTimeout,
        Action? onPromptSubmitted,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        var processId = 0;
        var recoveryOwnsProcess = true;

        try
        {
            if (File.Exists(recoveryLogPath))
            {
                File.Delete(recoveryLogPath);
            }

            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            var turnStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void AppendRecoveryLog(string? line)
            {
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }

                if (line.Contains("\"type\":\"turn.started\"", StringComparison.Ordinal))
                {
                    turnStarted.TrySetResult(true);
                }

                try
                {
                    lock (CliRecoveryLogLock)
                    {
                        File.AppendAllText(recoveryLogPath, line + Environment.NewLine, new UTF8Encoding(false));
                    }
                }
                catch (IOException)
                {
                    // Process cleanup must not be disrupted by a failed diagnostic write.
                }
                catch (UnauthorizedAccessException)
                {
                    // Process cleanup must not be disrupted by a failed diagnostic write.
                }
            }

            process.OutputDataReceived += (_, args) => AppendRecoveryLog(args.Data);
            process.ErrorDataReceived += (_, args) => AppendRecoveryLog(args.Data);
            process.Exited += (_, _) =>
            {
                turnStarted.TrySetResult(false);
            };

            if (!process.Start())
            {
                process.Dispose();
                return new CodexCliRecoveryResult(false, "Codex CLI recovery failed to start.");
            }

            processId = process.Id;
            ActiveCliRecoveries[processId] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            onPromptSubmitted?.Invoke();
            bool started;
            try
            {
                started = await turnStarted.Task.WaitAsync(
                    startTimeout,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                started = false;
            }

            if (started)
            {
                recoveryOwnsProcess = false;
                _ = ObserveCliRecoveryExitAsync(process, processId);
                return new CodexCliRecoveryResult(
                    true,
                    $"DELIVERY_CONFIRMED: Codex CLI resumed thread {threadId} and confirmed turn start for {taskId}.");
            }

            var output = File.Exists(recoveryLogPath)
                ? await File.ReadAllTextAsync(recoveryLogPath)
                : string.Empty;
            var tail = output.Length <= 500 ? output : output[^500..];
            return new CodexCliRecoveryResult(false, $"Codex CLI recovery ended before turn start for {taskId}. tail={tail}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CodexCliRecoveryResult(false, $"Codex CLI recovery failed for {taskId}: {ex.Message}");
        }
        finally
        {
            if (process is not null && recoveryOwnsProcess)
            {
                await TerminateCliRecoveryAsync(process, processId);
            }
        }
    }

    private static async Task ObserveCliRecoveryExitAsync(Process process, int processId)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process already reached its terminal state.
        }
        finally
        {
            ActiveCliRecoveries.TryRemove(processId, out _);
            process.Dispose();
        }
    }

    private static async Task TerminateCliRecoveryAsync(Process process, int processId)
    {
        ActiveCliRecoveries.TryRemove(processId, out _);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while cleanup was inspecting it.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or access was lost during cleanup.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // The process never started or already reached its terminal state.
        }
        catch (TimeoutException)
        {
            // Cleanup is bounded even if Windows does not report process exit.
        }
        finally
        {
            process.Dispose();
        }
    }

    internal static int ActiveCliRecoveryCountForSelfTest => ActiveCliRecoveries.Count;

    internal static async Task<bool> RunCliRecoveryForSelfTestAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string promptPath,
        string recoveryLogPath,
        TimeSpan startTimeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = await RunCodexCliRecoveryProcessAsync(
            startInfo,
            "SELFTEST",
            "self-test-thread",
            promptPath,
            recoveryLogPath,
            startTimeout,
            onPromptSubmitted: null,
            cancellationToken);
        return result.Success;
    }

    private static string ResolveCodexCliPath(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CodexCliPath))
        {
            return Environment.ExpandEnvironmentVariables(config.CodexCliPath.Trim());
        }

        var binRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        if (!Directory.Exists(binRoot))
        {
            return string.Empty;
        }

        return Directory
            .EnumerateFiles(binRoot, "codex.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? string.Empty;
    }

    private static async Task<WindowSearchResult> FindOrLaunchCodexWindowAsync(
        AppConfig config,
        LogService log,
        CancellationToken cancellationToken)
    {
        var initial = TryFindCodexWindow(config);
        if (initial.Success || string.IsNullOrWhiteSpace(config.CodexLaunchCommand))
        {
            return initial;
        }

        var launched = TryLaunchCodex(config.CodexLaunchCommand, log);
        if (!launched.Success)
        {
            return WindowSearchResult.Fail($"{initial.Message} Launch command failed: {launched.Message}", initial.Candidates);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, config.CodexSendTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        log.Info($"Codex launch command started; waiting up to {timeout.TotalSeconds:N0}s for a matching window.", "Codex");

        WindowSearchResult latest = initial;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            latest = TryFindCodexWindow(config);
            if (latest.Success)
            {
                return latest;
            }
        }

        return WindowSearchResult.Fail($"{latest.Message} Launch command was started but no matching Codex window appeared.", latest.Candidates);
    }

    private static LaunchResult TryLaunchCodex(string launchCommand, LogService log)
    {
        try
        {
            var startInfo = BuildLaunchStartInfo(launchCommand);
            Process.Start(startInfo);
            log.Info($"Codex launch command started: {launchCommand}", "Codex");
            return new LaunchResult(true, "Started.");
        }
        catch (Exception ex)
        {
            log.Error($"Codex launch command failed: {ex.Message}", "Codex");
            return new LaunchResult(false, ex.Message);
        }
    }

    private static ProcessStartInfo BuildLaunchStartInfo(string launchCommand)
    {
        var expanded = Environment.ExpandEnvironmentVariables(launchCommand.Trim());
        var fileName = expanded;
        var arguments = string.Empty;

        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                fileName = expanded[1..closingQuote];
                arguments = expanded[(closingQuote + 1)..].Trim();
            }
        }
        else
        {
            var firstSpace = expanded.IndexOf(' ');
            if (firstSpace > 0)
            {
                fileName = expanded[..firstSpace];
                arguments = expanded[(firstSpace + 1)..].Trim();
            }
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static WindowSearchResult TryFindCodexWindow(AppConfig config)
    {
        var windows = EnumerateVisibleWindows();
        var titleNeedle = config.CodexWindowTitleContains?.Trim() ?? string.Empty;
        var directorNeedle = config.CodexDirectorTitle ?? string.Empty;

        if (string.IsNullOrWhiteSpace(titleNeedle))
        {
            return WindowSearchResult.Fail("CodexWindowTitleContains is empty; refusing to guess a Codex target window.", windows);
        }

        var matches = windows
            .Where(window =>
                window.Title.Contains(titleNeedle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var result = SelectUnambiguousWindow(matches, directorNeedle, $"title contains CodexWindowTitleContains '{titleNeedle}'", windows);
        return result ?? WindowSearchResult.Fail(
            $"No safe Codex window match found for CodexWindowTitleContains='{titleNeedle}'.",
            windows);
    }

    private static WindowSearchResult? SelectUnambiguousWindow(
        IReadOnlyList<WindowListEntry> matches,
        string directorNeedle,
        string matchReason,
        IReadOnlyList<WindowListEntry> consideredWindows)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            var selected = matches[0];
            return BuildSafeWindowSelection(selected, consideredWindows);
        }

        if (!string.IsNullOrWhiteSpace(directorNeedle))
        {
            var directorMatches = matches
                .Where(window => window.Title.Contains(directorNeedle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (directorMatches.Count == 1)
            {
                var selected = directorMatches[0];
                return BuildSafeWindowSelection(selected, consideredWindows);
            }
        }

        return WindowSearchResult.Fail(
            $"Ambiguous Codex window match for {matchReason}; refusing to send. Candidate windows: {string.Join(" | ", matches.Select(FormatWindow))}",
            consideredWindows);
    }

    private static WindowSearchResult BuildSafeWindowSelection(
        WindowListEntry selected,
        IReadOnlyList<WindowListEntry> consideredWindows)
    {
        if (IsBrowserOrChatGptWindow(selected))
        {
            return WindowSearchResult.Fail(
                $"Refusing to send Codex wake to browser/ChatGPT window. Candidate: {FormatWindow(selected)}",
                consideredWindows);
        }

        return WindowSearchResult.Ok(selected.Handle, selected.Title, consideredWindows);
    }

    private static List<WindowListEntry> EnumerateVisibleWindows()
    {
        var windows = new List<WindowListEntry>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new WindowListEntry(handle, (int)processId, GetProcessName((int)processId), title));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static void LogWindowCandidates(IReadOnlyList<WindowListEntry> windows, LogService log)
    {
        if (windows.Count == 0)
        {
            log.Warning("No visible titled top-level windows were available for Codex matching.", "Codex");
            return;
        }

        log.Warning("Visible top-level windows considered for Codex matching:", "Codex");
        foreach (var window in windows)
        {
            log.Warning(FormatWindow(window), "Codex");
        }

        log.Warning("If the Codex title is present above, set Codex window title contains to a unique substring and retry.", "Codex");
    }

    private static string FormatWindow(WindowListEntry window)
    {
        return $"PID={window.ProcessId} Process={window.ProcessName} Title={window.Title}";
    }

    public static bool IsBrowserOrChatGptWindow(WindowListEntry window)
    {
        return IsBrowserOrChatGptWindow(window.Title, window.ProcessName);
    }

    public static bool IsBrowserOrChatGptWindow(string title, string processName)
    {
        var combined = $"{title} {processName}";
        return ContainsAny(combined, [
            "ChatGPT",
            "chatgpt.com",
            "Microsoft Edge",
            "Google Chrome",
            "Mozilla Firefox",
            "Brave",
            "Opera",
            "msedge",
            "chrome",
            "firefox",
            "brave",
            "opera"
        ]);
    }

    private static bool ContainsAny(string value, IEnumerable<string> needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool FocusCodexWindow(IntPtr handle, int retryCount)
    {
        for (var attempt = 0; attempt < Math.Max(1, retryCount); attempt++)
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            Thread.Sleep(300);
            if (GetForegroundWindow() == handle)
            {
                return true;
            }
        }

        return false;
    }

    private static Task<CodexUiDeliveryResult> RunClipboardPasteAsync(
        string prompt,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CodexUiDeliveryResult>();
        var thread = new Thread(() =>
        {
            IDataObject? previousData = null;
            var hadData = false;
            try
            {
                if (config.CodexRestoreClipboardAfterSend)
                {
                    previousData = Clipboard.GetDataObject();
                    hadData = previousData is not null;
                }

                Clipboard.SetText(prompt);
                SendKeys.SendWait("^v");
                Thread.Sleep(250);

                if (config.CodexRestoreClipboardAfterSend)
                {
                    if (hadData && previousData is not null)
                    {
                        Clipboard.SetDataObject(previousData, copy: true);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                }

                completion.SetResult(new CodexUiDeliveryResult(true, "Clipboard paste succeeded."));
            }
            catch (Exception ex)
            {
                completion.SetResult(new CodexUiDeliveryResult(false, $"Clipboard paste failed: {ex.Message}"));
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private static void SendConfiguredKeystroke(AppConfig config)
    {
        if (config.CodexSendKeystroke.Equals("CtrlEnter", StringComparison.OrdinalIgnoreCase) ||
            config.CodexSendKeystroke.Equals("Ctrl+Enter", StringComparison.OrdinalIgnoreCase))
        {
            SendKeys.SendWait("^{ENTER}");
            return;
        }

        SendKeys.SendWait("{ENTER}");
    }

    private sealed record CodexUiDeliveryResult(bool Success, string Message);

    private sealed record CodexCliRecoveryResult(bool Success, string Message);

    private sealed record CodexDeliveryAttemptResult(
        bool Success,
        string Message,
        string TransportUsed,
        bool FallbackUsed,
        bool IpcRequestSent = false);

    private sealed record LaunchResult(bool Success, string Message);

    public sealed record PendingTaskValidationResult(
        bool Success,
        string Message,
        string TaskFilePath,
        string MetadataFilePath,
        string TaskFileSha256)
    {
        public static PendingTaskValidationResult Ok(string taskFilePath, string metadataFilePath, string sha256)
        {
            return new PendingTaskValidationResult(true, "OK", taskFilePath, metadataFilePath, sha256);
        }

        public static PendingTaskValidationResult Fail(string message)
        {
            return new PendingTaskValidationResult(false, message, string.Empty, string.Empty, string.Empty);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

public sealed record WindowListEntry(IntPtr Handle, int ProcessId, string ProcessName, string Title);

public sealed record WindowListResult(IReadOnlyList<WindowListEntry> Entries, string SavedPath);

public sealed record WindowSearchResult(
    bool Success,
    IntPtr Handle,
    string Title,
    string Message,
    IReadOnlyList<WindowListEntry> Candidates)
{
    public static WindowSearchResult Ok(IntPtr handle, string title, IReadOnlyList<WindowListEntry>? candidates = null)
    {
        return new WindowSearchResult(true, handle, title, "OK", candidates ?? []);
    }

    public static WindowSearchResult Fail(string message, IReadOnlyList<WindowListEntry>? candidates = null)
    {
        return new WindowSearchResult(false, IntPtr.Zero, string.Empty, message, candidates ?? []);
    }
}
