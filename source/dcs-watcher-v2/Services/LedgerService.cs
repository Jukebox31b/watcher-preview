using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class LedgerService
{
    private readonly ConfigService _configService;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public LedgerService(ConfigService configService)
    {
        _configService = configService;
    }

    public void EnsureLedger(AppConfig config)
    {
        var root = _configService.GetLedgerRoot(config);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "tasks_to_codex"));
        Directory.CreateDirectory(Path.Combine(root, "captured_envelopes"));
        Directory.CreateDirectory(Path.Combine(root, "codex_handoffs"));
        Directory.CreateDirectory(Path.Combine(root, "events"));
        Prune(config);
    }

    public string SaveEnvelope(AppConfig config, CapturedTaskEnvelope envelope)
    {
        EnsureLedger(config);
        var path = Path.Combine(
            _configService.GetLedgerRoot(config),
            "captured_envelopes",
            $"{SafeFileStem(envelope.TaskId)}.envelope.txt");

        File.WriteAllText(path, envelope.RawEnvelope);
        return path;
    }

    public string SaveTaskFile(AppConfig config, CapturedTaskEnvelope envelope)
    {
        EnsureLedger(config);
        var path = Path.Combine(
            _configService.GetLedgerRoot(config),
            "tasks_to_codex",
            $"{SafeFileStem(envelope.TaskId)}.md");

        File.WriteAllText(path, envelope.ToTaskFileMarkdown());
        return path;
    }

    public string SaveTaskMetadata(
        AppConfig config,
        CapturedTaskEnvelope envelope,
        string envelopePath,
        string instructionPath,
        DateTimeOffset capturedAtUtc)
    {
        EnsureLedger(config);
        var path = Path.Combine(
            _configService.GetLedgerRoot(config),
            "tasks_to_codex",
            $"{SafeFileStem(envelope.TaskId)}.metadata.json");

        var metadata = new CapturedTaskMetadata
        {
            TaskId = envelope.TaskId,
            Origin = envelope.Origin,
            Repo = envelope.Repo,
            Target = envelope.Target,
            Mode = envelope.Mode,
            CreatedAt = envelope.CreatedAt,
            SourceReport = envelope.SourceReport,
            CapturedAtUtc = capturedAtUtc,
            EnvelopePath = envelopePath,
            InstructionPath = instructionPath,
            InstructionChars = envelope.InstructionBody.Length,
            EnvelopeChars = envelope.RawEnvelope.Length
        };

        File.WriteAllText(path, JsonSerializer.Serialize(metadata, JsonOptions));
        return path;
    }

    public string SaveCodexHandoffPrompt(AppConfig config, string taskId, string prompt)
    {
        EnsureLedger(config);
        var path = Path.Combine(
            _configService.GetLedgerRoot(config),
            "codex_handoffs",
            $"{SafeFileStem(taskId)}.handoff.txt");

        File.WriteAllText(path, prompt);
        return path;
    }

    public string GetLedgerRoot(AppConfig config)
    {
        return _configService.GetLedgerRoot(config);
    }

    public string GetLogsPath(AppConfig config)
    {
        return Path.Combine(_configService.GetLedgerRoot(config), "logs");
    }

    public void Prune(AppConfig config)
    {
        var root = _configService.GetLedgerRoot(config);
        PruneByAge(Path.Combine(root, "logs"), config.MaxLogDays);
        PruneByAge(Path.Combine(root, "events"), config.MaxLedgerDays);
        PruneByAge(Path.Combine(root, "captured_envelopes"), config.MaxLedgerDays);
        PruneTaskFiles(Path.Combine(root, "tasks_to_codex"), config.MaxTaskFiles);
    }

    private static void PruneByAge(string directory, int maxDays)
    {
        if (maxDays <= 0 || !Directory.Exists(directory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-maxDays);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < cutoff)
            {
                info.Delete();
            }
        }
    }

    private static void PruneTaskFiles(string directory, int maxFiles)
    {
        if (maxFiles <= 0 || !Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(directory)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Skip(maxFiles);

        foreach (var file in files)
        {
            file.Delete();
        }
    }

    private static string SafeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var stem = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(stem) ? $"task-{DateTime.Now:yyyyMMdd-HHmmss}" : stem;
    }
}
