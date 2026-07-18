using System.Text;

namespace DcsWatcherV2.Models;

public sealed class CapturedTaskEnvelope
{
    public string TaskId { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
    public string SourceReport { get; set; } = string.Empty;
    public string InstructionBody { get; set; } = string.Empty;
    public string RawEnvelope { get; set; } = string.Empty;
    public DateTimeOffset? CapturedAtUtc { get; set; }

    public string ToTaskFileMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DCS Watcher v2 Captured Codex Director Task");
        builder.AppendLine();
        builder.AppendLine($"Task ID: {TaskId}");
        builder.AppendLine($"Source report: {SourceReport}");
        builder.AppendLine($"Created at: {CreatedAt:O}");
        builder.AppendLine($"Captured at UTC: {CapturedAtUtc:O}");
        builder.AppendLine($"Repo: {Repo}");
        builder.AppendLine($"Target: {Target}");
        builder.AppendLine($"Mode: {Mode}");
        builder.AppendLine();
        builder.AppendLine("## Instruction");
        builder.AppendLine();
        builder.Append(InstructionBody);
        if (!InstructionBody.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

public sealed class CapturedTaskMetadata
{
    public string TaskId { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
    public string SourceReport { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string EnvelopePath { get; set; } = string.Empty;
    public string InstructionPath { get; set; } = string.Empty;
    public int InstructionChars { get; set; }
    public int EnvelopeChars { get; set; }
}
