using System.Text.Json;
using System.Text.Json.Serialization;
using DcsWatcherV2.Controls;
using DcsWatcherV2.Demo;
using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2;

public partial class MainForm : Form
{
    private readonly bool _demoMode;
    private readonly ConfigService _configService = new();
    private readonly ProfileValidator _profileValidator = new();
    private readonly ActivityProjectionService _activityProjection = new();
    private readonly List<ActivityTimelineItem> _activityItems = [];
    private readonly BranchGuardService _branchGuardService = new();
    private AppConfig _installationConfig = new();
    private AppConfig _config = new();
    private AppState _state = new();
    private WatcherPreferences _preferences = new();
    private ProfileService? _profileService;
    private WatcherProfileV1? _activeProfile;
    private StateService? _stateService;
    private LogService? _logService;
    private LedgerService? _ledgerService;
    private WatcherOrchestrator? _orchestrator;
    private OverviewControl _overview = null!;
    private WorkflowEditorControl _workflow = null!;
    private ActivityTimelineControl _activity = null!;
    private ProvenanceDetailsControl _evidence = null!;
    private DiagnosticsControl _diagnostics = null!;
    private bool _startupTestsPassed;
    private bool _busy;
    private bool _operatorPaused;
    private SafeDemoComposition? _demoComposition;
    private DemoRunResult? _lastDemoResult;
    private DemoRunResult? _lastAcceptedDemoResult;
    private int _nextDemoAction;
    private bool _adjustingCommandBar;

    public MainForm(bool demoMode = false)
    {
        _demoMode = demoMode;
        InitializeComponent();
        startButton.Available = !_demoMode;
        if (_demoMode)
        {
            operatingStateLabel.AccessibleDescription = "Operating state is isolated demo; no live automation is active.";
        }
        BuildPages();
        BuildCommandMenu();
        WireEvents();
        AdjustCommandBarLayout();
        Shown += MainForm_Shown;
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        if (_demoMode)
        {
            InitializeDemoState();
            return;
        }

        _installationConfig = _configService.Load();
        _config = _installationConfig;
        _preferences = _configService.LoadPreferences(_installationConfig);
        _profileService = new ProfileService(_configService.GetProfileDirectory(_installationConfig), _profileValidator);
        ReloadProfiles(_preferences.LastSelectedProfileId);
        operatingStateLabel.Text = "Safety checks";
        statusSummaryLabel.Text = "Running mandatory startup checks";
        UpdateUiState();
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        Shown -= MainForm_Shown;
        _startupTestsPassed = await RunRequiredStartupTestsAsync();
        if (!_startupTestsPassed)
        {
            SetBlocked("Startup safety checks failed. Automation is blocked; review Diagnostics.");
            return;
        }

        if (_demoMode)
        {
            operatingStateLabel.Text = "Demo";
            statusSummaryLabel.Text = "Synthetic fixtures only; all live outputs disabled";
            UpdateUiState();
            return;
        }

        if (!_preferences.FirstRunCompleted && _profileService is not null)
        {
            using var wizard = new FirstRunSetupForm(_profileService);
            if (wizard.ShowDialog(this) == DialogResult.OK && wizard.CreatedProfile is not null)
            {
                _preferences.FirstRunCompleted = true;
                _preferences.LastSelectedProfileId = wizard.CreatedProfile.Identity.ProfileId;
                _installationConfig.ActiveProfileId = wizard.CreatedProfile.Identity.ProfileId;
                _configService.SavePreferences(_installationConfig, _preferences);
                _configService.Save(_installationConfig);
                ReloadProfiles(wizard.CreatedProfile.Identity.ProfileId);
            }
        }

        CreateRuntimeAfterSafetyChecks();
        operatingStateLabel.Text = "Stopped";
        statusSummaryLabel.Text = _activeProfile is null ? "Create or import a workflow profile" : "Ready";
        UpdateUiState();
        // Preview never auto-starts. An operator starts a validated profile explicitly.
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _orchestrator?.Stop();
        _orchestrator?.Dispose();
        _demoComposition?.Dispose();
        _demoComposition = null;
        if (!_demoMode)
        {
            _preferences.OverviewSplitterDistance = _overview.SplitterDistance;
            _preferences.LastSelectedPage = mainTabs.SelectedTab?.Text ?? "Overview";
            _configService.SavePreferences(_installationConfig, _preferences);
        }
    }

    private void BuildPages()
    {
        _overview = new OverviewControl();
        _workflow = new WorkflowEditorControl();
        _activity = new ActivityTimelineControl();
        _evidence = new ProvenanceDetailsControl();
        _diagnostics = new DiagnosticsControl();
        AddPage("Overview", _overview);
        AddPage("Workflow", _workflow);
        AddPage("Activity", _activity);
        AddPage("Evidence", _evidence);
        AddPage("Diagnostics", _diagnostics);
    }

