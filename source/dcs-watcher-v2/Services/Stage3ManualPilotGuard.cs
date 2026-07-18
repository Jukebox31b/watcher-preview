using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage3ManualPilotGuard
{
    public bool TryBegin(AppConfig config, AppState state, out string reason)
    {
        if (!WatcherSafetyPolicy.CanRunStage3ManualPilot(config, out reason))
        {
            return false;
        }

        if (state.Stage3ManualPilotAttempted)
        {
            reason = "The Stage 3 manual pilot has already been attempted; another transaction is prohibited.";
            return false;
        }

        state.Stage3ManualPilotAttempted = true;
        state.Stage3ManualPilotTerminalResult = "IN_PROGRESS";
        state.WatcherRunning = true;
        reason = "The one-shot Stage 3 manual pilot is reserved.";
        return true;
    }

    public void Complete(AppState state, string disposition, string transactionId)
    {
        state.Stage3ManualPilotTransactionId = transactionId;
        state.Stage3ManualPilotTerminalResult = disposition;
        state.WatcherRunning = false;
    }
}
