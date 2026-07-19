using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2;

public sealed class FirstRunSetupForm : Form
{
    private sealed record ChoiceOption(string Name, string Description)
    {
        public override string ToString() => Name;
    }

    private static readonly ChoiceOption[] ReportChoices =
    [
        new("Demo fixture", "Uses built-in synthetic reports. It never contacts GitHub, reads project files, or starts live automation."),
        new("Git remote", "Reads reports from an authenticated local Git checkout with git fetch and git show. Recommended for private repositories."),
        new("GitHub", "Reads reports through GitHub. Best for public repositories or environments with configured GitHub access."),
        new("Local folder", "Watches a folder on this computer. Use when another tool writes reports directly to disk.")
    ];

    private static readonly ChoiceOption[] DirectorChoices =
    [
        new("Demo fixture", "Uses a synthetic assistant response for safe setup and demonstrations. No ChatGPT conversation is contacted."),
        new("Authenticated current-path message (Experimental)", "Captures one identified ChatGPT assistant message and verifies exact parentage, ancestry, and the current visible conversation path."),
        new("Manual envelope", "Waits for you to provide the complete instruction envelope. Watcher does not capture from a browser."),
        new("Hash-bound file", "Reads an instruction file only when its path, byte size, and SHA-256 match the approved values.")
    ];

    private static readonly ChoiceOption[] DestinationChoices =
    [
        new("Test sink", "Validates and records the transaction without sending anything to Codex. This is the safest setup and demonstration destination."),
        new("Verified Codex IPC (Experimental)", "Sends a provenance-authenticated transaction to one configured Codex thread after all verification gates pass."),
        new("Hash-bound file", "Writes the verified payload to a local integrity-bound file for a later, separately authorized handoff."),
        new("Manual visible paste", "Keeps delivery human-visible: you review the instruction and paste it into Codex yourself.")
    ];

    private static readonly ChoiceOption[] PolicyChoices =
    [
        new("Manual approval", "Requires visible human approval before every actionable exchange. Recommended while configuring a workflow."),
        new("Planned autopilot", "Runs a bounded plan automatically only after a separate signed grant, and stops at configured task or time limits."),
        new("Supervised checkpoints", "Allows bounded automatic progress between scheduled human review checkpoints and summary reports."),
        new("Audit only", "Observes and records activity but never wakes a Director, captures an instruction, or delivers a task.")
    ];

    private readonly ProfileService _profiles;
    private readonly TabControl _pages = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    private readonly Button _back = new() { Text = "Back", AutoSize = true };
    private readonly Button _next = new() { Text = "Next", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly FlowLayoutPanel _buttonBar = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false,
        AccessibleName = "Setup navigation"
    };
    private readonly List<TableLayoutPanel> _pageLayouts = [];
    private readonly TextBox _name = new() { Text = "My Watcher workflow" };
    private readonly ComboBox _report = Choice();
    private readonly ComboBox _director = Choice();
    private readonly ComboBox _destination = Choice();
    private readonly ComboBox _policy = Choice();
    private readonly Label _validation = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private bool _initialWindowBoundsApplied;

    public FirstRunSetupForm(ProfileService profiles)
    {
        _profiles = profiles;
        Text = "Set up DCS Watcher v2 Preview";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 560);
        ClientSize = new Size(760, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        ShowIcon = false;

        AddChoices(_report, ReportChoices);
        AddChoices(_director, DirectorChoices);
        AddChoices(_destination, DestinationChoices);
        AddChoices(_policy, PolicyChoices);
        _report.SelectedIndex = 0;
        _director.SelectedIndex = 0;
        _destination.SelectedIndex = 0;
        _policy.SelectedIndex = 0;

        _pages.TabPages.Add(Page("Name your workflow", "Profiles keep source, authorization, delivery, and audit settings independent.", Field("Profile name", _name)));
        _pages.TabPages.Add(Page("Choose a report source", "Git remote is recommended for authenticated private repositories. Demo fixtures never contact a live service.", Field("Report source", _report, ReportChoices)));
        _pages.TabPages.Add(Page("Choose Director capture", "Actionable ChatGPT capture always requires one authenticated current-path message with exact parentage.", Field("Capture method", _director, DirectorChoices)));
        _pages.TabPages.Add(Page("Choose a destination", "The test sink is non-actionable and is the safe default for setup and demonstrations.", Field("Delivery method", _destination, DestinationChoices)));
        _pages.TabPages.Add(Page("Choose authorization", "Manual approval is the default. Automatic policies require a separately signed, bounded grant.", Field("Policy", _policy, PolicyChoices)));
        _pages.TabPages.Add(Page("Review", "New profiles remain disabled until you review and explicitly enable them.", _validation));

        _buttonBar.Controls.Add(_next);
        _buttonBar.Controls.Add(_back);
        _buttonBar.Controls.Add(_cancel);
        Controls.Add(_pages);
        Controls.Add(_buttonBar);
        AcceptButton = _next;
        CancelButton = _cancel;
        _back.Click += (_, _) => MovePage(-1);
        _next.Click += (_, _) => MovePage(1);
        _pages.SelectedIndexChanged += (_, _) => UpdateButtons();
        Load += (_, _) => ApplyDpiAwareLayout(applyInitialWindowBounds: true);
        DpiChanged += (_, _) => BeginInvoke(() => ApplyDpiAwareLayout(applyInitialWindowBounds: false));
        UpdateButtons();
    }

