namespace DcsWatcherV2.Services;

public sealed record NotificationTemplateRequest(
    string ReportName,
    string ReportSha256,
    string Summary,
    string SourceLabel = "configured report source");

public sealed record NotificationTemplateResult(string Prompt, bool FollowOnRequested);

public sealed class PromptTemplateService
{
    public NotificationTemplateResult BuildNotification(
        NotificationTemplateRequest request,
        bool requestFollowOnInstruction = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireValue(request.ReportName, nameof(request.ReportName));
        RequireValue(request.ReportSha256, nameof(request.ReportSha256));
        RequireValue(request.Summary, nameof(request.Summary));
        RequireValue(request.SourceLabel, nameof(request.SourceLabel));

        var prompt = string.Join("\n", new[]
        {
            "A report is available for review.",
            $"Source: {request.SourceLabel}",
            $"Report: {request.ReportName}",
            $"SHA-256: {request.ReportSha256}",
            $"Summary: {request.Summary.Trim()}",
            string.Empty,
            requestFollowOnInstruction
                ? "A follow-on instruction was explicitly requested. Return one complete, source-bound instruction envelope."
                : "Notification only. Do not issue or infer a follow-on instruction."
        });

        return new NotificationTemplateResult(prompt, requestFollowOnInstruction);
    }

    private static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", parameterName);
    }
}
