using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Controls;

public sealed class WorkflowEditorControl : UserControl
{
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly CheckBox _enabled = new() { Text = "Profile enabled", AutoSize = true };
    private readonly ComboBox _reportAdapter = Choice();
    private readonly TextBox _repository = new();
    private readonly TextBox _branch = new();
    private readonly ComboBox _directorAdapter = Choice();
    private readonly TextBox _conversation = new();
    private readonly ComboBox _destinationAdapter = Choice();
    private readonly TextBox _destination = new();
    private readonly ComboBox _policy = Choice();
    private readonly NumericUpDown _taskLimit = Number(0, 10_000);
    private readonly NumericUpDown _timeLimit = Number(0, 43_200);
    private readonly NumericUpDown _summaryInterval = Number(0, 1_440);
    private readonly CheckBox _stopOnFailure = new() { Text = "Stop on failure", Checked = true, AutoSize = true };
    private readonly CheckBox _stopOnDivergence = new() { Text = "Stop on branch divergence", Checked = true, AutoSize = true };
    private readonly CheckBox _pauseAfterTask = new() { Text = "Pause after current task", AutoSize = true };
    private readonly Label _validation = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly List<Button> _actionButtons = [];
    private WatcherProfileV1? _profile;

    public WorkflowEditorControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Workflow profile editor";
        PopulateChoices();
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(12), MaximumSize = new Size(1180, 0) };
        content.Controls.Add(BuildActions());
        content.Controls.Add(Group("Project", ("Profile name", _name), ("Description", _description), (string.Empty, _enabled)));
        content.Controls.Add(Group("Report Source", ("Source adapter", _reportAdapter), ("Expected repository", _repository), ("Expected branch", _branch)));
        content.Controls.Add(Group("Director Capture", ("Capture adapter", _directorAdapter), ("Conversation identity", _conversation), ("Authorization", ReadOnly("Exact parent + current path + authenticated message object"))));
        content.Controls.Add(Group("Destination Delivery", ("Delivery adapter", _destinationAdapter), ("Destination identity", _destination), ("Compatibility", ReadOnly("Private desktop adapters are Experimental"))));
        content.Controls.Add(Group("Authorization Policy", ("Policy", _policy), ("Human approval", ReadOnly("Manual visible approval is required by default"))));
        content.Controls.Add(Group("Guardrails", ("Maximum tasks", _taskLimit), ("Maximum elapsed minutes", _timeLimit), ("Summary interval minutes", _summaryInterval), (string.Empty, _stopOnFailure), (string.Empty, _stopOnDivergence), (string.Empty, _pauseAfterTask)));
        content.Controls.Add(Group("Advanced", ("Envelope capture", ReadOnly("Fallback body and whole-page capture are never actionable")), ("Replay", ReadOnly("Destination-bound durable suppression")), ("Profile validation", _validation)));
        scroll.Controls.Add(content);
        Controls.Add(scroll);
    }

    public event EventHandler? SaveRequested;
    public event EventHandler? CreateRequested;
    public event EventHandler? DuplicateRequested;
    public event EventHandler? ImportRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? ValidateRequested;

    public void LoadProfile(WatcherProfileV1 profile, bool readOnly = false, bool syntheticDemo = false)
    {
        _profile = profile;
        _name.Text = profile.Identity.Name;
        _description.Text = profile.Identity.Description;
        _enabled.Checked = profile.Enabled;
        SelectValue(_reportAdapter, profile.ReportSource.Adapter.AdapterId);
        _repository.Text = profile.ReportSource.ExpectedRepository;
        _branch.Text = profile.ReportSource.ExpectedBranch;
        SelectValue(_directorAdapter, profile.Director.Adapter.AdapterId);
        _conversation.Text = profile.Director.ConversationIdentity;
        SelectValue(_destinationAdapter, profile.Destination.Adapter.AdapterId);
        _destination.Text = profile.Destination.DestinationIdentity;
        SelectValue(_policy, profile.AutomationPolicy.Kind.ToString());
        _taskLimit.Value = Clamp(profile.Guardrails.MaximumTasksPerRun, _taskLimit);
        _timeLimit.Value = Clamp(profile.Guardrails.MaximumElapsedMinutes, _timeLimit);
        _summaryInterval.Value = Clamp(profile.Guardrails.SummaryIntervalMinutes, _summaryInterval);
        _stopOnFailure.Checked = profile.Guardrails.StopOnFailure;
        _stopOnDivergence.Checked = profile.Guardrails.StopOnBranchDivergence;
        _pauseAfterTask.Checked = profile.Guardrails.PauseAfterCurrentTask;
        if (syntheticDemo)
        {
            _repository.Text = "Synthetic fixture";
            _branch.Text = "Synthetic fixture";
            _conversation.Text = "Synthetic fixture";
            _destination.Text = "Test sink";
        }
        SetEditorEnabled(!readOnly);
    }

    public WatcherProfileV1 ApplyToProfile()
    {
        var profile = _profile ?? throw new InvalidOperationException("No profile is loaded.");
        profile.Identity.Name = _name.Text.Trim();
        profile.Identity.Description = _description.Text.Trim();
        profile.Enabled = _enabled.Checked;
        profile.ReportSource.Adapter.AdapterId = Selected(_reportAdapter);
        profile.ReportSource.ExpectedRepository = _repository.Text.Trim();
        profile.ReportSource.ExpectedBranch = _branch.Text.Trim();
        profile.Director.Adapter.AdapterId = Selected(_directorAdapter);
        profile.Director.ConversationIdentity = _conversation.Text.Trim();
        profile.Director.RequireDirectParent = true;
        profile.Director.RequireCurrentPath = true;
        profile.Director.RequireBackendMessageObject = true;
        profile.Director.AllowFallbackBody = false;
        profile.Director.AllowWholePageCapture = false;
        profile.Destination.Adapter.AdapterId = Selected(_destinationAdapter);
        profile.Destination.DestinationIdentity = _destination.Text.Trim();
        profile.AutomationPolicy.Kind = Enum.Parse<WatcherAutomationPolicyKind>(Selected(_policy));
        profile.AutomationPolicy.RequireVisibleHumanApproval = profile.AutomationPolicy.Kind == WatcherAutomationPolicyKind.ManualApproval;
        profile.Guardrails.MaximumTasksPerRun = (int)_taskLimit.Value;
        profile.Guardrails.MaximumElapsedMinutes = (int)_timeLimit.Value;
        profile.Guardrails.SummaryIntervalMinutes = (int)_summaryInterval.Value;
        profile.Guardrails.StopOnFailure = _stopOnFailure.Checked;
        profile.Guardrails.StopOnBranchDivergence = _stopOnDivergence.Checked;
        profile.Guardrails.PauseAfterCurrentTask = _pauseAfterTask.Checked;
        return profile;
    }

    public void ShowValidation(ProfileValidationResult result)
    {
        _validation.Text = result.IsValid ? "PASS - profile is structurally valid" : string.Join(" | ", result.Issues.Take(4).Select(issue => issue.Code));
        _validation.ForeColor = result.IsValid ? Color.DarkGreen : Color.DarkRed;
    }

    private Control BuildActions()
    {
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        AddButton(bar, "New", "Create workflow profile", "Creates a new editable workflow profile.", () => CreateRequested?.Invoke(this, EventArgs.Empty));
        AddButton(bar, "Duplicate", "Duplicate workflow profile", "Duplicates the selected workflow profile.", () => DuplicateRequested?.Invoke(this, EventArgs.Empty));
        AddButton(bar, "Import", "Import workflow profile", "Imports a workflow profile from a file.", () => ImportRequested?.Invoke(this, EventArgs.Empty));
        AddButton(bar, "Export", "Export workflow profile", "Exports the selected workflow profile to a file.", () => ExportRequested?.Invoke(this, EventArgs.Empty));
        AddButton(bar, "Validate", "Validate workflow profile", "Validates the selected workflow profile without starting it.", () => ValidateRequested?.Invoke(this, EventArgs.Empty));
        AddButton(bar, "Save profile", "Save workflow profile", "Saves changes to the selected workflow profile.", () => SaveRequested?.Invoke(this, EventArgs.Empty), true);
        return bar;
    }

    private static Control Group(string title, params (string Label, Control Control)[] rows)
    {
        var group = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 10) };
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(2) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var input = rows[i].Control;
            var accessibleLabel = string.IsNullOrWhiteSpace(rows[i].Label) ? input.Text : rows[i].Label;
            input.AccessibleName = accessibleLabel;
            input.AccessibleDescription = $"Workflow {accessibleLabel.ToLowerInvariant()} field.";
            var label = new Label
            {
                Text = string.IsNullOrWhiteSpace(rows[i].Label) ? string.Empty : "&" + rows[i].Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 8, 6),
                TabIndex = i * 2,
                UseMnemonic = true,
                AccessibleName = string.IsNullOrWhiteSpace(rows[i].Label) ? accessibleLabel : rows[i].Label + " label",
                AccessibleDescription = string.IsNullOrWhiteSpace(rows[i].Label) ? accessibleLabel : $"Labels the {accessibleLabel} field."
            };
            table.Controls.Add(label, 0, i);
            input.TabIndex = i * 2 + 1;
            input.Dock = input is CheckBox ? DockStyle.Left : DockStyle.Fill;
            input.Margin = new Padding(4, 4, 4, 6);
            table.Controls.Add(input, 1, i);
        }
        group.Controls.Add(table);
        return group;
    }

    private void PopulateChoices()
    {
        AddChoice(_reportAdapter, "Git remote", WatcherAdapterIds.ReportGitRemote);
        AddChoice(_reportAdapter, "GitHub", WatcherAdapterIds.ReportGitHub);
        AddChoice(_reportAdapter, "Local folder", WatcherAdapterIds.ReportLocalFolder);
        AddChoice(_reportAdapter, "GitHub + local fallback", WatcherAdapterIds.ReportGitHubLocalFallback);
        AddChoice(_reportAdapter, "Synthetic fixture", WatcherAdapterIds.ReportDemoFixture);
        AddChoice(_directorAdapter, "Authenticated ChatGPT current path (Experimental)", WatcherAdapterIds.DirectorChatGptEdgeCdp);
        AddChoice(_directorAdapter, "Manual envelope", WatcherAdapterIds.DirectorManualEnvelope);
        AddChoice(_directorAdapter, "Hash-bound file", WatcherAdapterIds.DirectorHashBoundFile);
        AddChoice(_directorAdapter, "Synthetic fixture", WatcherAdapterIds.DirectorDemoFixture);
        AddChoice(_destinationAdapter, "Verified Codex IPC (Experimental)", WatcherAdapterIds.DeliveryCodexVerifiedIpc);
        AddChoice(_destinationAdapter, "Hash-bound file", WatcherAdapterIds.DeliveryHashBoundFile);
        AddChoice(_destinationAdapter, "Manual visible paste", WatcherAdapterIds.DeliveryManualVisiblePaste);
        AddChoice(_destinationAdapter, "Test sink", WatcherAdapterIds.DeliveryTestSink);
        AddChoice(_destinationAdapter, "UI paste fallback (Advanced)", WatcherAdapterIds.DeliveryUiPasteFallback);
        foreach (var kind in Enum.GetValues<WatcherAutomationPolicyKind>()) AddChoice(_policy, PolicyLabel(kind), kind.ToString());
    }

    private void SetEditorEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _name, _description, _enabled, _reportAdapter, _repository, _branch, _directorAdapter, _conversation, _destinationAdapter, _destination, _policy, _taskLimit, _timeLimit, _summaryInterval, _stopOnFailure, _stopOnDivergence, _pauseAfterTask })
        {
            control.Enabled = enabled;
        }
        foreach (var button in _actionButtons) button.Enabled = enabled;
    }

    private void AddButton(Control parent, string text, string accessibleName, string accessibleDescription, Action action, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = primary ? FlatStyle.System : FlatStyle.Standard,
            AccessibleName = accessibleName,
            AccessibleDescription = accessibleDescription
        };
        button.Click += (_, _) => action();
        _actionButtons.Add(button);
        parent.Controls.Add(button);
    }

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, ThousandsSeparator = true };
    private static TextBox ReadOnly(string text) => new() { Text = text, ReadOnly = true, BackColor = SystemColors.Control, TabStop = false };
    private static decimal Clamp(int value, NumericUpDown input) => Math.Clamp(value, (int)input.Minimum, (int)input.Maximum);
    private static string Selected(ComboBox combo) => combo.SelectedItem is ChoiceItem item ? item.Value : string.Empty;
    private static void AddChoice(ComboBox combo, string label, string value) => combo.Items.Add(new ChoiceItem(label, value));
    private static void SelectValue(ComboBox combo, string value) => combo.SelectedItem = combo.Items.Cast<ChoiceItem>().FirstOrDefault(item => item.Value == value) ?? combo.Items.Cast<ChoiceItem>().First();
    private static string PolicyLabel(WatcherAutomationPolicyKind kind) => kind switch
    {
        WatcherAutomationPolicyKind.ManualApproval => "Manual",
        WatcherAutomationPolicyKind.PlannedAutomatic => "Planned autopilot",
        WatcherAutomationPolicyKind.SupervisedAutomatic => "Supervised checkpoints",
        WatcherAutomationPolicyKind.ContinuousAutomatic => "Continuous autopilot",
        _ => "Audit only"
    };

    private sealed record ChoiceItem(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}
