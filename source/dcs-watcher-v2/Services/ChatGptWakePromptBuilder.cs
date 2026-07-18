using DcsWatcherV2.Models;
using System.Text;

namespace DcsWatcherV2.Services;

public sealed class ChatGptWakePromptBuilder
{
    public string Build(
        AppConfig config,
        ReportCandidate report,
        string wakeToken,
        bool requestFollowOnInstruction = false)
    {
        var reportText = new UTF8Encoding(false, true).GetString(report.ContentBytes);
        var lines = new List<string>
        {
            "Wake up and consume the authenticated Codex Director report included below.",
            "Do not browse, open the link, call a tool, or fetch another copy. Use only the exact report body included in this message.",
            string.Empty,
            "Watcher token:",
            wakeToken,
            string.Empty,
            "Report:",
            report.GitHubBlobUrl,
            string.Empty,
            "Source file:",
            report.RelativePath,
            string.Empty,
            "Authenticated report commit:",
            report.Commit,
            string.Empty,
            "Authenticated report SHA-256:",
            report.Fingerprint,
            string.Empty,
            "BEGIN_AUTHENTICATED_REPORT",
            reportText,
            "END_AUTHENTICATED_REPORT"
        };

        if (!requestFollowOnInstruction)
        {
            lines.AddRange([
                string.Empty,
                "Report notification only. Do not issue a follow-on Codex task or task envelope in response to this wake."
            ]);
            return string.Join(Environment.NewLine, lines);
        }

        lines.AddRange([
            string.Empty,
            "A separately authorized workflow has explicitly requested one follow-on Codex Director instruction.",
            "After consuming the report, issue that instruction directly in this ChatGPT conversation using exactly one strict envelope:",
            string.Empty,
            "<<<DCS_CODEX_TASK_V1>>>",
            "task_id: REAL_SC_OR_MEC_ID-YYYYMMDD-HHMMSS",
            "origin: chatgpt-ui",
            $"repo: {config.ExpectedRepo}",
            "target: codex-director",
            "mode: instruction",
            "created_at: ISO-8601 timestamp",
            $"source_report: {report.FileName}",
            string.Empty,
            "BEGIN_INSTRUCTION",
            "<full Codex Director instruction goes here>",
            "END_INSTRUCTION",
            "<<<END_DCS_CODEX_TASK_V1>>>",
            string.Empty,
            "Do not write a CGPT-TASK file to GitHub.",
            "Do not use chatgpt-bridge/inbox_to_codex.",
            "Do not include more than one task envelope.",
            "Do not treat prior ChatGPT UI envelopes as completed Codex work.",
            "Only use verified Codex reports/artifacts as completed work."
        ]);
        return string.Join(Environment.NewLine, lines);
    }
}
