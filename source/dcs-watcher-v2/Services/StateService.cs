using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class StateService
{
    private readonly ConfigService _configService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public StateService(ConfigService configService)
    {
        _configService = configService;
    }

    internal ConfigService ConfigService => _configService;

    public AppState Load(AppConfig config)
    {
        var path = GetStatePath(config);

        if (!File.Exists(path))
        {
            return new AppState();
        }

        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
        state.WatcherRunning = false;
        state.OperatingStage = config.OperatingStage;
        state.ActiveTaskLock ??= new ActiveTaskLockRecord();
        state.TransactionAudit ??= new TransactionAuditState();
        state.ReportIngestionHistory ??= [];
        state.ConsumedReportSha256 ??= [];
        state.UsedWakeTokens ??= [];
        state.RecordedManualDeliveryHashes ??= [];
        state.InstructionSupersessions ??= [];
        return state;
    }

    public void Save(AppConfig config, AppState state)
    {
        var path = GetStatePath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(path, json);
        SaveReportWakeHistory(config, state);
    }

    public string GetStatePath(AppConfig config)
    {
        return Path.Combine(_configService.GetLedgerRoot(config), "state.json");
    }

    private void SaveReportWakeHistory(AppConfig config, AppState state)
    {
        var eventsDir = Path.Combine(_configService.GetLedgerRoot(config), "events");
        Directory.CreateDirectory(eventsDir);
        var path = Path.Combine(eventsDir, "report-wake-history.jsonl");
        var lines = state.ReportWakeHistory.Select(entry => JsonSerializer.Serialize(new
        {
            entry
        }));
        File.WriteAllLines(path, lines);
    }
}
