using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2;

public sealed class FirstRunSetupForm : Form
{
    private readonly ProfileService _profiles;
    private readonly TabControl _pages = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    private readonly Button _back = new() { Text = "Back", AutoSize = true };
    private readonly Button _next = new() { Text = "Next", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly TextBox _name = new() { Text = "My Watcher workflow" };
    private readonly ComboBox _report = Choice();
    private readonly ComboBox _director = Choice();
    private readonly ComboBox _destination = Choice();
    private readonly ComboBox _policy = Choice();
    private readonly Label _validation = new() { AutoSize = true, MaximumSize = new Size(650, 0) };

    public FirstRunSetupForm(ProfileService profiles)
    {
        _profiles = profiles;
        Text = "Set up DCS Watcher v2 Preview";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 520);
        ClientSize = new Size(760, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;

        _report.Items.AddRange(["Demo fixture", "Git remote", "GitHub", "Local folder"]);
        _director.Items.AddRange(["Demo fixture", "Authenticated current-path message (Experimental)", "Manual envelope", "Hash-bound file"]);
        _destination.Items.AddRange(["Test sink", "Verified Codex IPC (Experimental)", "Hash-bound file", "Manual visible paste"]);
        _policy.Items.AddRange(["Manual approval", "Planned autopilot", "Supervised checkpoints", "Audit only"]);
        _report.SelectedIndex = 0;
        _director.SelectedIndex = 0;
        _destination.SelectedIndex = 0;
        _policy.SelectedIndex = 0;

        _pages.TabPages.Add(Page("Name your workflow", "Profiles keep source, authorization, delivery, and audit settings independent.", Field("Profile name", _name)));
        _pages.TabPages.Add(Page("Choose a report source", "Git remote is recommended for authenticated private repositories. Demo fixtures never contact a live service.", Field("Report source", _report)));
        _pages.TabPages.Add(Page("Choose Director capture", "Actionable ChatGPT capture always requires one authenticated current-path message with exact parentage.", Field("Capture method", _director)));
        _pages.TabPages.Add(Page("Choose a destination", "The test sink is non-actionable and is the safe default for setup and demonstrations.", Field("Delivery method", _destination)));
        _pages.TabPages.Add(Page("Choose authorization", "Manual approval is the default. Automatic policies require a separately signed, bounded grant.", Field("Policy", _policy)));
        _pages.TabPages.Add(Page("Review", "New profiles remain disabled until you review and explicitly enable them.", _validation));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 8), WrapContents = false };
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);
        buttons.Controls.Add(_cancel);
        Controls.Add(_pages);
        Controls.Add(buttons);
        AcceptButton = _next;
        CancelButton = _cancel;
        _back.Click += (_, _) => MovePage(-1);
        _next.Click += (_, _) => MovePage(1);
        _pages.SelectedIndexChanged += (_, _) => UpdateButtons();
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

    private static TabPage Page(string title, string description, Control content)
    {
        var page = new TabPage();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(32) };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
        table.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 28) });
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.Controls.Add(content);
        page.Controls.Add(table);
        return page;
    }

    private static Control Field(string label, Control input)
    {
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top };
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        input.Width = 560;
        table.Controls.Add(input);
        return table;
    }

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
}
