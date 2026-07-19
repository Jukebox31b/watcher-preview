namespace DcsWatcherV2.Controls;

public sealed class DiagnosticsControl : UserControl
{
    private readonly SplitContainer _split = new();
    private readonly ListView _health = new();
    private readonly TextBox _log = new();
    private readonly Label _experimentalNotice = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Color.FromArgb(180, 83, 9),
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Experimental adapters: Edge CDP and private Codex IPC.",
        AccessibleName = "Experimental adapter notice",
        AccessibleDescription = "Edge CDP and private Codex IPC adapters are experimental and unsupported in the isolated Preview."
    };
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
    private int _logicalSplitterDistance = 180;
    private bool _applyingDpiMetrics;

    public DiagnosticsControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Diagnostics";

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.SplitterWidth = 6;
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 120;
        _split.AccessibleName = "Diagnostic details splitter";
        _split.AccessibleDescription = "Resize the component summary above and diagnostic log below.";

        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10, 8, 10, 4)
        };
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        _health.Dock = DockStyle.Fill;
        _health.View = View.Details;
        _health.FullRowSelect = true;
        _health.HideSelection = false;
        _health.MultiSelect = false;
        _health.AccessibleName = "Component health list";
        _health.AccessibleDescription = "Lists each component with its current status and diagnostic notes.";
        _health.Columns.Add("Component", 180);
        _health.Columns.Add("Status", 120);
        _health.Columns.Add("Notes", 520);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 6, 0, 4),
            Margin = Padding.Empty,
            AccessibleName = "Diagnostic actions",
            AccessibleDescription = "Contains local diagnostic actions."
        };
        bar.Controls.Add(_runTests);
        bar.Controls.Add(_export);

        _log.Dock = DockStyle.Fill;
        _log.Margin = new Padding(10, 4, 10, 10);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.WordWrap = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = Color.FromArgb(27, 30, 34);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _log.AccessibleName = "Diagnostic log";
        _log.AccessibleDescription = "Read-only wrapped log output from local diagnostic checks.";

        summary.Controls.Add(_health, 0, 0);
        summary.Controls.Add(bar, 0, 1);
        summary.Controls.Add(_experimentalNotice, 0, 2);
        _split.Panel1.Controls.Add(summary);
        _split.Panel2.Controls.Add(_log);
        Controls.Add(_split);

        HandleCreated += (_, _) => ApplyDpiMetrics();
        _health.SizeChanged += (_, _) => ResizeHealthColumns();
        SizeChanged += (_, _) => ApplyDpiMetrics();
        _split.SplitterMoved += (_, _) =>
        {
            if (!_applyingDpiMetrics && DeviceDpi > 0)
            {
                _logicalSplitterDistance = (int)Math.Round(_split.SplitterDistance * 96D / DeviceDpi);
            }
        };

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
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ResizeHealthColumns()
    {
        if (_health.Columns.Count != 3 || _health.ClientSize.Width <= 0) return;

        var available = Math.Max(360, _health.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
        var component = Math.Max(120, (int)Math.Round(available * 0.22));
        var status = Math.Max(90, (int)Math.Round(available * 0.16));
        _health.Columns[0].Width = component;
        _health.Columns[1].Width = status;
        _health.Columns[2].Width = Math.Max(150, available - component - status);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
    }

    private void ApplyDpiMetrics()
    {
        if (!IsHandleCreated || _split.Height <= 0) return;

        _applyingDpiMetrics = true;
        try
        {
            _split.SplitterWidth = ScaleLogical(6);
            _split.Panel1MinSize = ScaleLogical(120);
            _split.Panel2MinSize = ScaleLogical(120);
            var noticeRow = ((TableLayoutPanel)_experimentalNotice.Parent!).RowStyles[2];
            noticeRow.Height = ScaleLogical(30);
            var maximum = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
            if (maximum >= _split.Panel1MinSize)
            {
                _split.SplitterDistance = Math.Clamp(ScaleLogical(_logicalSplitterDistance), _split.Panel1MinSize, maximum);
            }
            ResizeHealthColumns();
        }
        finally
        {
            _applyingDpiMetrics = false;
        }
    }

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96D);
}