    private void BuildCommandMenu()
    {
        if (_demoMode)
        {
            AddMenu("Demo: current-path acceptance", () => ExecuteDemoAction(DemoAction.CurrentPath));
            AddMenu("Demo: identical replay", () => ExecuteDemoAction(DemoAction.Replay));
            AddMenu("Demo: sibling-branch rejection", () => ExecuteDemoAction(DemoAction.SiblingBranch));
            moreButton.DropDownItems.Add(new ToolStripSeparator());
            AddMenu("Reset demo", ResetDemo);
            return;
        }

        AddMenu("Baseline existing reports", async () => await RunLiveActionAsync("Baseline reports", () => _orchestrator!.BaselineExistingReportsAsync()));
        AddMenu("Wake newest report once", async () => await RunPreparedHumanActionAsync(WatcherOrchestrator.WakeNewestReportAction));
        AddMenu("Resend latest report", async () => await RunPreparedHumanActionAsync(WatcherOrchestrator.ResendLatestReportAction));
        moreButton.DropDownItems.Add(new ToolStripSeparator());
        AddMenu("Capture current UI task", async () => await RunLiveActionAsync("Capture UI task", async () => { await _orchestrator!.CaptureLatestTaskFromChatGptAsync(); }));
        AddMenu("Send latest task", async () => await RunLiveActionAsync("Send latest task", async () => { await _orchestrator!.SendLatestTaskToCodexAsync(manualRetry: true); }));
        moreButton.DropDownItems.Add(new ToolStripSeparator());
        AddMenu("Test ChatGPT route", async () => await RunLiveActionAsync("Test ChatGPT route", () => _orchestrator!.TestChatGptWakeAsync()));
        AddMenu("Test Codex route", async () => await RunLiveActionAsync("Test Codex route", async () => { await _orchestrator!.TestCodexWakeAsync(); }));
        AddMenu("List visible windows", async () => await RunLiveActionAsync("List windows", async () => { await _orchestrator!.ListVisibleWindowsAsync(); }));
        moreButton.DropDownItems.Add(new ToolStripSeparator());
        AddMenu("Run first-run setup", ShowFirstRunSetup);
        AddMenu("Open config", () => OpenPath(_configService.GetConfigPath(_config)));
        AddMenu("Open ledger", () => OpenPath(_configService.GetLedgerRoot(_config)));
    }

    private void WireEvents()
    {
        startButton.Click += (_, _) => StartWatcher();
        stopButton.Click += (_, _) => StopWatcher("Stopped by operator");
        runOnceButton.Click += async (_, _) => await RunOnceAsync();
        emergencyPauseButton.Click += (_, _) => EmergencyPause();
        profileSelector.SelectedIndexChanged += (_, _) =>
        {
            SelectProfile();
            AdjustCommandBarLayout();
        };
        commandBar.SizeChanged += (_, _) => AdjustCommandBarLayout();
        DpiChanged += (_, _) => AdjustCommandBarLayout();
        mainTabs.SelectedIndexChanged += (_, _) =>
        {
            if (!_demoMode) _preferences.LastSelectedPage = mainTabs.SelectedTab?.Text ?? "Overview";
        };
        _overview.SplitterDistanceChanged += (_, _) =>
        {
            if (!_demoMode) _preferences.OverviewSplitterDistance = _overview.SplitterDistance;
        };
        _workflow.SaveRequested += (_, _) => SaveActiveProfile();
        _workflow.CreateRequested += (_, _) => CreateProfile();
        _workflow.DuplicateRequested += (_, _) => DuplicateProfile();
        _workflow.ImportRequested += (_, _) => ImportProfile();
        _workflow.ExportRequested += (_, _) => ExportProfile();
        _workflow.ValidateRequested += (_, _) => ValidateActiveProfile();
        _diagnostics.RunOfflineTestsRequested += async (_, _) => await RerunOfflineChecksAsync();
        _diagnostics.ExportBundleRequested += (_, _) =>
        {
            _diagnostics.AppendLog("Diagnostic bundle export is reserved for the release packaging package. No data was exported.");
            statusSummaryLabel.Text = "Diagnostic export placeholder - no files written";
        };
    }

    private void AdjustCommandBarLayout()
    {
        if (_adjustingCommandBar || commandBar.ClientSize.Width <= 0) return;

        _adjustingCommandBar = true;
        try
        {
            var fixedWidth = commandBar.Items.Cast<ToolStripItem>()
                .Where(item => item.Available && item != profileSelector)
                .Sum(item => item.GetPreferredSize(Size.Empty).Width + item.Margin.Horizontal);
            var widthBudget = commandBar.ClientSize.Width - commandBar.Padding.Horizontal - fixedWidth - LogicalToDeviceUnits(4);
            var selectedTextWidth = TextRenderer.MeasureText(
                profileSelector.Text,
                profileSelector.Font,
                Size.Empty,
                TextFormatFlags.NoPadding).Width;
            var minimumWidth = Math.Max(
                LogicalToDeviceUnits(110),
                selectedTextWidth + SystemInformation.VerticalScrollBarWidth + LogicalToDeviceUnits(12));
            var preferredWidth = LogicalToDeviceUnits(190);
            profileSelector.Width = Math.Min(preferredWidth, Math.Max(minimumWidth, widthBudget));
        }
        finally
        {
            _adjustingCommandBar = false;
        }
    }

