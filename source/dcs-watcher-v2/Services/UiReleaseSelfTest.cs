using DcsWatcherV2.Demo;
using DcsWatcherV2.Models;
using System.Runtime.InteropServices;

namespace DcsWatcherV2.Services;

public sealed record UiReleaseSelfTestResult(bool Passed, IReadOnlyList<string> Messages);

public static class UiReleaseSelfTest
{
    public static UiReleaseSelfTestResult Run()
    {
        var results = new List<(string Name, bool Passed)>();
        using var form = new MainForm(demoMode: true);
        var toolStrip = form.Controls.OfType<ToolStrip>().Single(strip => strip.Items.OfType<ToolStripButton>().Any(item => item.Text == "Stop"));
        var tabs = form.Controls.OfType<TabControl>().Single();
        var statusStrip = form.Controls.OfType<StatusStrip>().Single();
        form.CreateControl();
        _ = form.Handle;
        _ = toolStrip.Handle;
        var nativeDpi = checked((int)GetDpiForWindow(form.Handle));
        form.ApplyShellMetricsForSelfTest();

        Check($"WinForms uses PerMonitorV2 visual environment at {nativeDpi} DPI",
            Application.RenderWithVisualStyles &&
            AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorV2AwarenessContext));
        Check($"Managed controls match the current native {nativeDpi} DPI",
            nativeDpi > 0 &&
            form.DeviceDpi == nativeDpi &&
            toolStrip.DeviceDpi == nativeDpi);
        Check("Form has no form-level scrollbar", !form.AutoScroll);
        Check("Compact minimum is DPI-scaled from 760x520",
            form.MinimumSize == new Size(Scale(760, nativeDpi), Scale(520, nativeDpi)));
        Check("Five operational pages are present", tabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(["Overview", "Workflow", "Activity", "Evidence", "Diagnostics"]));
        Check("Stop remains outside overflow", toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Stop").Overflow == ToolStripItemOverflow.Never);
        Check("Pause remains outside overflow", toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Pause").Overflow == ToolStripItemOverflow.Never);
        Check("Command bar supports compact overflow", toolStrip.CanOverflow);
        Check("Primary pages expose accessible names", tabs.TabPages.Cast<TabPage>().SelectMany(page => page.Controls.Cast<Control>()).All(control => !string.IsNullOrWhiteSpace(control.AccessibleName)));

        var demoProfile = new WatcherProfileV1
        {
            Enabled = false,
            Identity = new ProfileIdentityV1 { ProfileId = "ui-self-test", Name = "UI self-test" },
            ReportSource = new ReportSourceProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.ReportDemoFixture } },
            Director = new DirectorProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DirectorDemoFixture } },
            Destination = new DestinationProfileV1 { Adapter = new AdapterConfigurationV1 { AdapterId = WatcherAdapterIds.DeliveryTestSink } },
            AutomationPolicy = new AutomationPolicyProfileV1 { Kind = WatcherAutomationPolicyKind.ManualApproval, RequireVisibleHumanApproval = true }
        };
        Check("Demo profile is disabled and test-sink only", new ProfileValidator().Validate(demoProfile).IsValid);

        form.InitializeDemoForSelfTest();
        var start = toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Start");
        var stop = toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Stop");
        var runOnce = toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Run Once");
        var emergencyPause = toolStrip.Items.OfType<ToolStripButton>().Single(item => item.Text == "Pause");
        var more = toolStrip.Items.OfType<ToolStripDropDownButton>().Single(item => item.AccessibleName == "More actions");
        Check("Demo hides the irrelevant Start control", !start.Available);
        Check("Safety-critical demo commands cannot overflow",
            new ToolStripItem[]
            {
                toolStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Operating state"),
                runOnce,
                stop,
                emergencyPause,
                more
            }.All(item => item.Overflow == ToolStripItemOverflow.Never));
        Check("Profile controls may use compact command-bar overflow",
            toolStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Profile").Overflow == ToolStripItemOverflow.AsNeeded &&
            toolStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Active workflow profile").Overflow == ToolStripItemOverflow.AsNeeded);
        Check("Demo disables no-op stop controls without inhibiting Run Once",
            !stop.Enabled &&
            !emergencyPause.Enabled &&
            runOnce.Enabled &&
            stop.ToolTipText?.Contains("demo mode", StringComparison.OrdinalIgnoreCase) == true &&
            emergencyPause.ToolTipText?.Contains("demo mode", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrWhiteSpace(stop.AccessibleDescription) &&
            !string.IsNullOrWhiteSpace(emergencyPause.AccessibleDescription));
        Check("Pause keeps explicit Emergency Pause metadata",
            emergencyPause.AccessibleName == "Emergency Pause" &&
            emergencyPause.AccessibleDescription?.Contains("Emergency Pause", StringComparison.OrdinalIgnoreCase) == true &&
            emergencyPause.ToolTipText?.Contains("Emergency Pause", StringComparison.OrdinalIgnoreCase) == true);
        Check("Command actions expose accessible names and descriptions",
            toolStrip.Items.OfType<ToolStripButton>().All(item =>
                !string.IsNullOrWhiteSpace(item.AccessibleName) &&
                !string.IsNullOrWhiteSpace(item.AccessibleDescription)));

        using var setup = new FirstRunSetupForm(new ProfileService(Path.Combine(
            Path.GetTempPath(),
            "DcsWatcherV2UiSelfTest",
            Guid.NewGuid().ToString("N"))));
        setup.CreateControl();
        _ = setup.Handle;
        setup.ApplyLayoutForSelfTest();
        setup.Size = setup.MinimumSize;
        setup.PerformLayout();
        var setupControls = Descendants(setup).ToArray();
        var setupNavigation = setupControls.OfType<FlowLayoutPanel>()
            .Single(panel => panel.AccessibleName == "Setup navigation");
        var setupButtons = setupNavigation.Controls.OfType<Button>().ToArray();
        Check("Setup navigation sizes itself around every button",
            setupNavigation.AutoSize &&
            setupButtons.Length == 3 &&
            setupButtons.All(button => setupNavigation.ClientRectangle.Contains(button.Bounds)) &&
            setupButtons.SelectMany((button, index) => setupButtons.Skip(index + 1).Select(other => (button, other)))
                .All(pair => !pair.button.Bounds.IntersectsWith(pair.other.Bounds)));

        var setupPages = setupControls.OfType<TabControl>().Single();
        Check("Setup wizard minimum is DPI-scaled from 700x560",
            setup.MinimumSize == new Size(Scale(700, nativeDpi), Scale(560, nativeDpi)));
        setup.SelectPageForSelfTest(setupPages.TabCount - 1);
        setupNavigation.PerformLayout();
        var createButton = setupButtons.SingleOrDefault(button => button.Text == "Create disabled profile");
        Check("Setup review keeps the long create button inside the footer",
            createButton is not null &&
            setupNavigation.ClientRectangle.Contains(createButton.Bounds));

        var setupChoices = setupControls.OfType<ComboBox>().ToArray();
        Check("Every setup choice exposes four documented options",
            setupChoices.Length == 4 &&
            setupChoices.All(combo => combo.Items.Count == 4 && !string.IsNullOrWhiteSpace(combo.AccessibleDescription)));
        Check("Every setup option provides a distinct plain-language explanation",
            setupChoices.All(combo =>
            {
                var parent = combo.Parent;
                if (parent is null) return false;
                var explanation = parent.Controls.OfType<Label>()
                    .SingleOrDefault(label => label.AccessibleName?.EndsWith("option explanation", StringComparison.Ordinal) == true);
                if (explanation is null) return false;
                var descriptions = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < combo.Items.Count; index++)
                {
                    combo.SelectedIndex = index;
                    if (!explanation.Text.StartsWith("What this option does: ", StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(explanation.AccessibleDescription)) return false;
                    descriptions.Add(explanation.Text);
                }
                return descriptions.Count == combo.Items.Count;
            }));
        setup.SelectPageForSelfTest(2);
        var selectedSetupPage = setupPages.TabPages[2];
        var visibleChoice = selectedSetupPage.Controls.Cast<Control>()
            .SelectMany(Descendants)
            .OfType<ComboBox>()
            .Single();
        var visibleExplanation = visibleChoice.Parent!.Controls.OfType<Label>()
            .Single(label => label.AccessibleName?.EndsWith("option explanation", StringComparison.Ordinal) == true);
        Check("Setup option explanation uses a fluid full-width layout",
            visibleChoice.Parent is TableLayoutPanel { AutoSize: true } &&
            visibleExplanation.AutoSize &&
            visibleExplanation.Text.StartsWith("What this option does: ", StringComparison.Ordinal));

        var overview = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Overview");
        var pipeline = Descendants(overview).OfType<Label>().Single(label => label.AccessibleName == "Transaction pipeline");
        Check("Demo pipeline summary is compact with full accessible meaning",
            pipeline.Text == "Fixture -> approval -> test sink" &&
            pipeline.AccessibleDescription?.Contains("lineage", StringComparison.OrdinalIgnoreCase) == true &&
            pipeline.AccessibleDescription.Contains("replay", StringComparison.OrdinalIgnoreCase));
        var overviewControls = Descendants(overview).ToArray();
        var overviewCollapseButtons = overviewControls.OfType<Button>()
            .Where(button => button.Text.Contains("summary", StringComparison.OrdinalIgnoreCase) || button.Text.Contains("activity", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Check("Overview collapse controls and activity grid expose explicit accessibility metadata",
            overviewCollapseButtons.Length == 2 &&
            overviewCollapseButtons.All(HasAccessibilityMetadata) &&
            overviewControls.OfType<DataGridView>().Single().AccessibleName == "Recent activity grid" &&
            HasAccessibilityMetadata(overviewControls.OfType<DataGridView>().Single()));
        var overviewSummaryValues = overviewControls.OfType<Label>()
            .Where(label => label.Text is "Demo" or "Build Week Demo" or "Fixture -> approval -> test sink")
            .ToArray();
        var overviewSummaryLayout = overviewSummaryValues.First().Parent as TableLayoutPanel;
        Check("Overview summary values reserve readable DPI-aware heights",
            overviewSummaryValues.All(label =>
                label.MinimumSize.Height >= label.Font.Height));
        Check("Overview summary uses explicit DPI-aware text rows",
            overviewSummaryLayout is not null &&
            overviewSummaryLayout.RowStyles.Cast<RowStyle>().Take(6).All(style =>
                style.SizeType == SizeType.Absolute &&
                style.Height >= overviewSummaryValues[0].Font.Height));

        var diagnostics = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Diagnostics");
        var diagnosticControls = Descendants(diagnostics).ToArray();
        Check("Diagnostics actions, health list, and log expose explicit accessibility metadata",
            diagnosticControls.OfType<Button>().Count() == 2 &&
            diagnosticControls.OfType<Button>().All(HasAccessibilityMetadata) &&
            diagnosticControls.OfType<ListView>().Single().AccessibleName == "Component health list" &&
            HasAccessibilityMetadata(diagnosticControls.OfType<ListView>().Single()) &&
            diagnosticControls.OfType<TextBox>().Single().AccessibleName == "Diagnostic log" &&
            HasAccessibilityMetadata(diagnosticControls.OfType<TextBox>().Single()));
        var diagnosticLog = diagnosticControls.OfType<TextBox>().Single(control => control.AccessibleName == "Diagnostic log");
        var diagnosticActions = diagnosticControls.OfType<FlowLayoutPanel>().Single(control => control.AccessibleName == "Diagnostic actions");
        Check("Diagnostics uses a resizable wrapped layout without horizontal log scrolling",
            diagnosticControls.OfType<SplitContainer>().Count() == 1 &&
            diagnosticActions.WrapContents &&
            diagnosticLog.WordWrap &&
            diagnosticLog.ScrollBars == ScrollBars.Vertical &&
            diagnosticControls.OfType<Label>().Single(label => label.AccessibleName == "Experimental adapter notice").AutoEllipsis);

        CheckCommandBarLayout(ScaleSize(new Size(760, 520), nativeDpi));
        CheckCommandBarLayout(ScaleSize(new Size(900, 600), nativeDpi));
        CheckCommandBarLayout(ScaleSize(new Size(1100, 600), nativeDpi));

        var workflow = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Workflow");
        var workflowControls = Descendants(workflow).ToArray();
        var workflowInputs = workflowControls
            .Where(control => control is TextBox or ComboBox or NumericUpDown or CheckBox)
            .Where(control => control.Parent is not NumericUpDown)
            .ToArray();
        var workflowButtons = workflowControls.OfType<Button>().ToArray();
        Check("Demo workflow inputs and actions are fixed read-only controls",
            workflowInputs.All(control => control switch
            {
                TextBox textBox => !textBox.Enabled || textBox.ReadOnly,
                _ => !control.Enabled
            }) && workflowButtons.All(button => !button.Enabled));
        Check("Demo workflow visibly fixes synthetic manual test-sink routing",
            FindWorkflowText(workflowInputs, "Source adapter") == "Synthetic fixture" &&
            FindWorkflowText(workflowInputs, "Capture adapter") == "Synthetic fixture" &&
            FindWorkflowText(workflowInputs, "Policy") == "Manual" &&
            FindWorkflowText(workflowInputs, "Delivery adapter") == "Test sink" &&
            FindWorkflowText(workflowInputs, "Conversation identity") == "Synthetic fixture" &&
            FindWorkflowText(workflowInputs, "Destination identity") == "Test sink");
        Check("Workflow inputs and action buttons expose accessible metadata",
            workflowInputs.Cast<Control>().Concat(workflowButtons).All(control =>
                !string.IsNullOrWhiteSpace(control.AccessibleName) &&
                !string.IsNullOrWhiteSpace(control.AccessibleDescription)));

        using var editableWorkflow = new DcsWatcherV2.Controls.WorkflowEditorControl();
        editableWorkflow.LoadProfile(demoProfile);
        var editableControls = Descendants(editableWorkflow).ToArray();
        var normalRoutingInputs = editableControls
            .Where(control => control.AccessibleName is "Source adapter" or "Capture adapter" or "Delivery adapter" or "Policy" or "Expected repository")
            .ToArray();
        Check("Normal workflow mode remains editable",
            normalRoutingInputs.Length == 5 &&
            normalRoutingInputs.All(control => control.Enabled) &&
            editableControls.OfType<Button>().All(button => button.Enabled));

        Check("Demo shows no result before execution",
            form.DemoActivityCount == 0 &&
            form.DemoSinkAcceptedCount == 0 &&
            !form.DemoStatusSummary.Contains("accepted", StringComparison.OrdinalIgnoreCase) &&
            !form.DemoStatusSummary.Contains("rejected", StringComparison.OrdinalIgnoreCase));

        var accepted = form.ExecuteDemoForSelfTest("current");
        var acceptedTransactionId = DemoFixtureCatalog.CurrentPath().Wake.TransactionId;
        Check("Demo current-path action projects actual hashes",
            accepted?.Accepted == true &&
            accepted.Evidence.EnvelopeSha256.Length == 64 &&
            accepted.Evidence.ProvenanceSha256.Length == 64 &&
            accepted.Evidence.SignerFingerprintSha256.Length == 64 &&
            form.DemoEvidenceValue("Conversation") == DemoFixtureCatalog.CurrentPath().Wake.ConversationId &&
            form.DemoEvidenceValue("Task ID") == "DEMO-TASK-CURRENT-001" &&
            form.DemoSinkAcceptedCount == 1 &&
            form.DemoSinkReceiveCount == 1);

        var replay = form.ExecuteDemoForSelfTest("replay");
        Check("Demo replay leaves sink count unchanged",
            replay is { Accepted: false } &&
            replay.Disposition == DcsWatcherV2.Demo.DemoDispositions.RejectedReplay &&
            form.DemoSinkAcceptedCount == 1 &&
            form.DemoSinkReceiveCount == 1 &&
            form.DemoSigningCount == 1 &&
            form.DemoDeliveryAttemptCount == 1 &&
            form.DemoLastSuccess.Contains(acceptedTransactionId, StringComparison.Ordinal));

        var sibling = form.ExecuteDemoForSelfTest("sibling");
        Check("Demo sibling branch never signs or delivers",
            sibling is { Accepted: false } &&
            sibling.Disposition == DcsWatcherV2.Demo.DemoDispositions.RejectedBranchDivergence &&
            !sibling.Evidence.SignatureCreated &&
            !sibling.Evidence.DeliveryAttempted &&
            form.DemoSigningCount == 1 &&
            form.DemoDeliveryAttemptCount == 1 &&
            form.DemoEvidenceValue("Task ID") == "Not available" &&
            form.DemoEvidenceValue("Envelope SHA-256") == "Not available" &&
            form.DemoArtifactSummary.Contains("Provenance SHA-256: Not available", StringComparison.Ordinal) &&
            form.DemoArtifactSummary.Contains("Signer fingerprint: Not available", StringComparison.Ordinal));
        Check("Latest rejection remains separate from accepted history",
            form.DemoEvidenceValue("Authorization") == DcsWatcherV2.Demo.DemoDispositions.RejectedBranchDivergence &&
            form.DemoLastSuccess.Contains(acceptedTransactionId, StringComparison.Ordinal));

        _ = form.ExecuteDemoForSelfTest("reset");
        Check("Demo reset creates a clean composition",
            form.DemoActivityCount == 0 &&
            form.DemoSinkAcceptedCount == 0 &&
            form.DemoSinkReceiveCount == 0 &&
            form.DemoSigningCount == 0 &&
            form.DemoDeliveryAttemptCount == 0);

        var aggregate = WatcherReleaseTestSuite.AggregateForSelfTest(
            new WatcherReleaseSuiteResult("synthetic-pass", 1, 0, ["PASS"]),
            new WatcherReleaseSuiteResult("synthetic-fail", 0, 1, ["FAIL"]));
        Check("Unified release aggregation returns a failing disposition", !aggregate.Passed && aggregate.Failed == 1);

        var messages = results.Select(result => $"UI release self-test: {(result.Passed ? "PASS" : "FAIL")} - {result.Name}").ToArray();
        return new UiReleaseSelfTestResult(results.All(result => result.Passed), messages);

        void Check(string name, bool passed) => results.Add((name, passed));

        void CheckCommandBarLayout(Size size)
        {
            form.Size = size;
            form.PerformLayout();
            toolStrip.PerformLayout();
            statusStrip.PerformLayout();

            var required = new (string Name, ToolStripItem Item)[]
            {
                ("State", toolStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Operating state")),
                ("Run Once", runOnce),
                ("Stop", stop),
                ("Pause", emergencyPause),
                ("More", more)
            };
            var items = required.Select(entry => entry.Item).ToArray();
            var visibleOnBar = required.All(entry =>
                entry.Item.Available &&
                entry.Item.Placement == ToolStripItemPlacement.Main &&
                !entry.Item.IsOnOverflow);
            var containedBounds = items.All(item =>
                item.Bounds.Width > 0 &&
                item.Bounds.Height > 0 &&
                (toolStrip.DisplayRectangle.Contains(item.Bounds) || toolStrip.ClientRectangle.Contains(item.Bounds)));
            var doNotIntersect = items.SelectMany((item, index) => items.Skip(index + 1).Select(other => (item, other)))
                .All(pair => !pair.item.Bounds.IntersectsWith(pair.other.Bounds));
            var buttons = items.Where(item => item is ToolStripButton or ToolStripDropDownButton);
            var textFits = buttons.All(item => item.Bounds.Width >= item.GetPreferredSize(Size.Empty).Width);
            var selector = (ToolStripComboBox)toolStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Active workflow profile");
            var selectorTextWidth = TextRenderer.MeasureText(
                selector.Text,
                selector.Font,
                Size.Empty,
                TextFormatFlags.NoPadding).Width + SystemInformation.VerticalScrollBarWidth + Scale(12, nativeDpi);
            var selectorReachable = selector.Available && selector.Placement is ToolStripItemPlacement.Main or ToolStripItemPlacement.Overflow;
            var selectorTextFits = selector.Placement != ToolStripItemPlacement.Main || selector.Bounds.Width >= selectorTextWidth;
            var moreOnMainBar = more.Available &&
                more.Placement == ToolStripItemPlacement.Main &&
                !more.IsOnOverflow &&
                more.Alignment == ToolStripItemAlignment.Left;
            var summary = statusStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Status summary");
            var policyStatus = statusStrip.Items.Cast<ToolStripItem>().Single(item => item.AccessibleName == "Policy status");
            var statusContained = statusStrip.ClientRectangle.Contains(summary.Bounds) &&
                statusStrip.ClientRectangle.Contains(policyStatus.Bounds);
            var statusSeparated = !summary.Bounds.IntersectsWith(policyStatus.Bounds);
            var logicalSize = new Size(Scale(size.Width, 96, nativeDpi), Scale(size.Height, 96, nativeDpi));
            var sizeName = $"{size.Width}x{size.Height} physical / {logicalSize.Width}x{logicalSize.Height} logical at {nativeDpi} DPI";
            var geometry = $"strip {toolStrip.ClientSize.Width}px; " + string.Join(", ", required.Select(entry =>
                $"{entry.Name}={entry.Item.Bounds.X}:{entry.Item.Bounds.Width}/{entry.Item.Placement}"));

            Check($"Form applies exact target bounds at {sizeName}", form.Size == size);
            Check($"Command bar essentials are visible at {sizeName} ({geometry})", visibleOnBar);
            Check($"Command bar essentials are contained at {sizeName}", containedBounds);
            Check($"Command bar essentials do not intersect at {sizeName}", doNotIntersect);
            Check($"Command button text is not clipped at {sizeName}", textFits);
            Check($"Command bar receives a DPI-scaled content height at {sizeName}",
                !toolStrip.AutoSize && toolStrip.Height >= Scale(40, nativeDpi));
            Check($"Profile selector remains reachable at {sizeName}", selectorReachable);
            Check($"Profile selection text is not clipped at {sizeName}", selectorTextFits);
            Check($"More remains on the main command bar at {sizeName}", moreOnMainBar);
            Check($"Status summary and policy are contained at {sizeName}", statusContained);
            Check($"Status summary and policy do not overlap at {sizeName}", statusSeparated);
        }
    }

    private static bool HasAccessibilityMetadata(Control control) =>
        !string.IsNullOrWhiteSpace(control.AccessibleName) &&
        !string.IsNullOrWhiteSpace(control.AccessibleDescription);

    private static int Scale(int value, int dpi) => Scale(value, dpi, 96);

    private static Size ScaleSize(Size value, int dpi) =>
        new(Scale(value.Width, dpi), Scale(value.Height, dpi));

    private static int Scale(int value, int numerator, int denominator) =>
        (int)Math.Round(value * (double)numerator / denominator);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string FindWorkflowText(IEnumerable<Control> controls, string accessibleName) =>
        controls.Single(control => control.AccessibleName == accessibleName).Text;

    private static readonly IntPtr PerMonitorV2AwarenessContext = new(-4);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
