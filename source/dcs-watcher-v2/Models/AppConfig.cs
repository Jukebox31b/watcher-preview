namespace DcsWatcherV2.Models;

public sealed class AppConfig
{
    public string InstallationRoot { get; set; } = @"%LOCALAPPDATA%\Watcher";
    public string InstallationSecurityRoot { get; set; } = @"%LOCALAPPDATA%\Watcher\security";
    public string ActiveProfileId { get; set; } = string.Empty;
    public string RuntimeProfileId { get; set; } = string.Empty;
    public string RuntimeProfileRoot { get; set; } = string.Empty;
    public string ProfileConfigurationSha256 { get; set; } = string.Empty;
    public bool RuntimeComposedFromProfile { get; set; }
    public string LegacyEvidenceRoot { get; set; } = string.Empty;
    public string ProfileDirectory { get; set; } = @"%LOCALAPPDATA%\Watcher\profiles";
    public string WatcherPreferencesPath { get; set; } = @"%LOCALAPPDATA%\Watcher\preferences.json";
    public string OperatingStage { get; set; } = nameof(WatcherOperatingStage.Stage1DetectOnly);
    public string InstructionDeliveryMode { get; set; } = nameof(AuthorizedInstructionDeliveryMode.HashBoundFile);
    public bool RequireHumanWakeConfirmation { get; set; } = true;
    public bool AutomaticInstructionDeliveryEnabled { get; set; }
    public bool LiveManualPilotAuthorized { get; set; }
    public bool AutomaticWakeEnabled { get; set; }
    public bool AutomaticDeliveryEnabled { get; set; }
    public bool LiveCodexIntakeEnabled { get; set; }
    public bool Stage4Authorized { get; set; }
    public bool Stage5Authorized { get; set; }
    public string Stage4BootstrapReportPath { get; set; } = string.Empty;
    public string Stage4BootstrapReportSha256 { get; set; } = string.Empty;
    public string Stage4IntakeExecutablePath { get; set; } = string.Empty;
    public bool Stage4StopOnFailure { get; set; } = true;
    public string Stage3BuildAttestationPath { get; set; } = string.Empty;
    public string Stage3TrustStorePath { get; set; } = string.Empty;
    public string Stage3TrustRootPath { get; set; } = string.Empty;
    public string Stage3TrustAnchorPath { get; set; } = string.Empty;
    public string Stage3BuildGenerationAnchorPath { get; set; } = string.Empty;
    public string Stage3IntakePolicyPath { get; set; } = string.Empty;
    public string Stage3OutboundReplayLedgerDirectory { get; set; } = string.Empty;
    public string Stage3IntakeReplayLedgerDirectory { get; set; } = string.Empty;
    public string ApprovedInstructionDirectory { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public string LocalRepoPath { get; set; } = string.Empty;
    public string GitHubBlobBase { get; set; } = string.Empty;
    public string GitHubReportsFolder { get; set; } = string.Empty;
    public string ReportRepoFullName { get; set; } = string.Empty;
    public string ReportGitRoot { get; set; } = string.Empty;
    public string ReportRemote { get; set; } = "origin";
    public string ReportBranch { get; set; } = string.Empty;
    public string ReportFolder { get; set; } = string.Empty;
    public string ReportGitHubBlobBase { get; set; } = string.Empty;
    public string ReportPollMode { get; set; } = "LocalFolder";
    public bool AutoPublishLocalReportCommits { get; set; }
    public string LocalReportRoot { get; set; } = string.Empty;
    public string ChatGptDirectorUrl { get; set; } = string.Empty;
    public string ChatGptTitle { get; set; } = string.Empty;
    public string CodexThreadId { get; set; } = string.Empty;
    public string CodexDirectorThreadId { get; set; } = string.Empty;
    public string CodexDeliveryMode { get; set; } = "HashBoundFile";
    public string CodexIpcPipeName { get; set; } = "\\\\" + @".\pipe\codex-ipc";
    public string CodexDeliveryTransport { get; set; } = "IpcOnly";
    public int CodexIpcConnectTimeoutSeconds { get; set; } = 10;
    public int CodexIpcResponseTimeoutSeconds { get; set; } = 30;
    public bool CodexRequireConfirmedTurnStart { get; set; } = true;
    public bool CodexOpenThreadUrlOnIpcFailure { get; set; }
    public bool CodexCliFallbackOnNoOwner { get; set; }
    public string CodexCliPath { get; set; } = string.Empty;
    public int CodexCliStartTimeoutSeconds { get; set; } = 20;
    public bool CodexUiPasteFallbackEnabled { get; set; }
    public bool AutoSendCapturedTaskToCodex { get; set; }
    public string CodexWindowTitleContains { get; set; } = string.Empty;
    public string CodexDirectorTitle { get; set; } = string.Empty;
    public string CodexLaunchCommand { get; set; } = string.Empty;
    public int CodexSendTimeoutSeconds { get; set; } = 30;
    public int CodexFocusRetryCount { get; set; } = 3;
    public bool CodexUseClipboardFallback { get; set; }
    public bool CodexRestoreClipboardAfterSend { get; set; } = true;
    public int CodexDeliveryConfirmationSeconds { get; set; } = 10;
    public bool RequirePendingTaskBeforeCodexSend { get; set; } = true;
    public string CodexSendKeystroke { get; set; } = "Enter";
    public int PollSeconds { get; set; } = 60;
    public bool GitPullBeforeScan { get; set; }
    public bool SubmitChatGptPrompt { get; set; }
    public bool SubmitCodexPrompt { get; set; }
    public bool StartWatcherOnLaunch { get; set; }
    public string ChatGptMode { get; set; } = "Edge CDP";
    public string ChatGptCdpHost { get; set; } = "127.0.0.1";
    public int ChatGptCdpPort { get; set; } = 9222;
    public string EdgeUserDataDir { get; set; } = @"%LOCALAPPDATA%\Watcher\edge";
    public string EdgeExecutablePath { get; set; } = string.Empty;
    public int ChatGptSendTimeoutSeconds { get; set; } = 45;
    public int ChatGptBusyRecoverySeconds { get; set; } = 300;
    public bool ChatGptExactUrlRequired { get; set; } = true;
    public bool ChatGptOpenIfMissing { get; set; }
    public bool ChatGptStopIfBusyAfterTimeout { get; set; }
    public bool AutoCaptureChatGptEnvelope { get; set; }
    public int ChatGptCaptureTimeoutSeconds { get; set; } = 600;
    public int ChatGptLateEnvelopeSweepSeconds { get; set; } = 300;
    public int ChatGptStableResponseSeconds { get; set; } = 8;
    public string ChatGptCaptureScope { get; set; } = "BackendMessageObject";
    public bool RequireSingleTaskEnvelope { get; set; } = true;
    public bool AllowRepoMismatch { get; set; }
    public bool CaptureNewestEnvelopeOnly { get; set; } = true;
    public int MinInstructionChars { get; set; } = 100;
    public int MaxEnvelopeChars { get; set; } = 500000;
    public bool UiBridgeToCodex { get; set; }
    public bool KeepSystemAwakeWhileRunning { get; set; }
    public bool BranchLockEnabled { get; set; }
    public string AllowedBranch { get; set; } = string.Empty;
    public string LedgerRoot { get; set; } = string.Empty;
    public int MaxLedgerDays { get; set; } = 30;
    public int MaxTaskFiles { get; set; } = 500;
    public int MaxLogDays { get; set; } = 14;
    public string ExpectedRepo { get; set; } = string.Empty;
    public bool AllowRepoOverride { get; set; }
}