    private void CreateRuntimeAfterSafetyChecks()
    {
        _orchestrator?.Stop();
        _orchestrator?.Dispose();
        _orchestrator = null;
        _logService = null;
        _stateService = null;
        _ledgerService = null;
        _state = new AppState();
        if (_demoMode || !_startupTestsPassed || _activeProfile is null) return;

        var result = RuntimeComposition.TryCreate(_configService, _installationConfig, _activeProfile, _profileValidator);
        if (!result.Accepted || result.Composition is null)
        {
            statusSummaryLabel.Text = $"Runtime blocked: {result.ReasonCode}";
            _diagnostics.AppendLog($"Runtime composition rejected: {result.ReasonCode}: {result.Message}");
            return;
        }
        if (result.Composition.OfflineOnly)
        {
            statusSummaryLabel.Text = "Offline fixture profiles run only through --demo-ui";
            return;
        }

        _config = result.Composition.Config;
        _stateService = new StateService(_configService);
        _ledgerService = new LedgerService(_configService);
        _logService = new LogService(_configService);
        _logService.EventLogged += LogService_EventLogged;
        _ledgerService.EnsureLedger(_config);
        _logService.Initialize(_config);
        _state = _stateService.Load(_config);
        _state.WatcherRunning = false;
        _state.OperatingStage = _config.OperatingStage;
        _orchestrator = new WatcherOrchestrator(
            _config,
            _state,
            _configService,
            _stateService,
            _logService,
            new GitHubReportPoller(),
            new DirectorReportPublishService(),
            new ChatGptDirectorBridge(),
            new ChatGptWakePromptBuilder(),
            new GitPullService(),
            new ChatGptEnvelopeCapture(),
            new CodexDirectorBridge(_branchGuardService, _ledgerService),
            _ledgerService,
            _branchGuardService,
            result.Composition.TrustContext);
        _orchestrator.StateChanged += (_, _) => UpdateUiState();
    }

    private async Task<bool> RunRequiredStartupTestsAsync()
    {
        try
        {
            var result = await Task.Run(WatcherReleaseTestSuite.RunStartupBoundedAsync);
            foreach (var suite in result.Suites)
            {
                foreach (var message in suite.Messages) _diagnostics.AppendLog($"{suite.Name}: {message}");
            }
            if (!result.Passed)
            {
                throw new InvalidOperationException($"Required startup checks failed ({result.Failed} failures). Runtime activation is blocked.");
            }
            _diagnostics.SetHealth(HealthRows("PASS"));
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.AppendLog("BLOCKED: " + ex);
            _diagnostics.SetHealth(HealthRows("BLOCKED"));
            return false;
        }
    }

    private void InitializeDemoState()
    {
        _config = new AppConfig
        {
            ActiveProfileId = "build-week-demo",
            OperatingStage = "PreviewDemo",
            StartWatcherOnLaunch = false,
            SubmitChatGptPrompt = false,
            SubmitCodexPrompt = false,
            AutoCaptureChatGptEnvelope = false,
            AutomaticWakeEnabled = false,
            AutomaticDeliveryEnabled = false,
            AutomaticInstructionDeliveryEnabled = false,
            LiveCodexIntakeEnabled = false,
            Stage4Authorized = false,
            Stage5Authorized = false
        };
        _activeProfile = CreateDemoProfile();
        profileSelector.Items.Add(new ProfileChoice(_activeProfile.Identity.ProfileId, _activeProfile.Identity.Name));
        profileSelector.SelectedIndex = 0;
        _workflow.LoadProfile(_activeProfile, readOnly: true, syntheticDemo: true);
        ResetDemo();
        _preferences = new WatcherPreferences { FirstRunCompleted = true, OverviewSplitterDistance = 245 };
        _overview.SplitterDistance = 245;
        operatingStateLabel.Text = "Demo";
        UpdateUiState();
    }

