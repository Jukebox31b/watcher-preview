#nullable enable

namespace DcsWatcherV2;

partial class MainForm
{
    private System.ComponentModel.IContainer? components;
    private ToolStrip commandBar = null!;
    private ToolStripLabel profileLabel = null!;
    private ToolStripComboBox profileSelector = null!;
    private ToolStripSeparator profileSeparator = null!;
    private ToolStripLabel operatingStateLabel = null!;
    private ToolStripSeparator stateSeparator = null!;
    private ToolStripButton startButton = null!;
    private ToolStripButton stopButton = null!;
    private ToolStripButton runOnceButton = null!;
    private ToolStripButton emergencyPauseButton = null!;
    private ToolStripDropDownButton moreButton = null!;
    private TabControl mainTabs = null!;
    private StatusStrip statusBar = null!;
    private ToolStripStatusLabel statusSummaryLabel = null!;
    private ToolStripStatusLabel policyStatusLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        commandBar = new ToolStrip();
        profileLabel = new ToolStripLabel("Profile") { AccessibleName = "Profile", AccessibleDescription = "Labels the active workflow profile selector.", Overflow = ToolStripItemOverflow.AsNeeded };
        profileSelector = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 190, AccessibleName = "Active workflow profile", AccessibleDescription = "Selects the editable workflow profile in normal mode; fixed in isolated demo mode.", Overflow = ToolStripItemOverflow.AsNeeded };
        profileSeparator = new ToolStripSeparator { Overflow = ToolStripItemOverflow.AsNeeded };
        operatingStateLabel = new ToolStripLabel("Stopped") { Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold), AccessibleName = "Operating state", AccessibleDescription = "Shows the current Watcher operating state.", Overflow = ToolStripItemOverflow.Never };
        stateSeparator = new ToolStripSeparator { Overflow = ToolStripItemOverflow.Never };
        startButton = new ToolStripButton("Start") { AccessibleName = "Start Watcher", AccessibleDescription = "Starts the selected workflow in normal mode.", Overflow = ToolStripItemOverflow.AsNeeded };
        stopButton = new ToolStripButton("Stop") { AccessibleName = "Stop Watcher", AccessibleDescription = "Stops the active watcher runtime.", Overflow = ToolStripItemOverflow.Never };
        runOnceButton = new ToolStripButton("Run Once") { AccessibleName = "Run one transaction", AccessibleDescription = "Runs one transaction, or the next synthetic fixture action in isolated demo mode.", Overflow = ToolStripItemOverflow.Never };
        emergencyPauseButton = new ToolStripButton("Pause") { AccessibleName = "Emergency Pause", AccessibleDescription = "Emergency Pause immediately pauses the active watcher runtime.", ToolTipText = "Emergency Pause: immediately pause the active watcher runtime.", Overflow = ToolStripItemOverflow.Never };
        moreButton = new ToolStripDropDownButton("More") { AccessibleName = "More actions", AccessibleDescription = "Opens additional workflow actions.", Overflow = ToolStripItemOverflow.Never };
        mainTabs = new TabControl();
        statusBar = new StatusStrip();
        statusSummaryLabel = new ToolStripStatusLabel("Initializing")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Status summary",
            AccessibleDescription = "Shows the latest Watcher status message."
        };
        policyStatusLabel = new ToolStripStatusLabel("Policy: unavailable")
        {
            TextAlign = ContentAlignment.MiddleRight,
            AccessibleName = "Policy status",
            AccessibleDescription = "Shows the active authorization policy."
        };

        SuspendLayout();
        commandBar.CanOverflow = true;
        commandBar.GripStyle = ToolStripGripStyle.Hidden;
        commandBar.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
        commandBar.Padding = new Padding(8, 3, 8, 3);
        commandBar.AutoSize = false;
        commandBar.Items.AddRange([profileLabel, profileSelector, profileSeparator, operatingStateLabel, stateSeparator, startButton, runOnceButton, stopButton, emergencyPauseButton, moreButton]);
        commandBar.Dock = DockStyle.Top;
        commandBar.TabStop = true;

        mainTabs.Dock = DockStyle.Fill;
        mainTabs.Padding = new Point(12, 5);
        mainTabs.TabIndex = 0;
        mainTabs.AccessibleName = "Watcher pages";

        statusBar.Items.AddRange([statusSummaryLabel, policyStatusLabel]);
        statusBar.Dock = DockStyle.Bottom;
        statusBar.AutoSize = false;
        statusBar.SizingGrip = false;

        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "DCS Watcher v2 Preview";
        Controls.Add(mainTabs);
        Controls.Add(commandBar);
        Controls.Add(statusBar);
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        ResumeLayout(false);
        PerformLayout();
    }
}
