using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public static class WatcherSafetyPolicy
{
    public const string AutomaticDeliveryDisabledMessage =
        "AUTOMATIC DELIVERY DISABLED: Watcher supports only human manual envelope paste or hash-bound file handoff.";

    public static WatcherOperatingStage ResolveStage(AppConfig config)
    {
        return Enum.TryParse<WatcherOperatingStage>(config.OperatingStage, true, out var stage)
            ? stage
            : WatcherOperatingStage.Stage1DetectOnly;
    }

    public static bool CanPostWake(AppConfig config, WakeTransactionRecord? transaction, out string reason)
    {
        var stage4 = CanRunStage4LimitedAutomatic(config, out var stage4Reason);
        if (!stage4 && !CanRunStage3ManualPilot(config, out reason))
        {
            reason = stage4Reason + " " + reason;
            return false;
        }

        if (transaction is null || !transaction.HumanConfirmed ||
            !(transaction.Status.Equals("human-confirmed-preflight", StringComparison.Ordinal) ||
              stage4 && transaction.Status.Equals("stage4-authorized-preflight", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(transaction.TransactionId) ||
            string.IsNullOrWhiteSpace(transaction.Nonce) ||
            string.IsNullOrWhiteSpace(transaction.WakeToken) ||
            !string.IsNullOrWhiteSpace(transaction.WakeMessageId))
        {
            reason = "Stage 3 manual pilot requires one unused, human-confirmed wake transaction.";
            return false;
        }

        reason = stage4
            ? "One Stage 4 globally authorized, lineage-bound wake is permitted."
            : "One human-confirmed Stage 3 manual-pilot wake is permitted.";
        return true;
    }

    public static bool CanCaptureInstructionForAuthorization(
        AppConfig config,
        AssistantResponseObservation observation,
        out string reason)
    {
        var stage4 = CanRunStage4LimitedAutomatic(config, out _);
        if (!stage4 && (ResolveStage(config) <= WatcherOperatingStage.Stage3ManualPilotReady || !config.LiveManualPilotAuthorized))
        {
            reason = "Stage 3 readiness does not authorize live instruction capture.";
            return false;
        }

        if (!observation.CaptureMethod.Equals(BranchLineageSafetyService.AuthorizedCaptureMethod, StringComparison.Ordinal) ||
            observation.FallbackBody || !observation.ApiVerified || observation.OnCurrentPath is not true)
        {
            reason = "Only an API-verified, current-path backend message object can be considered for human display.";
            return false;
        }

        reason = "Instruction may be displayed for a manual handoff decision.";
        return true;
    }

    public static bool CanAutomaticallyDeliver(AppConfig config, out string reason)
    {
        return CanRunStage4LimitedAutomatic(config, out reason);
    }

    public static bool CanRunStage4LimitedAutomatic(AppConfig config, out string reason)
    {
        if (ResolveStage(config) != WatcherOperatingStage.Stage4LimitedAutomatic ||
            !config.Stage4Authorized || config.Stage5Authorized ||
            !config.AutomaticWakeEnabled || !config.AutomaticDeliveryEnabled ||
            !config.AutomaticInstructionDeliveryEnabled || !config.LiveCodexIntakeEnabled ||
            !config.SubmitChatGptPrompt || !config.AutoCaptureChatGptEnvelope || !config.SubmitCodexPrompt)
        {
            reason = "Stage 4 requires the complete limited-automatic authorization set and Stage 5 must remain disabled.";
            return false;
        }

        if (config.LiveManualPilotAuthorized || config.AutoSendCapturedTaskToCodex || config.UiBridgeToCodex ||
            config.CodexUiPasteFallbackEnabled || config.CodexUseClipboardFallback ||
            !config.ChatGptCaptureScope.Equals("BackendMessageObject", StringComparison.Ordinal))
        {
            reason = "Stage 4 prohibits manual-pilot state, legacy auto-send, UI fallback, clipboard delivery, and non-backend capture.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.Stage4IntakeExecutablePath) ||
            string.IsNullOrWhiteSpace(config.CodexThreadId))
        {
            reason = "Stage 4 requires the verified intake executable and destination Codex thread.";
            return false;
        }

        reason = "Stage 4 limited automatic operation is authorized through the signed lineage and intake gates.";
        return true;
    }

    public static bool CanRunSignedDryRun(AppConfig config, out string reason)
    {
        if (ResolveStage(config) != WatcherOperatingStage.Stage2SignedDryRun)
        {
            reason = "Signed dry-run requires Stage2SignedDryRun.";
            return false;
        }

        reason = "Offline signed dry-run is permitted; live wake and delivery remain disabled.";
        return true;
    }

    public static bool CanRunStage3Readiness(AppConfig config, out string reason)
    {
        if (ResolveStage(config) != WatcherOperatingStage.Stage3ManualPilotReady)
        {
            reason = "Stage 3 readiness verification requires Stage3ManualPilotReady.";
            return false;
        }

        if (config.LiveManualPilotAuthorized || config.AutomaticWakeEnabled || config.AutomaticDeliveryEnabled ||
            config.LiveCodexIntakeEnabled || config.Stage4Authorized || config.Stage5Authorized ||
            config.SubmitChatGptPrompt || config.AutoCaptureChatGptEnvelope || config.SubmitCodexPrompt ||
            config.AutoSendCapturedTaskToCodex || config.AutomaticInstructionDeliveryEnabled)
        {
            reason = "Stage 3 readiness requires every live and automatic authorization flag to remain false.";
            return false;
        }

        reason = "Offline Stage 3 readiness verification and isolated test-sink transport are permitted.";
        return true;
    }

    public static bool CanRunStage3ManualPilot(AppConfig config, out string reason)
    {
        if (ResolveStage(config) != WatcherOperatingStage.Stage3ManualPilot ||
            !config.LiveManualPilotAuthorized || !config.LiveCodexIntakeEnabled)
        {
            reason = "The one-shot pilot requires Stage3ManualPilot with explicit manual-pilot and live-intake authorization.";
            return false;
        }

        if (config.AutomaticWakeEnabled || config.AutomaticDeliveryEnabled || config.Stage4Authorized ||
            config.Stage5Authorized || config.SubmitChatGptPrompt || config.AutoCaptureChatGptEnvelope ||
            config.SubmitCodexPrompt || config.AutoSendCapturedTaskToCodex ||
            config.AutomaticInstructionDeliveryEnabled)
        {
            reason = "The one-shot pilot requires every automatic, Stage 4, and Stage 5 authorization flag to remain false.";
            return false;
        }

        reason = "Exactly one manually supervised Stage 3 pilot transaction is authorized.";
        return true;
    }
}
