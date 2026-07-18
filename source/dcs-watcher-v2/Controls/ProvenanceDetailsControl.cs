using DcsWatcherV2.Models;

namespace DcsWatcherV2.Controls;

public sealed class ProvenanceDetailsControl : UserControl
{
    private readonly ListView _details = new();
    private readonly TextBox _artifacts = new();

    public ProvenanceDetailsControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "Transaction evidence and provenance";
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, SplitterWidth = 6 };
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
        _artifacts.Font = new Font(FontFamily.GenericMonospace, 9F);
        split.Panel1.Controls.Add(_details);
        split.Panel2.Controls.Add(_artifacts);
        Controls.Add(split);
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
}
