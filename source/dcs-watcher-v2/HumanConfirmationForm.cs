using DcsWatcherV2.Models;

namespace DcsWatcherV2;

public sealed class HumanConfirmationForm : Form
{
    private readonly CheckBox _approval = new()
    {
        AutoSize = true,
        Text = "I confirm this exact prepared action, report, destination, policy, and prompt."
    };
    private readonly Button _confirm = new()
    {
        Text = "Confirm",
        AutoSize = true,
        Enabled = false,
        DialogResult = DialogResult.OK
    };

    public HumanConfirmationForm(PreparedHumanAction prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Text = "Confirm prepared human action";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 700);
        MinimumSize = new Size(700, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;
        ShowInTaskbar = false;

        var details = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 10, 12, 4)
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddDetail(details, "Action", prepared.Action);
        AddDetail(details, "Report path", prepared.Report.RelativePath);
        AddDetail(details, "Resolved source", prepared.Report.FullPath);
        AddDetail(details, "Report SHA-256", prepared.Report.Fingerprint);
        AddDetail(details, "Prompt SHA-256", prepared.PromptSha256);
        AddDetail(details, "Wake token", prepared.WakeToken);
        AddDetail(details, "Profile", prepared.ProfileId);
        AddDetail(details, "Director", prepared.DirectorIdentity);
        AddDetail(details, "Destination", prepared.DestinationDisplay);
        AddDetail(details, "Policy", prepared.PolicyDisplay);
        AddDetail(details, "Issued (UTC)", prepared.IssuedAtUtc.ToString("O"));
        AddDetail(details, "Expires (UTC)", prepared.ExpiresAtUtc.ToString("O"));
        AddDetail(details, "Nonce", prepared.Nonce);

        var promptLabel = new Label
        {
            Text = "Full prompt",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 7, 12, 5),
            Font = new Font(Font, FontStyle.Bold)
        };
        var prompt = new TextBox
        {
            Text = prepared.Prompt,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9F)
        };

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(prompt);
        content.Controls.Add(promptLabel);
        content.Controls.Add(details);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 78,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 9, 12, 8),
            WrapContents = false
        };
        buttons.Controls.Add(_confirm);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_approval);

        Controls.Add(content);
        Controls.Add(buttons);
        AcceptButton = _confirm;
        CancelButton = cancel;
        _approval.CheckedChanged += (_, _) => _confirm.Enabled = _approval.Checked;
    }

    internal bool ExplicitApprovalChecked => _approval.Checked;
    internal bool ConfirmEnabled => _confirm.Enabled;
    internal void SetExplicitApprovalForSelfTest(bool value) => _approval.Checked = value;

    private static void AddDetail(TableLayoutPanel table, string name, string value)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.DimGray
        }, 0, row);
        table.Controls.Add(new TextBox
        {
            Text = value,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 5, 3, 3)
        }, 1, row);
    }
}
