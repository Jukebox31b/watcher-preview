using DcsWatcherV2.Models;

namespace DcsWatcherV2.Controls;

public sealed class OverviewControl : UserControl
{
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
    private readonly Label _state = ValueLabel();
    private readonly Label _profile = ValueLabel();
    private readonly Label _pipeline = ValueLabel("Transaction pipeline");
    private readonly Label _warning = ValueLabel();
    private readonly Label _lastSuccess = ValueLabel();
    private readonly Label _nextPoll = ValueLabel();
    private readonly DataGridView _activity = CreateGrid();
    private readonly Button _collapseSummary = new()
    {
        Text = "Hide summary",
        AutoSize = true,
        AccessibleName = "Toggle overview summary",
        AccessibleDescription = "Hides the overview summary section."
    };
    private readonly Button _collapseActivity = new()
    {
        Text = "Hide activity",
        AutoSize = true,
        AccessibleName = "Toggle recent activity",
        AccessibleDescription = "Hides the recent activity section."
    };

    public OverviewControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Watcher overview";
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 120;
        _split.Panel1.Controls.Add(BuildSummary());
        _split.Panel2.Controls.Add(BuildActivity());
        Controls.Add(_split);
        _split.SplitterMoved += (_, _) => SplitterDistanceChanged?.Invoke(this, EventArgs.Empty);
        _collapseSummary.Click += (_, _) =>
        {
            _split.Panel1Collapsed = !_split.Panel1Collapsed;
            _collapseSummary.Text = _split.Panel1Collapsed ? "Show summary" : "Hide summary";
            _collapseSummary.AccessibleDescription = _split.Panel1Collapsed
                ? "Shows the overview summary section."
                : "Hides the overview summary section.";
        };
        _collapseActivity.Click += (_, _) =>
        {
            _split.Panel2Collapsed = !_split.Panel2Collapsed;
            _collapseActivity.Text = _split.Panel2Collapsed ? "Show activity" : "Hide activity";
            _collapseActivity.AccessibleDescription = _split.Panel2Collapsed
                ? "Shows the recent activity section."
                : "Hides the recent activity section.";
        };
    }

    public int SplitterDistance
    {
        get => _split.SplitterDistance;
        set
        {
            if (value > _split.Panel1MinSize && value < Height - _split.Panel2MinSize)
            {
                _split.SplitterDistance = value;
            }
        }
    }

    public event EventHandler? SplitterDistanceChanged;

    public void UpdateSummary(string state, string profile, string pipeline, string pipelineDescription, string warning, string lastSuccess, string nextPoll)
    {
        _state.Text = state;
        _profile.Text = profile;
        _pipeline.Text = pipeline;
        _pipeline.AccessibleDescription = pipelineDescription;
        _warning.Text = string.IsNullOrWhiteSpace(warning) ? "No active warnings" : warning;
        _warning.ForeColor = string.IsNullOrWhiteSpace(warning) ? Color.DarkGreen : Color.DarkRed;
        _lastSuccess.Text = lastSuccess;
        _nextPoll.Text = nextPoll;
    }

    public void SetActivity(IReadOnlyList<ActivityTimelineItem> items)
    {
        _activity.Rows.Clear();
        foreach (var item in items.TakeLast(20).Reverse())
        {
            _activity.Rows.Add(item.Timestamp.ToLocalTime().ToString("HH:mm:ss"), item.Stage, item.Title, item.Result);
        }
    }

    private Control BuildSummary()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, 0, "Operating state", _state);
        AddRow(root, 1, "Profile", _profile);
        AddRow(root, 2, "Transaction pipeline", _pipeline);
        AddRow(root, 3, "Warnings", _warning);
        AddRow(root, 4, "Last success", _lastSuccess);
        AddRow(root, 5, "Next poll", _nextPoll);
        root.Controls.Add(_collapseSummary, 1, 6);
        return root;
    }

    private Control BuildActivity()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 6, 10, 10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        bar.Controls.Add(new Label { Text = "Recent activity", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold), Margin = new Padding(4, 8, 18, 4) });
        bar.Controls.Add(_collapseActivity);
        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_activity, 0, 1);
        return root;
    }

    private static void AddRow(TableLayoutPanel panel, int row, string name, Control value)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 14.28F));
        panel.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.DimGray }, 0, row);
        value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(value, 1, row);
    }

    private static Label ValueLabel(string? accessibleName = null) => new() { AccessibleName = accessibleName, AutoEllipsis = true, AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleLeft };

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Recent activity grid",
            AccessibleDescription = "Read-only transaction activity showing time, stage, event, and result."
        };
        grid.Columns.Add("Time", "Time");
        grid.Columns.Add("Stage", "Stage");
        grid.Columns.Add("Event", "Event");
        grid.Columns.Add("Result", "Result");
        grid.Columns[0].FillWeight = 18;
        grid.Columns[1].FillWeight = 24;
        grid.Columns[2].FillWeight = 40;
        grid.Columns[3].FillWeight = 18;
        return grid;
    }
}