    public WatcherProfileV1? CreatedProfile { get; private set; }

    private void MovePage(int direction)
    {
        if (direction < 0)
        {
            _pages.SelectedIndex = Math.Max(0, _pages.SelectedIndex - 1);
            return;
        }
        if (_pages.SelectedIndex < _pages.TabCount - 1)
        {
            _pages.SelectedIndex++;
            if (_pages.SelectedIndex == _pages.TabCount - 1) PreviewValidation();
            return;
        }

        var profile = BuildProfile();
        var validation = new ProfileValidator().Validate(profile);
        if (!validation.IsValid)
        {
            _validation.Text = "Setup is blocked:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Issues.Select(issue => "- " + issue.Message));
            _validation.ForeColor = Color.DarkRed;
            return;
        }
        _profiles.Save(profile);
        CreatedProfile = profile;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PreviewValidation()
    {
        var profile = BuildProfile();
        var validation = new ProfileValidator().Validate(profile);
        _validation.Text = validation.IsValid
            ? $"Ready to create '{profile.Identity.Name}'.\n\nThe profile will be disabled. Source: {_report.SelectedItem}. Capture: {_director.SelectedItem}. Destination: {_destination.SelectedItem}. Policy: {_policy.SelectedItem}."
            : "Setup is blocked:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Issues.Select(issue => "- " + issue.Message));
        _validation.ForeColor = validation.IsValid ? Color.DarkGreen : Color.DarkRed;
    }

    private WatcherProfileV1 BuildProfile()
    {
        var profile = _profiles.CreateFresh(_name.Text);
        profile.Enabled = false;
        profile.ReportSource.Adapter.AdapterId = _report.SelectedIndex switch
        {
            1 => WatcherAdapterIds.ReportGitRemote,
            2 => WatcherAdapterIds.ReportGitHub,
            3 => WatcherAdapterIds.ReportLocalFolder,
            _ => WatcherAdapterIds.ReportDemoFixture
        };
        profile.Director.Adapter.AdapterId = _director.SelectedIndex switch
        {
            1 => WatcherAdapterIds.DirectorChatGptEdgeCdp,
            2 => WatcherAdapterIds.DirectorManualEnvelope,
            3 => WatcherAdapterIds.DirectorHashBoundFile,
            _ => WatcherAdapterIds.DirectorDemoFixture
        };
        profile.Destination.Adapter.AdapterId = _destination.SelectedIndex switch
        {
            1 => WatcherAdapterIds.DeliveryCodexVerifiedIpc,
            2 => WatcherAdapterIds.DeliveryHashBoundFile,
            3 => WatcherAdapterIds.DeliveryManualVisiblePaste,
            _ => WatcherAdapterIds.DeliveryTestSink
        };
        profile.AutomationPolicy.Kind = _policy.SelectedIndex switch
        {
            1 => WatcherAutomationPolicyKind.PlannedAutomatic,
            2 => WatcherAutomationPolicyKind.SupervisedAutomatic,
            3 => WatcherAutomationPolicyKind.AuditOnly,
            _ => WatcherAutomationPolicyKind.ManualApproval
        };
        profile.AutomationPolicy.RequireVisibleHumanApproval = profile.AutomationPolicy.Kind == WatcherAutomationPolicyKind.ManualApproval;
        if (profile.AutomationPolicy.Kind.IsAutomatic())
        {
            profile.Guardrails.MaximumTasksPerRun = 3;
            profile.Guardrails.MaximumElapsedMinutes = 60;
            profile.Guardrails.SummaryIntervalMinutes = 15;
        }
        return profile;
    }

    private void UpdateButtons()
    {
        _back.Enabled = _pages.SelectedIndex > 0;
        _next.Text = _pages.SelectedIndex == _pages.TabCount - 1 ? "Create disabled profile" : "Next";
    }

    internal void SelectPageForSelfTest(int index)
    {
        _pages.SelectedIndex = Math.Clamp(index, 0, _pages.TabCount - 1);
        UpdateButtons();
        PerformLayout();
    }

    private TabPage Page(string title, string description, Control content)
    {
        var page = new TabPage();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(32) };
        _pageLayouts.Add(table);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titleLabel = new Label { Text = title, AutoSize = true, Font = new Font((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
        var descriptionLabel = new Label { Text = description, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 28) };
        table.Controls.Add(titleLabel);
        table.Controls.Add(descriptionLabel);
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.Controls.Add(content);
        table.SizeChanged += (_, _) =>
        {
            var width = Math.Max(0, table.ClientSize.Width - table.Padding.Horizontal);
            descriptionLabel.MaximumSize = new Size(width, 0);
            if (content is Label contentLabel) contentLabel.MaximumSize = new Size(width, 0);
        };
        page.Controls.Add(table);
        return page;
    }

    private static Control Field(string label, Control input)
    {
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top };
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        input.Dock = DockStyle.Top;
        table.Controls.Add(input);
        return table;
    }

    private static Control Field(string label, ComboBox input, IReadOnlyList<ChoiceOption> choices)
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Top
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        input.Dock = DockStyle.Top;
        input.AccessibleName = label;
        table.Controls.Add(input, 0, 1);

        var explanation = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 12, 0, 0),
            AccessibleName = $"{label} option explanation"
        };
        table.Controls.Add(explanation, 0, 2);
        table.SizeChanged += (_, _) => explanation.MaximumSize = new Size(Math.Max(0, table.ClientSize.Width), 0);

        void UpdateExplanation()
        {
            var selected = input.SelectedIndex;
            var description = selected >= 0 && selected < choices.Count
                ? choices[selected].Description
                : "Select an option to see what it does.";
            explanation.Text = "What this option does: " + description;
            explanation.AccessibleDescription = description;
            input.AccessibleDescription = description;
        }

        input.SelectedIndexChanged += (_, _) => UpdateExplanation();
        UpdateExplanation();
        return table;
    }

    private static void AddChoices(ComboBox comboBox, IEnumerable<ChoiceOption> choices) =>
        comboBox.Items.AddRange(choices.Cast<object>().ToArray());

    private void ApplyDpiAwareLayout(bool applyInitialWindowBounds)
    {
        _buttonBar.Padding = new Padding(LogicalToDeviceUnits(12));
        foreach (var pageLayout in _pageLayouts)
        {
            pageLayout.Padding = new Padding(LogicalToDeviceUnits(32));
        }

        var workingArea = Screen.FromControl(this).WorkingArea;
        var minimumWidth = Math.Min(LogicalToDeviceUnits(700), workingArea.Width);
        var minimumHeight = Math.Min(LogicalToDeviceUnits(560), workingArea.Height);
        MinimumSize = new Size(minimumWidth, minimumHeight);

        if (applyInitialWindowBounds && !_initialWindowBoundsApplied)
        {
            _initialWindowBoundsApplied = true;
            var maximumWidth = Math.Max(minimumWidth, (int)Math.Round(workingArea.Width * 0.92));
            var maximumHeight = Math.Max(minimumHeight, (int)Math.Round(workingArea.Height * 0.90));
            Size = new Size(
                Math.Min(LogicalToDeviceUnits(760), maximumWidth),
                Math.Min(LogicalToDeviceUnits(620), maximumHeight));
            CenterToParent();
        }

        PerformLayout();
    }

    internal void ApplyLayoutForSelfTest() => ApplyDpiAwareLayout(applyInitialWindowBounds: false);

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
}
