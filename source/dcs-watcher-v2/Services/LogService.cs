using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class LogService
{
    private readonly ConfigService _configService;
    private AppConfig _config = new();
    private string? _ledgerRoot;

    public event EventHandler<WatchEvent>? EventLogged;

    public LogService(ConfigService configService)
    {
        _configService = configService;
    }

    public void Initialize(AppConfig config)
    {
        _config = config;
        _ledgerRoot = _configService.GetLedgerRoot(config);
        Directory.CreateDirectory(Path.Combine(_ledgerRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(_ledgerRoot, "events"));
    }

    public void Info(string message, string source = "Watcher")
    {
        Write("INFO", message, source);
    }

    public void Warning(string message, string source = "Watcher")
    {
        Write("WARN", message, source);
    }

    public void Error(string message, string source = "Watcher")
    {
        Write("ERROR", message, source);
    }

    private void Write(string level, string message, string source)
    {
        var watchEvent = new WatchEvent
        {
            Level = level,
            Source = source,
            Message = message
        };

        try
        {
            if (_ledgerRoot is null)
            {
                Initialize(_config);
            }

            var root = _ledgerRoot!;
            var logPath = Path.Combine(root, "logs", $"watcher-{DateTime.Now:yyyyMMdd}.log");
            var eventPath = Path.Combine(root, "events", $"events-{DateTime.Now:yyyyMMdd}.jsonl");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(eventPath)!);

            File.AppendAllText(logPath, watchEvent + Environment.NewLine);
            File.AppendAllText(eventPath, JsonSerializer.Serialize(watchEvent) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            watchEvent.Level = "ERROR";
            watchEvent.Source = "LogService";
            watchEvent.Message = $"Log write failed: {ex.Message}. Original event: {level} {source}: {message}";
        }

        EventLogged?.Invoke(this, watchEvent);
    }
}
