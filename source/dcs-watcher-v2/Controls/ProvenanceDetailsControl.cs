using DcsWatcherV2.Models;

namespace DcsWatcherV2.Controls;

public sealed class ProvenanceDetailsControl : UserControl
{
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly ListView _details = new();
    private readonly TextBox _artifacts = new();
    private int _logicalSplitterDistance = 330;
    private bool _applyingDpiMetrics;

    public ProvenanceDetailsControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Transaction evidence and provenance";
        _details.Dock = DockStyle.Fill;
        _details.View = View.Details;
        _details.FullRowSelect = true;
        _details.GridLines = true;
        _details.Columns.Add("Evidence", 230);
        _details.Columns.Add("Value", 720);
        _artifacts.Dock = DockStyle.Fill;
        _artifacts.Multiline = true;
        _artifacts.ReadOnly = true;
        _artifacts.ScrollBars = ScrollBars.Vertical;
        _artifacts.BackColor = SystemColors.Window;
        _artifacts.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _split.Panel1.Controls.Add(_details);
        _split.Panel2.Controls.Add(_artifacts);
        Controls.Add(_split);
        HandleCreated += (_, _) => ApplyDpiMetrics();
        SizeChanged += (_, _) => ApplyDpiMetrics();
        _split.SplitterMoved += (_, _) =>
        {
            if (!_applyingDpiMetrics && DeviceDpi > 0)
            {
                _logicalSplitterDistance = (int)Math.Round(_split.SplitterDistance * 96D / DeviceDpi);
            }
        };
        _details.SizeChanged += (_, _) => ResizeColumns();
    }

    public void ShowAudit(AppState state, string profileName, string profileHash, string artifacts)
    {
        _details.Items.Clear();
        var audit = state.TransactionAudit;
        Add("Profile", profileName);
        Add("Profile SHA-256", profileHash);
        Add("Operating stage", state.OperatingStage);
        Add("Conversation", audit.ConversationId);
        Add("Wake message", audit.WakeMessageId);
        Add("Assistant response", audit.ResponseMessageId);
        Add("Response parent", audit.ResponseParentId);
        Add("Current node", audit.CurrentNode);
        Add("Current path", audit.OnCurrentPath?.ToString());
        Add("Capture method", audit.CaptureMethod);
        Add("Fallback body", audit.FallbackBody?.ToString());
        Add("Backend verified", audit.ApiVerification?.ToString());
        Add("Task ID", audit.EnvelopeTaskId);
        Add("Envelope SHA-256", audit.EnvelopeSha256);
        Add("Authorization", audit.EligibilityResult);
        Add("Replay disposition", state.LastCodexSendResult);
        _artifacts.Text = string.IsNullOrWhiteSpace(artifacts) ? "No saved artifacts for the selected transaction." : artifacts;
    }

    private void Add(string name, string? value)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add(string.IsNullOrWhiteSpace(value) ? "Not available" : value);
        _details.Items.Add(item);
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
            _split.Panel1MinSize = ScaleLogical(140);
            _split.Panel2MinSize = ScaleLogical(120);
            var maximum = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
            if (maximum >= _split.Panel1MinSize)
            {
                _split.SplitterDistance = Math.Clamp(ScaleLogical(_logicalSplitterDistance), _split.Panel1MinSize, maximum);
            }
            ResizeColumns();
        }
        finally
        {
            _applyingDpiMetrics = false;
        }
    }

    private void ResizeColumns()
    {
        if (_details.Columns.Count != 2 || _details.ClientSize.Width <= 0) return;
        var available = Math.Max(360, _details.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
        var labelWidth = Math.Max(150, (int)Math.Round(available * 0.28));
        _details.Columns[0].Width = labelWidth;
        _details.Columns[1].Width = Math.Max(200, available - labelWidth);
    }

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96D);
}
