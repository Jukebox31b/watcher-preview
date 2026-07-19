using DcsWatcherV2.Models;

namespace DcsWatcherV2.Controls;

public sealed class ActivityTimelineControl : UserControl
{
    private readonly ComboBox _stageFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox _search = new() { Width = 240, PlaceholderText = "Filter activity" };
    private readonly DataGridView _grid = new();
    private readonly FlowLayoutPanel _bar = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Padding = new Padding(8, 7, 8, 4)
    };
    private IReadOnlyList<ActivityTimelineItem> _items = [];

    public ActivityTimelineControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Transaction activity timeline";
        _stageFilter.Items.AddRange(["All stages", "Source", "Lineage", "Authorization", "Destination", "System"]);
        _stageFilter.SelectedIndex = 0;
        _stageFilter.SelectedIndexChanged += (_, _) => RefreshRows();
        _search.TextChanged += (_, _) => RefreshRows();

        _bar.Controls.Add(new Label { Text = "Stage", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _bar.Controls.Add(_stageFilter);
        _bar.Controls.Add(_search);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add("Time", "Time");
        _grid.Columns.Add("Stage", "Stage");
        _grid.Columns.Add("Title", "Event");
        _grid.Columns.Add("Detail", "Detail");
        _grid.Columns.Add("Result", "Result");
        _grid.Columns[0].FillWeight = 16;
        _grid.Columns[1].FillWeight = 18;
        _grid.Columns[2].FillWeight = 24;
        _grid.Columns[3].FillWeight = 52;
        _grid.Columns[4].FillWeight = 16;

        Controls.Add(_grid);
        Controls.Add(_bar);
        HandleCreated += (_, _) => ApplyDpiMetrics();
    }

    public void SetItems(IReadOnlyList<ActivityTimelineItem> items)
    {
        _items = items;
        RefreshRows();
    }

    private void RefreshRows()
    {
        var stage = _stageFilter.SelectedItem?.ToString() ?? "All stages";
        var query = _search.Text.Trim();
        var filtered = _items.Where(item =>
            (stage == "All stages" || item.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || (item.Title + " " + item.Detail + " " + item.Result).Contains(query, StringComparison.OrdinalIgnoreCase)));
        _grid.Rows.Clear();
        foreach (var item in filtered.OrderByDescending(item => item.Timestamp))
        {
            _grid.Rows.Add(item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Stage, item.Title, item.Detail, item.Result);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
    }

    private void ApplyDpiMetrics()
    {
        if (!IsHandleCreated) return;
        _stageFilter.Width = ScaleLogical(150);
        _search.Width = ScaleLogical(240);
    }

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96D);
}
