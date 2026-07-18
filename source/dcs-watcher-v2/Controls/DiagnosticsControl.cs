namespace DcsWatcherV2.Controls;

public sealed class DiagnosticsControl : UserControl
{
    private readonly ListView _health = new();
    private readonly TextBox _log = new();
    private readonly Button _runTests = new()
    {
        Text = "Run offline checks",
        AutoSize = true,
        AccessibleName = "Run offline diagnostic checks",
        AccessibleDescription = "Runs the local offline safety and diagnostic checks."
    };
    private readonly Button _export = new()
    {
        Text = "Export diagnostic bundle",
        AutoSize = true,
        AccessibleName = "Export diagnostic bundle",
        AccessibleDescription = "Requests a diagnostic bundle export without starting live automation."
    };

    public DiagnosticsControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Diagnostics";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _health.Dock = DockStyle.Fill;
        _health.View = View.Details;
        _health.FullRowSelect = true;
        _health.AccessibleName = "Component health list";
        _health.AccessibleDescription = "Lists each component with its current status and diagnostic notes.";
        _health.Columns.Add("Component", 240);
        _health.Columns.Add("Status", 170);
        _health.Columns.Add("Notes", 620);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 8) };
        bar.Controls.Add(_runTests);
        bar.Controls.Add(_export);
        bar.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkOrange,
            Margin = new Padding(18, 7, 0, 0),
            Text = "Edge CDP and private Codex IPC adapters are Experimental."
        });

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.ScrollBars = ScrollBars.Both;
        _log.BackColor = Color.FromArgb(27, 30, 34);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font(FontFamily.GenericMonospace, 9F);
        _log.AccessibleName = "Diagnostic log";
        _log.AccessibleDescription = "Read-only log output from local diagnostic checks.";

        root.Controls.Add(_health, 0, 0);
        root.Controls.Add(bar, 0, 1);
        root.Controls.Add(_log, 0, 2);
        Controls.Add(root);

        _runTests.Click += (_, _) => RunOfflineTestsRequested?.Invoke(this, EventArgs.Empty);
        _export.Click += (_, _) => ExportBundleRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? RunOfflineTestsRequested;
    public event EventHandler? ExportBundleRequested;

    public void SetHealth(IEnumerable<(string Component, string Status, string Notes)> rows)
    {
        _health.Items.Clear();
        foreach (var row in rows)
        {
            var item = new ListViewItem(row.Component);
            item.SubItems.Add(row.Status);
            item.SubItems.Add(row.Notes);
            _health.Items.Add(item);
        }
    }

    public void AppendLog(string text)
    {
        if (_log.TextLength > 200_000) _log.Clear();
        _log.AppendText(text + Environment.NewLine);
    }
}
