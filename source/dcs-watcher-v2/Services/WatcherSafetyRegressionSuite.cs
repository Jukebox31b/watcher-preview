using System.Security.Cryptography;
using System.Text;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class WatcherSafetyRegressionSuite
{
    private readonly BranchLineageSafetyService _lineage = new();
    private readonly HashBoundInstructionFileService _files = new();
    private readonly CodexProvenanceValidator _codex = new();
    private readonly ActiveTaskLockService _locks = new();
    private readonly TransactionReplayGuardService _replay = new();
    private readonly ReportIngestionVerifier _reports = new();

    public RegressionTestReport Run()
    {
        var report = new RegressionTestReport { StartedAtUtc = DateTimeOffset.UtcNow };
        var tempRoot = Path.Combine(Path.GetTempPath(), "DcsWatcherV2-Regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            Run(report, 1, "Normal single-branch wake and response", () => AssertEligible(Validate()));
            Run(report, 2, "Sibling branch created before wake", TestSiblingBeforeWake);
            Run(report, 3, "Sibling branch created after wake", TestSiblingAfterWake);
            Run(report, 4, "Response exists but onCurrentPath=false", () => AssertRejected(Validate(response: r => r.OnCurrentPath = false), "onCurrentPath"));
            Run(report, 5, "Correct assistant role but wrong parent", () => AssertRejected(Validate(response: r => r.ParentMessageId = "wrong-parent"), "parent"));
            Run(report, 6, "Same conversation ID, wrong branch", TestWrongBranch);
            Run(report, 7, "current_node changes during polling", TestCurrentNodeChange);
            Run(report, 8, "Browser and backend disagree", () => AssertRejected(Validate(snapshot: s => s.BrowserBackendAgree = false), "disagree"));
            Run(report, 9, "API verification returns 401", () => AssertRejected(Validate(snapshot: s => { s.ApiVerified = false; s.ApiStatusCode = 401; }), "401"));
            Run(report, 10, "Missing message IDs", () => AssertRejected(Validate(response: r => r.MessageId = string.Empty), "unknown"));
            Run(report, 11, "Multiple assistant children", TestSiblingAfterWake);
            Run(report, 12, "Multiple task envelopes", () => AssertRejected(Validate(response: r => r.Content += "\n" + BuildEnvelope("SC2001-20260716-120100")), "exactly one"));
            Run(report, 13, "Truncated envelope", () => AssertRejected(Validate(response: r => r.Content = r.Content.Replace(ChatGptEnvelopeCapture.CloseMarker, string.Empty)), "exactly one"));
            Run(report, 14, "Whole-page fallback finds an off-path envelope", TestIncidentShape);
            Run(report, 15, "fallbackBody=True is rejected", () => AssertRejected(Validate(response: r => r.FallbackBody = true), "fallback"));
            Run(report, 16, "Stale prior assistant envelope remains in DOM", TestStaleEnvelopeIgnored);
            Run(report, 17, "User regenerates a response", TestSiblingAfterWake);
            Run(report, 18, "User edits a prior message", () => AssertRejected(Validate(snapshot: s => s.Nodes["wake"].ParentMessageId = "edited-parent"), "parent"));
            Run(report, 19, "Browser reload during transaction", () => AssertRejected(Validate(snapshot: s => s.BrowserTabIdentity = "reloaded-tab"), "tab identity"));
            Run(report, 20, "Duplicate wake token", TestDuplicateWakeToken);
            Run(report, 21, "Duplicate instruction delivery", TestDuplicateDelivery);
            Run(report, 22, "Old report arrives after a newer task", TestOldReportAfterNewerTask);
            Run(report, 23, "Local instruction file with correct hash", () => TestValidInstructionFile(tempRoot));
            Run(report, 24, "Local instruction file with wrong size", () => TestWrongSize(tempRoot));
            Run(report, 25, "Local instruction file with wrong hash", () => TestWrongHash(tempRoot));
            Run(report, 26, "Local instruction file modified after authorization", () => TestModifiedFile(tempRoot));
            Run(report, 27, "Local file containing 0x08", () => TestControlByte(tempRoot, 0x08));
            Run(report, 28, "Local file containing 0x0B", () => TestControlByte(tempRoot, 0x0B));
            Run(report, 29, "Corrected file supersedes revoked file", () => TestSupersession(tempRoot));
            Run(report, 30, "Codex rejects missing provenance", () => AssertRejected(_codex.Validate(null, BuildEnvelope(), new ActiveTaskLockRecord()), "provenance"));
            Run(report, 31, "Codex rejects self-declared origin without proof", TestSelfDeclaredOriginRejected);
            Run(report, 32, "Active-task lock prevents follow-on delivery", TestActiveTaskLock);
            Run(report, 33, "Markdown terminal report syntax is recognized", TestMarkdownTerminalReport);
            Run(report, 34, "Missing optional source_report does not reject authenticated active-task report", TestMissingSourceReport);
            Run(report, 35, "Present mismatched source_report remains rejected", TestMismatchedSourceReport);
            Run(report, 36, "Stage 4 requires the complete authorization set", TestStage4Authorization);
            Run(report, 37, "Stage 4 prohibits UI and clipboard fallback", TestStage4FallbackRejected);
            Run(report, 38, "Wake prompt embeds exact authenticated report and prohibits tool use", TestAuthenticatedReportPrompt);
            Run(report, 39, "Hyphenated product version does not become task revision", TestProductVersionNotTaskRevision);
            Run(report, 40, "Contiguous task revision remains supported", TestContiguousTaskRevision);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Temporary test evidence is non-authoritative and can be cleaned up later.
            }
        }

        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        report.Passed = report.Tests.Count(test => test.Passed);
        report.Failed = report.Tests.Count - report.Passed;
        return report;
    }

    private void TestSiblingBeforeWake()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["parent"].ChildMessageIds = ["visible-sibling", "wake"];
            snapshot.Nodes["visible-sibling"] = Node("visible-sibling", "parent", "user", "What is your report status");
        });
        AssertRejected(result, "sibling branch");
    }

    private void TestSiblingAfterWake()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["wake"].ChildMessageIds = ["response", "alternate-response"];
            snapshot.Nodes["alternate-response"] = Node("alternate-response", "wake", "assistant", BuildEnvelope("SC2002-20260716-120200"));
        });
        AssertRejected(result, "multiple");
    }

    private void TestWrongBranch()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["other"] = Node("other", "root", "assistant", "Visible sibling branch");
            snapshot.CurrentNode = "other";
            snapshot.BrowserVisibleMessageIds = ["other"];
        });
        AssertRejected(result, "current_node");
    }

    private void TestCurrentNodeChange()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["changed"] = Node("changed", "root", "user", "Manual branch change");
            snapshot.CurrentNode = "changed";
        });
        AssertRejected(result, "current_node");
    }

    private void TestIncidentShape()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["visible-user"] = Node("visible-user", "parent", "user", "What is your report status");
            snapshot.Nodes["parent"].ChildMessageIds = ["visible-user", "wake"];
            snapshot.CurrentNode = "visible-user";
            snapshot.BrowserVisibleMessageIds = ["visible-user"];
        }, response: response =>
        {
            response.OnCurrentPath = false;
            response.FallbackBody = true;
            response.CaptureMethod = "DocumentBodyInnerText";
        });
        AssertRejected(result, "fallback");
    }

    private void TestStaleEnvelopeIgnored()
    {
        var result = Validate(snapshot: snapshot =>
        {
            snapshot.Nodes["stale"] = Node("stale", "root", "assistant", BuildEnvelope("SC1999-20260716-110000"));
        });
        AssertEligible(result);
        if (!result.EnvelopeTaskId.StartsWith("SC2000-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Validator selected stale off-transaction content.");
        }
    }

    private void TestDuplicateWakeToken()
    {
        var state = new AppState();
        AssertEligible(_replay.TryReserveWakeToken(state, "wake-token"));
        AssertRejected(_replay.TryReserveWakeToken(state, "wake-token"), "Duplicate");
    }

    private void TestDuplicateDelivery()
    {
        var state = new AppState();
        var hash = BranchLineageSafetyService.HashUtf8(BuildEnvelope());
        AssertEligible(_replay.TryRecordManualDelivery(state, hash));
        AssertRejected(_replay.TryRecordManualDelivery(state, hash), "Duplicate");
    }

    private void TestOldReportAfterNewerTask()
    {
        var config = new AppConfig { ReportBranch = "main", ReportFolder = "chatgpt-bridge/reports_from_codex" };
        var state = new AppState
        {
            ActiveTaskLock = new ActiveTaskLockRecord
            {
                IsActive = true,
                ActiveTaskId = "SC2000-20260716-120000",
                SourceReport = "CGPT-REPORT-20260716-110000-sc1999-source.md",
                DeliveryTimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                TerminalReportExpectedPath = "chatgpt-bridge/reports_from_codex/CGPT-REPORT-20260716-120500-sc2000-result.md"
            }
        };
        var valid = BuildReportCandidate(config, "SC2000", state.ActiveTaskLock.SourceReport, state.ActiveTaskLock.TerminalReportExpectedPath, DateTime.UtcNow);
        var authenticated = _reports.Verify(config, state, valid, DateTimeOffset.UtcNow);
        if (!authenticated.Eligible)
        {
            throw new InvalidOperationException("Known-good origin/main report did not authenticate: " + authenticated.RejectionReason);
        }

        var old = BuildReportCandidate(
            config,
            "SC1999",
            "CGPT-REPORT-20260716-100000-sc1998-source.md",
            "chatgpt-bridge/reports_from_codex/CGPT-REPORT-20260716-120600-sc1999-old.md",
            DateTime.UtcNow.AddMinutes(1));
        var rejected = _reports.Verify(config, state, old, DateTimeOffset.UtcNow);
        if (rejected.Eligible || !rejected.RejectionReason.Contains("task ID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Old unrelated report was not rejected by active-task identity.");
        }
    }

    private void TestValidInstructionFile(string root)
    {
        var firstTool = Path.Combine(Path.GetTempPath(), "watcher-safety-tools", "luae.exe");
        var secondTool = Path.Combine(Path.GetTempPath(), "watcher-safety-tools", "powershell.exe");
        var envelope = BuildEnvelope(instruction: $"Use {firstTool} and {secondTool} exactly. " + new string('x', 120));
        var path = Write(root, "SC2000-INSTRUCTION-ENVELOPE-VALID.txt", Encoding.UTF8.GetBytes(envelope));
        var authorization = Authorize(path);
        var result = _files.Validate(authorization, root, [firstTool, secondTool]);
        if (!result.Eligible)
        {
            throw new InvalidOperationException(result.Reason);
        }
    }

    private void TestWrongSize(string root)
    {
        var path = Write(root, "SC2000-INSTRUCTION-ENVELOPE-WRONG-SIZE.txt", Encoding.UTF8.GetBytes(BuildEnvelope()));
        var authorization = Authorize(path);
        authorization.ExpectedSizeBytes++;
        AssertFileRejected(_files.Validate(authorization, root), "size mismatch");
    }

    private void TestWrongHash(string root)
    {
        var path = Write(root, "SC2000-INSTRUCTION-ENVELOPE-WRONG-HASH.txt", Encoding.UTF8.GetBytes(BuildEnvelope()));
        var authorization = Authorize(path);
        authorization.ExpectedSha256 = new string('0', 64);
        AssertFileRejected(_files.Validate(authorization, root), "SHA-256");
    }

    private void TestModifiedFile(string root)
    {
        var path = Write(root, "SC2000-INSTRUCTION-ENVELOPE-MODIFIED.txt", Encoding.UTF8.GetBytes(BuildEnvelope()));
        var authorization = Authorize(path);
        File.SetLastWriteTimeUtc(path, authorization.FileLastWriteTimeUtcAtAuthorization!.Value.UtcDateTime.AddMinutes(1));
        AssertFileRejected(_files.Validate(authorization, root), "modified after authorization");
    }

    private void TestControlByte(string root, byte control)
    {
        var bytes = Encoding.UTF8.GetBytes(BuildEnvelope());
        var marker = Encoding.UTF8.GetBytes("Exact instruction payload");
        var offset = bytes.AsSpan().IndexOf(marker);
        bytes[offset] = control;
        var path = Write(root, $"SC2000-INSTRUCTION-ENVELOPE-CONTROL-{control:X2}.txt", bytes);
        var authorization = Authorize(path);
        AssertFileRejected(_files.Validate(authorization, root), $"0x{control:X2}");
    }

    private void TestSupersession(string root)
    {
        var revoked = Write(root, "SC2000-INSTRUCTION-ENVELOPE-REVOKED.txt", Encoding.UTF8.GetBytes(BuildEnvelope(instruction: "Defective " + new string('x', 120))));
        var replacement = Write(root, "SC2000-INSTRUCTION-ENVELOPE-CORRECTED.txt", Encoding.UTF8.GetBytes(BuildEnvelope(instruction: "Corrected " + new string('y', 120))));
        var record = new SupersessionRecord
        {
            RevokedPath = revoked,
            RevokedSha256 = HashBoundInstructionFileService.HashFile(revoked),
            ReplacementPath = replacement,
            ReplacementSha256 = HashBoundInstructionFileService.HashFile(replacement),
            SupersededAtUtc = DateTimeOffset.UtcNow
        };
        AssertEligible(_files.ValidateSupersession(record, root));
    }

    private void TestSelfDeclaredOriginRejected()
    {
        var envelope = BuildEnvelope().Replace("origin: chatgpt-ui", "origin: chatgpt-ui", StringComparison.Ordinal);
        AssertRejected(_codex.Validate(null, envelope, new ActiveTaskLockRecord()), "provenance");
    }

    private void TestActiveTaskLock()
    {
        var state = new AppState();
        AssertEligible(_locks.TryActivate(state, "SC2000", new string('a', 64), "source.md", "thread", "result.md", DateTimeOffset.UtcNow));
        AssertRejected(_locks.TryActivate(state, "SC2001", new string('b', 64), "source2.md", "thread", "result2.md", DateTimeOffset.UtcNow), "already held");
    }

    private static void TestMarkdownTerminalReport()
    {
        if (!GitHubReportPoller.IsTerminalReportText("- Result: `FAIL_CLOSED`\n") ||
            !GitHubReportPoller.IsTerminalReportText("**Status**: PASS\n") ||
            GitHubReportPoller.IsTerminalReportText("Result: RUNNING\n"))
            throw new InvalidOperationException("Terminal Markdown syntax classification is incorrect.");
    }

    private void TestMissingSourceReport()
    {
        var (config, state, candidate) = BuildActiveReportFixture();
        candidate = candidate with { SourceReport = string.Empty };
        var result = _reports.Verify(config, state, candidate, DateTimeOffset.UtcNow);
        if (!result.Eligible) throw new InvalidOperationException(result.RejectionReason);
    }

    private void TestMismatchedSourceReport()
    {
        var (config, state, candidate) = BuildActiveReportFixture();
        candidate = candidate with { SourceReport = "wrong.md" };
        var result = _reports.Verify(config, state, candidate, DateTimeOffset.UtcNow);
        if (result.Eligible || !result.RejectionReason.Contains("source-report", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A present mismatched source_report was not rejected.");
    }

    private static void TestStage4Authorization()
    {
        var config = ValidStage4Config();
        if (!WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(config, out var reason))
            throw new InvalidOperationException(reason);
        config.AutomaticDeliveryEnabled = false;
        if (WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(config, out _))
            throw new InvalidOperationException("Incomplete Stage 4 authorization was accepted.");
    }

    private static void TestStage4FallbackRejected()
    {
        var config = ValidStage4Config();
        config.CodexUseClipboardFallback = true;
        if (WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(config, out _))
            throw new InvalidOperationException("Stage 4 accepted clipboard fallback.");
    }

    private static void TestAuthenticatedReportPrompt()
    {
        var bytes = Encoding.UTF8.GetBytes("Result: PASS\nExact report body.\n");
        var candidate = new ReportCandidate("folder/report.md", "git:origin/main:folder/report.md", "report.md",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTime.UtcNow, "https://github.test/report", DateTime.UtcNow)
        {
            ContentBytes = bytes,
            Commit = new string('a', 40)
        };
        var prompt = new ChatGptWakePromptBuilder().Build(new AppConfig(), candidate, "wake-token");
        var normalized = prompt.ReplaceLineEndings("\n");
        if (!normalized.Contains("BEGIN_AUTHENTICATED_REPORT\nResult: PASS\nExact report body.\n\nEND_AUTHENTICATED_REPORT", StringComparison.Ordinal) ||
            !prompt.Contains("Do not browse, open the link, call a tool", StringComparison.Ordinal))
            throw new InvalidOperationException("Wake prompt did not bind the exact authenticated report bytes or prohibit tool use.");
    }

    private static AppConfig ValidStage4Config() => new()
    {
        OperatingStage = nameof(WatcherOperatingStage.Stage4LimitedAutomatic),
        Stage4Authorized = true,
        AutomaticWakeEnabled = true,
        AutomaticDeliveryEnabled = true,
        AutomaticInstructionDeliveryEnabled = true,
        LiveCodexIntakeEnabled = true,
        SubmitChatGptPrompt = true,
        AutoCaptureChatGptEnvelope = true,
        SubmitCodexPrompt = true,
        ChatGptCaptureScope = "BackendMessageObject",
        Stage4IntakeExecutablePath = "intake.exe",
        CodexThreadId = "thread",
        CodexUseClipboardFallback = false
    };

    private static (AppConfig Config, AppState State, ReportCandidate Candidate) BuildActiveReportFixture()
    {
        var config = new AppConfig { ReportBranch = "main", ReportFolder = "chatgpt-bridge/reports_from_codex" };
        var state = new AppState { ActiveTaskLock = new ActiveTaskLockRecord
        {
            IsActive = true,
            ActiveTaskId = "SC2000",
            SourceReport = "source.md",
            DeliveryTimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        }};
        return (config, state, BuildReportCandidate(config, "SC2000", "source.md",
            "chatgpt-bridge/reports_from_codex/CGPT-REPORT-20260716-120500-sc2000-result.md", DateTime.UtcNow));
    }

    private SafetyValidationResult Validate(
        Action<WakeTransactionRecord>? transaction = null,
        Action<ConversationLineageSnapshot>? snapshot = null,
        Action<AssistantResponseObservation>? response = null)
    {
        var fixture = BuildFixture();
        transaction?.Invoke(fixture.Transaction);
        snapshot?.Invoke(fixture.Snapshot);
        response?.Invoke(fixture.Response);
        return _lineage.Validate(fixture.Transaction, fixture.Snapshot, fixture.Response);
    }

    private static (WakeTransactionRecord Transaction, ConversationLineageSnapshot Snapshot, AssistantResponseObservation Response) BuildFixture()
    {
        var envelope = BuildEnvelope();
        var transaction = new WakeTransactionRecord
        {
            ConversationId = "conversation",
            CurrentNodeBeforeWake = "parent",
            VisibleBranchAncestry = ["root", "parent"],
            VisibleParentMessageId = "parent",
            BrowserTabIdentity = "tab|https://chatgpt.com/c/conversation",
            WakeToken = "wake-token",
            IntendedSourceReport = "CGPT-REPORT-20260716-110000-sc1999-source.md",
            IntendedActiveTask = "SC2000-20260716-120000",
            WakeMessageId = "wake",
            WakeParentMessageId = "parent",
            WakeCreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
            HumanConfirmed = true,
            Status = "wake-posted"
        };
        var snapshot = new ConversationLineageSnapshot
        {
            ConversationId = "conversation",
            CurrentNode = "response",
            BrowserTabIdentity = transaction.BrowserTabIdentity,
            ApiVerified = true,
            ApiStatusCode = 200,
            BrowserBackendAgree = true,
            BrowserVisibleMessageIds = ["wake", "response"],
            Nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
            {
                ["root"] = Node("root", string.Empty, "system", "root", ["parent"]),
                ["parent"] = Node("parent", "root", "assistant", "Visible terminal", ["wake"]),
                ["wake"] = Node("wake", "parent", "user", "wake-token", ["response"]),
                ["response"] = Node("response", "wake", "assistant", envelope)
            }
        };
        var response = new AssistantResponseObservation
        {
            MessageId = "response",
            ParentMessageId = "wake",
            Role = "assistant",
            Content = envelope,
            Complete = true,
            OnCurrentPath = true,
            WakeToken = "wake-token",
            SourceReport = transaction.IntendedSourceReport,
            CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
            FallbackBody = false,
            ApiVerified = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        return (transaction, snapshot, response);
    }

    private static void TestProductVersionNotTaskRevision()
    {
        const string fileName = "CGPT-REPORT-20260717-230000-sc1479-r35-authenticated-evidence-session-a-disposition.md";
        var parsed = WorkItemIdParser.Parse(fileName);
        if (parsed is null || parsed.ToString() != "SC1479" ||
            !ReportIngestionVerifier.SameWorkItem(fileName, "SC1479-20260717-230000"))
        {
            throw new InvalidOperationException($"Expected SC1479 without a revision, got {parsed?.ToString() ?? "null"}.");
        }
    }

    private static void TestContiguousTaskRevision()
    {
        const string fileName = "CGPT-REPORT-20260715-151800-sc1464r4ak-powershell-identity-session-a-disposition.md";
        var parsed = WorkItemIdParser.Parse(fileName);
        if (parsed is null || parsed.ToString() != "SC1464R4AK" || parsed.Revision != 4 || parsed.RevisionSuffix != "AK")
        {
            throw new InvalidOperationException($"Expected SC1464R4AK, got {parsed?.ToString() ?? "null"}.");
        }
    }

    private static ConversationNodeRecord Node(string id, string parent, string role, string content, List<string>? children = null)
    {
        return new ConversationNodeRecord
        {
            MessageId = id,
            ParentMessageId = parent,
            Role = role,
            Content = content,
            Complete = true,
            ChildMessageIds = children ?? []
        };
    }

    private static string BuildEnvelope(string taskId = "SC2000-20260716-120000", string? instruction = null)
    {
        instruction ??= "Exact instruction payload " + new string('x', 120);
        return string.Join("\n", new[]
        {
            ChatGptEnvelopeCapture.OpenMarker,
            $"task_id: {taskId}",
            "origin: chatgpt-ui",
            "repo: example/watcher-regression",
            "target: codex-director",
            "mode: instruction",
            "created_at: 2026-07-16T12:00:00-04:00",
            "source_report: CGPT-REPORT-20260716-110000-sc1999-source.md",
            string.Empty,
            "BEGIN_INSTRUCTION",
            instruction,
            "END_INSTRUCTION",
            ChatGptEnvelopeCapture.CloseMarker
        });
    }

    private static ReportCandidate BuildReportCandidate(AppConfig config, string taskId, string sourceReport, string path, DateTime lastWriteUtc)
    {
        var content = Encoding.UTF8.GetBytes($"task_id: {taskId}\nsource_report: {sourceReport}\nresult: PASS\n");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new ReportCandidate(path, "git:origin/main:" + path, Path.GetFileName(path), hash, lastWriteUtc, "https://github.test/" + path, lastWriteUtc)
        {
            Repository = config.ReportRepoFullName,
            Branch = "main",
            Commit = new string('a', 40),
            BlobIdentity = new string('b', 40),
            ContentBytes = content,
            ReportTaskId = taskId,
            SourceReport = sourceReport,
            IsTerminal = true
        };
    }

    private static string Write(string root, string fileName, byte[] bytes)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static HashBoundInstructionAuthorization Authorize(string path)
    {
        var info = new FileInfo(path);
        return new HashBoundInstructionAuthorization
        {
            AbsolutePath = path,
            FileName = info.Name,
            ExpectedSizeBytes = info.Length,
            ExpectedSha256 = HashBoundInstructionFileService.HashFile(path),
            ExpectedTaskId = "SC2000-20260716-120000",
            ReceivingDirectorThreadId = string.Join("-", "00000000", "0000", "4000", "8000", "000000000003"),
            AuthorizedAtUtc = DateTimeOffset.UtcNow,
            FileLastWriteTimeUtcAtAuthorization = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        };
    }

    private static void Run(RegressionTestReport report, int number, string name, Action test)
    {
        try
        {
            test();
            report.Tests.Add(new RegressionTestCaseResult { Number = number, Name = name, Passed = true, Details = "PASS" });
        }
        catch (Exception ex)
        {
            report.Tests.Add(new RegressionTestCaseResult { Number = number, Name = name, Passed = false, Details = ex.Message });
        }
    }

    private static void AssertEligible(SafetyValidationResult result)
    {
        if (!result.Eligible)
        {
            throw new InvalidOperationException("Expected eligible result: " + result.Reason);
        }
    }

    private static void AssertRejected(SafetyValidationResult result, string reasonFragment)
    {
        if (result.Eligible || !result.Reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected rejection containing '{reasonFragment}', got: {result.Reason}");
        }
    }

    private static void AssertFileRejected(HashBoundFileValidationResult result, string reasonFragment)
    {
        if (result.Eligible || !result.Reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected file rejection containing '{reasonFragment}', got: {result.Reason}");
        }
    }
}