    private static WatcherProfileV1 CreateDemoProfile()
    {
        return new WatcherProfileV1
        {
            Enabled = false,
            Identity = new ProfileIdentityV1 { ProfileId = "build-week-demo", Name = "Build Week Demo", Description = "Sanitized, offline fixture and non-actionable test sink." },
            ReportSource = new ReportSourceProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.ReportDemoFixture } },
            Director = new DirectorProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DirectorDemoFixture } },
            Destination = new DestinationProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DeliveryTestSink } },
            AutomationPolicy = new AutomationPolicyProfileV1 { Kind = WatcherAutomationPolicyKind.ManualApproval, RequireVisibleHumanApproval = true },
            Guardrails = new GuardrailsProfileV1 { StopOnFailure = true, StopOnBranchDivergence = true }
        };
    }

    private void StartWatcher()
    {
        if (!CanUseRuntime(out var reason))
        {
            statusSummaryLabel.Text = reason;
            return;
        }
        _operatorPaused = false;
        _orchestrator!.Start();
        operatingStateLabel.Text = "Running";
        statusSummaryLabel.Text = "Watcher running under " + PolicyLabel();
        UpdateUiState();
    }

    private void StopWatcher(string reason)
    {
        _orchestrator?.Stop();
        _state.WatcherRunning = false;
        operatingStateLabel.Text = _operatorPaused ? "Paused" : "Stopped";
        statusSummaryLabel.Text = reason;
        UpdateUiState();
    }

    private void EmergencyPause()
    {
        _operatorPaused = true;
        StopWatcher("Emergency pause active; explicit Start is required to resume");
        AddActivity(new ActivityTimelineItem { Stage = "Authorization", Title = "Emergency pause", Detail = "Operator interrupted the workflow", Result = "PAUSED" });
    }

    private async Task RunOnceAsync()
    {
        if (_demoMode)
        {
            ExecuteDemoAction(_nextDemoAction switch
            {
                0 => DemoAction.CurrentPath,
                1 => DemoAction.Replay,
                2 => DemoAction.SiblingBranch,
                _ => DemoAction.Complete
            });
            return;
        }
        await RunLiveActionAsync("Run once", async () => { await _orchestrator!.ScanNowAsync(); });
    }

    private async Task RunLiveActionAsync(string name, Func<Task> action)
    {
        if (!CanUseRuntime(out var reason))
        {
            statusSummaryLabel.Text = reason;
            return;
        }
        if (_busy) return;
        _busy = true;
        UpdateUiState();
        try
        {
            statusSummaryLabel.Text = name + " in progress";
            await action();
            statusSummaryLabel.Text = name + " completed";
        }
        catch (Exception ex)
        {
            _state.ErrorCount++;
            _logService?.Error(ex.Message, "UI");
            statusSummaryLabel.Text = name + " failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateUiState();
        }
    }

    private async Task RunPreparedHumanActionAsync(string action)
    {
        if (!CanUseRuntime(out var reason))
        {
            statusSummaryLabel.Text = reason;
            return;
        }
        if (_busy || _activeProfile is null) return;

        _busy = true;
        UpdateUiState();
        PreparedHumanAction? prepared = null;
        var decisionMade = false;
        try
        {
            statusSummaryLabel.Text = "Preparing exact human-confirmed action";
            var preparation = await _orchestrator!.PrepareHumanActionAsync(action, _activeProfile);
            if (!preparation.Prepared || preparation.Action is null)
            {
                statusSummaryLabel.Text = "Preparation blocked: " + preparation.Message;
                return;
            }

            prepared = preparation.Action;
            statusSummaryLabel.Text = "Prepared action awaiting confirmation";
            using var dialog = new HumanConfirmationForm(prepared);
            if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.ExplicitApprovalChecked)
            {
                _orchestrator.CancelPreparedHumanAction(prepared);
                decisionMade = true;
                statusSummaryLabel.Text = "Prepared action cancelled; no delivery attempted";
                return;
            }

            decisionMade = true;
            var confirmation = prepared.CreateConfirmationRecord();
            var result = await _orchestrator.ExecutePreparedHumanActionAsync(
                prepared,
                confirmation,
                _activeProfile);
            statusSummaryLabel.Text = result.Message;
        }
        catch (Exception ex)
        {
            if (prepared is not null && !decisionMade)
            {
                _orchestrator?.CancelPreparedHumanAction(prepared);
            }
            _state.ErrorCount++;
            _logService?.Error(ex.Message, "UI");
            statusSummaryLabel.Text = "Prepared action failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateUiState();
        }
    }

    private bool CanUseRuntime(out string reason)
    {
        if (_demoMode) { reason = "Demo mode cannot address live services"; return false; }
        if (!_startupTestsPassed) { reason = "Startup safety checks have not passed"; return false; }
        if (_operatorPaused) { reason = "Emergency pause is active"; return false; }
        if (_activeProfile is null) { reason = "Select a workflow profile"; return false; }
        if (!_activeProfile.Enabled) { reason = "The selected profile is disabled"; return false; }
        if (!_profileValidator.Validate(_activeProfile).IsValid) { reason = "The selected profile is invalid"; return false; }
        if (_orchestrator is null) { reason = "Runtime composition is unavailable"; return false; }
        reason = string.Empty;
        return true;
    }

    private void ReloadProfiles(string? preferredId = null)
    {
        if (_profileService is null) return;
        profileSelector.Items.Clear();
        foreach (var id in _profileService.ListProfileIds())
        {
            try
            {
                var profile = _profileService.Load(id);
                profileSelector.Items.Add(new ProfileChoice(id, profile.Identity.Name));
            }
            catch (Exception ex)
            {
                _diagnostics.AppendLog($"Profile '{id}' was not loaded: {ex.Message}");
            }
        }
        var requested = preferredId ?? _installationConfig.ActiveProfileId;
        profileSelector.SelectedItem = profileSelector.Items.Cast<ProfileChoice>().FirstOrDefault(item => item.Id == requested) ?? profileSelector.Items.Cast<ProfileChoice>().FirstOrDefault();
        if (profileSelector.SelectedIndex < 0 && profileSelector.Items.Count > 0) profileSelector.SelectedIndex = 0;
        SelectRememberedPage();
    }

    private void SelectProfile()
    {
        if (profileSelector.SelectedItem is not ProfileChoice choice) return;
        if (_demoMode)
        {
            if (_activeProfile is not null) _workflow.LoadProfile(_activeProfile);
            return;
        }
        if (_profileService is null) return;
        _activeProfile = _profileService.Load(choice.Id);
        _installationConfig.ActiveProfileId = choice.Id;
        _preferences.LastSelectedProfileId = choice.Id;
        _configService.Save(_installationConfig);
        _configService.SavePreferences(_installationConfig, _preferences);
        _workflow.LoadProfile(_activeProfile);
        ValidateActiveProfile();
        if (_startupTestsPassed) CreateRuntimeAfterSafetyChecks();
        UpdateUiState();
    }

    private void SaveActiveProfile()
    {
        if (_activeProfile is null) return;
        var profile = _workflow.ApplyToProfile();
        var validation = _profileValidator.Validate(profile);
        _workflow.ShowValidation(validation);
        if (!validation.IsValid)
        {
            statusSummaryLabel.Text = "Profile not saved: validation failed";
            return;
        }
        if (_demoMode)
        {
            statusSummaryLabel.Text = "Demo profile updated in memory only";
            UpdateUiState();
            return;
        }
        _profileService!.Save(profile);
        _installationConfig.ActiveProfileId = profile.Identity.ProfileId;
        _preferences.LastSelectedProfileId = profile.Identity.ProfileId;
        _configService.Save(_installationConfig);
        _configService.SavePreferences(_installationConfig, _preferences);
        ReloadProfiles(profile.Identity.ProfileId);
        statusSummaryLabel.Text = "Profile saved";
    }

    private void CreateProfile()
    {
        if (_demoMode) { statusSummaryLabel.Text = "Profile creation is disabled in isolated demo mode"; return; }
        var profile = _profileService!.CreateFresh("New workflow");
        _profileService.Save(profile, overwrite: false);
        ReloadProfiles(profile.Identity.ProfileId);
    }

    private void DuplicateProfile()
    {
        if (_demoMode || _activeProfile is null || _profileService is null) { statusSummaryLabel.Text = "Duplicate is unavailable in demo mode"; return; }
        var json = JsonSerializer.Serialize(_workflow.ApplyToProfile());
        var duplicate = JsonSerializer.Deserialize<WatcherProfileV1>(json)!;
        duplicate.Identity.ProfileId = Guid.NewGuid().ToString("N");
        duplicate.Identity.Name += " copy";
        duplicate.Enabled = false;
        _profileService.Save(duplicate, overwrite: false);
        ReloadProfiles(duplicate.Identity.ProfileId);
    }

    private void ImportProfile()
    {
        if (_demoMode || _profileService is null) { statusSummaryLabel.Text = "Import is unavailable in demo mode"; return; }
        using var dialog = new OpenFileDialog { Filter = "Watcher profile (*.watcher-profile.json)|*.watcher-profile.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            var profile = JsonSerializer.Deserialize<WatcherProfileV1>(File.ReadAllText(dialog.FileName), options) ?? throw new InvalidDataException("Profile is empty.");
            profile.Enabled = false;
            profile.AutomationPolicy.Kind = WatcherAutomationPolicyKind.ManualApproval;
            profile.AutomationPolicy.RequireVisibleHumanApproval = true;
            _profileService.Save(profile, overwrite: false);
            ReloadProfiles(profile.Identity.ProfileId);
            statusSummaryLabel.Text = "Imported profile is disabled and requires review";
        }
        catch (Exception ex)
        {
            statusSummaryLabel.Text = "Import blocked: " + ex.Message;
        }
    }

    private void ExportProfile()
    {
        if (_demoMode || _activeProfile is null || _profileService is null) { statusSummaryLabel.Text = "Export is unavailable in demo mode"; return; }
        using var dialog = new SaveFileDialog { FileName = _activeProfile.Identity.ProfileId + ".watcher-profile.json", Filter = "Watcher profile (*.watcher-profile.json)|*.watcher-profile.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllBytes(dialog.FileName, _profileService.SerializeCanonical(_workflow.ApplyToProfile()));
        statusSummaryLabel.Text = "Profile exported without credentials";
    }

    private void ValidateActiveProfile()
    {
        if (_activeProfile is null) return;
        var validation = _profileValidator.Validate(_workflow.ApplyToProfile());
        _workflow.ShowValidation(validation);
        statusSummaryLabel.Text = validation.IsValid ? "Profile validation passed" : "Profile validation blocked activation";
    }

    private void ShowFirstRunSetup()
    {
        if (_demoMode || _profileService is null) return;
        using var wizard = new FirstRunSetupForm(_profileService);
        if (wizard.ShowDialog(this) == DialogResult.OK && wizard.CreatedProfile is not null) ReloadProfiles(wizard.CreatedProfile.Identity.ProfileId);
    }

    private async Task RerunOfflineChecksAsync()
    {
        statusSummaryLabel.Text = "Running offline checks";
        _startupTestsPassed = await RunRequiredStartupTestsAsync();
        statusSummaryLabel.Text = _startupTestsPassed ? "Offline checks passed" : "Offline checks failed; runtime blocked";
        UpdateUiState();
    }

    private void LogService_EventLogged(object? sender, WatchEvent watchEvent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => LogService_EventLogged(sender, watchEvent));
            return;
        }
        _diagnostics.AppendLog(watchEvent.ToString());
        AddActivity(_activityProjection.Project(watchEvent));
    }

    private void AddActivity(ActivityTimelineItem item)
    {
        _activityItems.Add(item);
        _activity.SetItems(_activityItems);
        _overview.SetActivity(_activityItems);
    }

    private void UpdateUiState()
    {
        if (InvokeRequired) { BeginInvoke(UpdateUiState); return; }
        var profileName = _activeProfile?.Identity.Name ?? "No profile";
        var policy = PolicyLabel();
        var enabled = _activeProfile?.Enabled == true;
        if (_demoMode) operatingStateLabel.Text = "Demo";
        else if (_state.WatcherRunning) operatingStateLabel.Text = "Running";
        else if (_operatorPaused) operatingStateLabel.Text = "Paused";
        else if (!_startupTestsPassed) operatingStateLabel.Text = "Blocked";
        else operatingStateLabel.Text = "Stopped";

        startButton.Enabled = !_busy && !_demoMode && _startupTestsPassed && enabled && !_operatorPaused;
        stopButton.Enabled = !_demoMode;
        stopButton.ToolTipText = _demoMode
            ? "Unavailable in isolated demo mode because no watcher runtime is started."
            : "Stop the active watcher runtime.";
        stopButton.AccessibleDescription = stopButton.ToolTipText;
        emergencyPauseButton.Enabled = !_demoMode;
        emergencyPauseButton.ToolTipText = _demoMode
            ? "Emergency Pause is unavailable in isolated demo mode because synthetic actions do not start a watcher runtime."
            : "Emergency Pause: immediately pause the active watcher runtime.";
        emergencyPauseButton.AccessibleDescription = emergencyPauseButton.ToolTipText;
        runOnceButton.Enabled = !_busy && (_demoMode || (_startupTestsPassed && enabled && !_operatorPaused));
        profileSelector.Enabled = !_demoMode && !_busy && !_state.WatcherRunning;
        policyStatusLabel.Text = $"Policy: {policy}";
        statusBar.SizingGrip = false;

        var warning = _demoMode ? string.Empty : !enabled ? "Selected profile is disabled" : _operatorPaused ? "Emergency pause is active" : string.Empty;
        _overview.UpdateSummary(operatingStateLabel.Text ?? "Unknown", profileName, PipelineText(), PipelineDescription(), warning, LastSuccessText(), NextPollText());
        var profileHash = _activeProfile is null ? string.Empty : ComputeProfileHash(_activeProfile);
        _evidence.ShowAudit(_state, profileName, profileHash, BuildArtifactSummary());
        _activity.SetItems(_activityItems);
        _overview.SetActivity(_activityItems);
    }

    private string PipelineText()
    {
        if (_demoMode) return "Fixture -> approval -> test sink";
        if (_state.WatcherRunning) return "Report source -> lineage -> authorization -> destination";
        return "Idle; no transaction in progress";
    }

    private string PipelineDescription() => _demoMode
        ? "Synthetic fixture input, lineage validation, manual approval, test-sink delivery, and replay rejection."
        : $"Transaction pipeline: {PipelineText()}.";

    private string LastSuccessText() => _demoMode
        ? _lastAcceptedDemoResult is not null
            ? $"{_lastAcceptedDemoResult.Evidence.TransactionId} accepted once by the test sink"
            : "No demo transaction accepted yet"
        : _state.LastCodexSendSucceededAtUtc?.ToLocalTime().ToString("g") ?? "No successful delivery recorded";
    private string NextPollText() => _state.WatcherRunning ? $"Every {_config.PollSeconds} seconds" : "Not scheduled while stopped";
    private string PolicyLabel() => _activeProfile?.AutomationPolicy.Kind switch
    {
        WatcherAutomationPolicyKind.ManualApproval => "Manual approval",
        WatcherAutomationPolicyKind.PlannedAutomatic => "Planned autopilot",
        WatcherAutomationPolicyKind.SupervisedAutomatic => "Supervised checkpoints",
        WatcherAutomationPolicyKind.ContinuousAutomatic => "Continuous autopilot",
        WatcherAutomationPolicyKind.AuditOnly => "Audit only",
        _ => "Unavailable"
    };

    private string BuildArtifactSummary()
    {
        if (_demoMode)
        {
            var evidence = _lastDemoResult?.Evidence;
            return string.Join("\r\n", new[]
            {
                "Synthetic fixture: in-memory only",
                "Destination: non-actionable test sink",
                "Live output: technically disabled",
                $"Disposition: {evidence?.Disposition ?? "Not executed"}",
                $"Envelope SHA-256: {DisplayEvidenceValue(evidence?.EnvelopeSha256)}",
                $"Provenance SHA-256: {DisplayEvidenceValue(evidence?.ProvenanceSha256)}",
                $"Signer fingerprint: {DisplayEvidenceValue(evidence?.SignerFingerprintSha256)}",
                $"Sink accepted: {_demoComposition?.Sink.AcceptedCount ?? 0}",
                $"Sink received: {_demoComposition?.Sink.ReceiveCount ?? 0}",
                $"Signing count: {_demoComposition?.SigningCount ?? 0}",
                $"Delivery attempts: {_demoComposition?.DeliveryAttemptCount ?? 0}"
            });
        }
        return string.Join(Environment.NewLine, new[] { _state.LastCapturedEnvelopePath, _state.LastCapturedInstructionPath, _state.PendingCodexTaskPath }.Where(path => !string.IsNullOrWhiteSpace(path)));
    }

    private void ExecuteDemoAction(DemoAction action)
    {
        if (!_demoMode) return;
        if (action == DemoAction.Reset)
        {
            ResetDemo();
            return;
        }
        _demoComposition ??= new SafeDemoComposition();

        if (action == DemoAction.Complete)
        {
            statusSummaryLabel.Text = "Demo sequence complete. Reset to run it again.";
            return;
        }
        if (action == DemoAction.Replay && _demoComposition.Sink.AcceptedCount == 0)
        {
            statusSummaryLabel.Text = "Run current-path acceptance before the identical replay.";
            return;
        }

        var result = action switch
        {
            DemoAction.CurrentPath => _demoComposition.RunCurrentPath(),
            DemoAction.Replay => _demoComposition.ReplayCurrentPath(),
            DemoAction.SiblingBranch => _demoComposition.RunSiblingBranch(),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        _lastDemoResult = result;
        if (result.Accepted) _lastAcceptedDemoResult = result;
        ProjectDemoResult(result);
        _nextDemoAction = action switch
        {
            DemoAction.CurrentPath => Math.Max(_nextDemoAction, 1),
            DemoAction.Replay => Math.Max(_nextDemoAction, 2),
            DemoAction.SiblingBranch => 3,
            _ => _nextDemoAction
        };
        statusSummaryLabel.Text = $"{result.Disposition}: {result.Message}";
        UpdateUiState();
    }

    private void ProjectDemoResult(DemoRunResult result)
    {
        foreach (var record in result.Activity)
        {
            AddActivity(new ActivityTimelineItem
            {
                Timestamp = record.TimestampUtc,
                Stage = record.Stage,
                Title = record.Status,
                Detail = record.Message,
                Result = record.Status
            });
        }

        var fixture = result.Evidence;
        _state.TransactionAudit.ConversationId = fixture.ConversationId;
        _state.TransactionAudit.WakeMessageId = fixture.WakeMessageId;
        _state.TransactionAudit.ResponseMessageId = fixture.AssistantMessageId;
        _state.TransactionAudit.ResponseParentId = fixture.AssistantParentMessageId;
        _state.TransactionAudit.CurrentNode = fixture.CurrentNode;
        _state.TransactionAudit.OnCurrentPath = fixture.OnCurrentPath;
        _state.TransactionAudit.CaptureMethod = "Synthetic authenticated message object";
        _state.TransactionAudit.FallbackBody = false;
        _state.TransactionAudit.ApiVerification = true;
        _state.TransactionAudit.EnvelopeTaskId = fixture.TaskId;
        _state.TransactionAudit.EnvelopeSha256 = fixture.EnvelopeSha256;
        _state.TransactionAudit.EligibilityResult = fixture.Disposition;
        _state.LastCodexSendResult = fixture.Disposition;
    }

    private void ResetDemo()
    {
        if (!_demoMode) return;
        _demoComposition?.Dispose();
        _demoComposition = new SafeDemoComposition();
        _lastDemoResult = null;
        _lastAcceptedDemoResult = null;
        _nextDemoAction = 0;
        _state = new AppState { WatcherRunning = false, OperatingStage = "PreviewDemo" };
        _activityItems.Clear();
        statusSummaryLabel.Text = "Ready. Run Once executes current-path acceptance.";
        UpdateUiState();
    }

    internal void InitializeDemoForSelfTest()
    {
        if (!_demoMode) throw new InvalidOperationException("Self-test demo initialization requires demo mode.");
        if (_demoComposition is null) InitializeDemoState();
    }

    internal DemoRunResult? ExecuteDemoForSelfTest(string action)
    {
        InitializeDemoForSelfTest();
        ExecuteDemoAction(action switch
        {
            "current" => DemoAction.CurrentPath,
            "replay" => DemoAction.Replay,
            "sibling" => DemoAction.SiblingBranch,
            "reset" => DemoAction.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        });
        return _lastDemoResult;
    }

    internal int DemoActivityCount => _activityItems.Count;
    internal int DemoSinkAcceptedCount => _demoComposition?.Sink.AcceptedCount ?? 0;
    internal int DemoSinkReceiveCount => _demoComposition?.Sink.ReceiveCount ?? 0;
    internal int DemoSigningCount => _demoComposition?.SigningCount ?? 0;
    internal int DemoDeliveryAttemptCount => _demoComposition?.DeliveryAttemptCount ?? 0;
    internal string DemoStatusSummary => statusSummaryLabel.Text ?? string.Empty;
    internal string DemoLastSuccess => LastSuccessText();
    internal string DemoArtifactSummary => BuildArtifactSummary();
    internal string DemoEvidenceValue(string name) => name switch
    {
        "Conversation" => DisplayEvidenceValue(_state.TransactionAudit.ConversationId),
        "Task ID" => DisplayEvidenceValue(_state.TransactionAudit.EnvelopeTaskId),
        "Envelope SHA-256" => DisplayEvidenceValue(_state.TransactionAudit.EnvelopeSha256),
        "Authorization" => DisplayEvidenceValue(_state.TransactionAudit.EligibilityResult),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static string DisplayEvidenceValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not available" : value;

    internal void StopForFatalError()
    {
        _orchestrator?.Stop();
        _orchestrator?.Dispose();
        _orchestrator = null;
        _demoComposition?.Dispose();
        _demoComposition = null;
    }

    private IEnumerable<(string Component, string Status, string Notes)> HealthRows(string safetyStatus)
    {
        yield return ("Startup safety suite", safetyStatus, safetyStatus == "PASS" ? "Required offline checks passed" : "Runtime construction and auto-start blocked");
        yield return ("Profile", _activeProfile is null ? "NOT CONFIGURED" : _profileValidator.Validate(_activeProfile).IsValid ? "VALID" : "BLOCKED", _activeProfile?.Identity.Name ?? "Create or import a profile");
        yield return ("ChatGPT current-path adapter", _demoMode ? "DISABLED" : "EXPERIMENTAL", "Private browser interface; exact current-path lineage remains mandatory");
        yield return ("Codex verified IPC", _demoMode ? "DISABLED" : "EXPERIMENTAL", "Private desktop interface; destination-bound verification required");
        yield return ("Live outputs", _demoMode ? "TECHNICALLY DISABLED" : _activeProfile?.Enabled == true ? "PROFILE CONTROLLED" : "DISABLED", _demoMode ? "No live adapters are constructed" : "Start requires a valid enabled profile");
    }

    private void SetBlocked(string message)
    {
        _startupTestsPassed = false;
        _operatorPaused = true;
        operatingStateLabel.Text = "Blocked";
        statusSummaryLabel.Text = message;
        mainTabs.SelectedIndex = 4;
        UpdateUiState();
    }

    private void AddPage(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(content);
        mainTabs.TabPages.Add(page);
    }

    private void AddMenu(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        moreButton.DropDownItems.Add(item);
    }

    private void AddMenu(string text, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += async (_, _) => await action();
        moreButton.DropDownItems.Add(item);
    }

    private void OpenPath(string path)
    {
        if (_demoMode) { statusSummaryLabel.Text = "File access is disabled in isolated demo mode"; return; }
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) Directory.CreateDirectory(Path.GetExtension(path).Length == 0 ? path : Path.GetDirectoryName(path)!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { statusSummaryLabel.Text = "Open failed: " + ex.Message; }
    }

    private void SelectRememberedPage()
    {
        var page = mainTabs.TabPages.Cast<TabPage>().FirstOrDefault(tab => tab.Text.Equals(_preferences.LastSelectedPage, StringComparison.OrdinalIgnoreCase));
        if (page is not null) mainTabs.SelectedTab = page;
        BeginInvoke(() => _overview.SplitterDistance = _preferences.OverviewSplitterDistance);
    }

    private static string ComputeProfileHash(WatcherProfileV1 profile)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record ProfileChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private enum DemoAction
    {
        CurrentPath,
        Replay,
        SiblingBranch,
        Complete,
        Reset
    }
}
