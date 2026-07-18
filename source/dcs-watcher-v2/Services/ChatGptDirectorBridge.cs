using System.Diagnostics;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed record ChatGptSendResult(
    bool Success,
    bool Busy,
    bool Deferred,
    string Token,
    string Message,
    string? TargetUrl);

public sealed record ChatGptDomCaptureResult(
    bool Success,
    string Text,
    EnvelopeExtractionDiagnostics? Diagnostics,
    string Message,
    string? TargetUrl);

public sealed class ChatGptDirectorBridge
{
    private readonly EdgeCdpClient _cdpClient;

    public ChatGptDirectorBridge()
        : this(new EdgeCdpClient())
    {
    }

    public ChatGptDirectorBridge(EdgeCdpClient cdpClient)
    {
        _cdpClient = cdpClient;
    }

    public async Task<ChatGptSendResult> SendPromptAsync(
        string prompt,
        AppConfig config,
        AppState state,
        CancellationToken cancellationToken,
        LogService? log = null)
    {
        var token = ExtractWakeToken(prompt);
        state.LastChatGptWakeToken = token;
        log?.Info($"Prompt token generated: {token}", "ChatGPT");

        if (!WatcherSafetyPolicy.CanPostWake(config, state.WakeTransaction, out var safetyReason))
        {
            log?.Warning(safetyReason, "Safety");
            return Fail(state, token, safetyReason);
        }

        if (!state.WakeTransaction!.WakeToken.Equals(token, StringComparison.Ordinal))
        {
            const string mismatch = "Wake prompt token does not match the human-confirmed transaction token.";
            log?.Warning(mismatch, "Safety");
            return Fail(state, token, mismatch);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.ChatGptSendTimeoutSeconds)));
        var ct = timeoutCts.Token;

        try
        {
            if (string.IsNullOrWhiteSpace(config.ChatGptDirectorUrl))
            {
                return Fail(state, token, "ChatGPT Director URL is empty. Enter the Director 2 URL before sending.");
            }

            await EnsureEdgeCdpAsync(config, log, ct);
            var target = await FindOrOpenTargetAsync(config, log, ct);
            if (target is null)
            {
                return Fail(state, token, "No safe ChatGPT Director tab was found or opened.");
            }

            if (!ChatGptUrlMatcher.Parse(target.Url).IsChatGptHost)
            {
                return Fail(state, token, $"Refusing to send ChatGPT wake to non-chatgpt.com target: {target.Title} | {target.Url}");
            }

            state.LastChatGptTargetUrl = target.Url;
            log?.Info($"ChatGPT tab selected: {target.Title} | {target.Url}", "ChatGPT");
            log?.Info("ChatGPT CDP target selected; foreground focus is not required.", "ChatGPT");

            var busy = await DetectBusyAsync(target, ct);
            if (busy.IsBusy)
            {
                var message = $"ChatGPT appears busy; wake deferred. {busy.Reason}";
                state.LastChatGptWakeResult = message;
                log?.Warning(message, "ChatGPT");
                return new ChatGptSendResult(false, true, true, token, message, target.Url);
            }

            log?.Info("ChatGPT appears ready.", "ChatGPT");
            var initialAssistantCount = await GetAssistantMessageCountAsync(target, ct);
            var inserted = await InsertPromptAsync(target, prompt, token, ct);
            if (!inserted.Success)
            {
                var message = inserted.Message + " If ChatGPT is showing a login page, sign into the dedicated Edge profile and retry.";
                state.LastChatGptWakeResult = message;
                log?.Error(message, "ChatGPT");
                return new ChatGptSendResult(false, false, false, token, message, target.Url);
            }

            log?.Info("Prompt inserted into ChatGPT composer.", "ChatGPT");
            var clicked = await ClickSendAsync(target, ct);
            if (!clicked.Success)
            {
                var message = clicked.Message;
                state.LastChatGptWakeResult = message;
                log?.Error(message, "ChatGPT");
                return new ChatGptSendResult(false, false, false, token, message, target.Url);
            }

            log?.Info("Send button clicked.", "ChatGPT");
            var reportIdentifier = ExtractReportIdentifier(prompt);
            var confirmation = await ConfirmSendAsync(target, token, reportIdentifier, initialAssistantCount, config, ct);
            state.LastChatGptWakeResult = confirmation.Message;

            if (!confirmation.Success)
            {
                log?.Error($"Send confirmation failed: {confirmation.Message}", "ChatGPT");
                return new ChatGptSendResult(false, false, false, token, confirmation.Message, target.Url);
            }

            var binding = await ResolveExactWakeMessageAsync(target, token, ct);
            if (!binding.Success ||
                state.WakeTransaction is null ||
                !binding.ParentMessageId.Equals(state.WakeTransaction.VisibleParentMessageId, StringComparison.Ordinal) ||
                !binding.OnCurrentPath)
            {
                var bindingFailure = "Wake was posted but exact backend message ID/current-path binding failed; transaction stopped fail-closed. " + binding.Message;
                if (state.WakeTransaction is not null)
                {
                    state.WakeTransaction.Status = "wake-id-binding-failed";
                }
                log?.Error(bindingFailure, "Safety");
                return Fail(state, token, bindingFailure);
            }

            state.WakeTransaction.WakeMessageId = binding.MessageId;
            state.WakeTransaction.WakeParentMessageId = binding.ParentMessageId;
            state.WakeTransaction.WakeCreatedAtUtc = binding.CreatedAtUtc;
            state.WakeTransaction.Status = "wake-posted-id-bound";

            state.LastChatGptWakeSentAtUtc = DateTimeOffset.UtcNow;
            state.LastChatGptWakeToken = token;
            log?.Info($"Send confirmed: {confirmation.Message}", "ChatGPT");
            return new ChatGptSendResult(true, false, false, token, confirmation.Message, target.Url);
        }
        catch (OperationCanceledException)
        {
            return Fail(state, token, $"ChatGPT send timed out after {config.ChatGptSendTimeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            return Fail(state, token, $"ChatGPT wake failed: {ex.Message}");
        }
    }

    public async Task<ChatGptDomCaptureResult> CaptureLatestEnvelopeTextAsync(
        AppConfig config,
        AppState state,
        CancellationToken cancellationToken,
        LogService? log = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.ChatGptCaptureTimeoutSeconds)));
        var ct = timeoutCts.Token;

        try
        {
            if (string.IsNullOrWhiteSpace(config.ChatGptDirectorUrl))
            {
                return new ChatGptDomCaptureResult(false, string.Empty, null, "ChatGPT Director URL is empty. Configure it before capture.", null);
            }

            await EnsureEdgeCdpAsync(config, log, ct);
            var target = await FindOrOpenTargetAsync(config, log, ct);
            if (target is null)
            {
                return new ChatGptDomCaptureResult(false, string.Empty, null, "No safe ChatGPT Director tab was found for capture.", null);
            }

            state.LastChatGptTargetUrl = target.Url;
            log?.Info($"ChatGPT capture tab selected: {target.Title} | {target.Url}", "ChatGPT");
            log?.Info("ChatGPT capture uses Edge CDP without foreground focus.", "ChatGPT");

            var periodicRefreshAge = TimeSpan.FromSeconds(Math.Max(60, config.ChatGptLateEnvelopeSweepSeconds));
            var refreshForFailedCapture = state.ShouldRefreshChatGptBeforeCapture(periodicRefreshAge);
            var refreshForSessionSync = state.LastChatGptConversationRefreshAtUtc is null ||
                                        DateTimeOffset.UtcNow - state.LastChatGptConversationRefreshAtUtc.Value >= periodicRefreshAge;
            if (refreshForFailedCapture || refreshForSessionSync)
            {
                log?.Info(
                    refreshForFailedCapture
                        ? "Refreshing ChatGPT tab in background because the latest report capture previously failed."
                        : "Refreshing ChatGPT tab in background for periodic cross-session conversation synchronization.",
                    "ChatGPT");
                await _cdpClient.ReloadAsync(target, ct);
                await WaitForDocumentReadyAsync(target, ct);
                await WaitForChatGptConversationHydratedAsync(target, ct);
                state.LastChatGptConversationRefreshAtUtc = DateTimeOffset.UtcNow;
                var refreshedTarget = await FindOrOpenTargetAsync(config, log, ct);
                if (refreshedTarget is not null)
                {
                    target = refreshedTarget;
                    state.LastChatGptTargetUrl = target.Url;
                    log?.Info($"ChatGPT capture tab refreshed: {target.Title} | {target.Url}", "ChatGPT");
                }
            }

            var conversationData = await ExtractConversationDataTextAsync(target, log, ct);
            if (!string.IsNullOrWhiteSpace(conversationData.Text))
            {
                log?.Info(
                    $"Envelope conversation-data extraction complete. assistantMessages={conversationData.Diagnostics.AssistantMessageCount} selectedIndex={conversationData.Diagnostics.SelectedAssistantMessageIndex} selectedEnvelopeCount={conversationData.Diagnostics.EnvelopeCount} textLength={conversationData.Diagnostics.TextLength} task_id={conversationData.Diagnostics.SelectedTaskId} candidates={conversationData.Diagnostics.SkippedDuplicateTaskIds}",
                    "ChatGPT");
                return new ChatGptDomCaptureResult(true, conversationData.Text, conversationData.Diagnostics, "Conversation data extracted.", target.Url);
            }

            log?.Warning(
                $"Conversation-data envelope extraction did not find a usable envelope. {conversationData.Diagnostics.RejectionReason}",
                "ChatGPT");

            await ScrollConversationToBottomAsync(target, ct);
            var expandedBeforeWait = await ExpandCollapsedMessagesAsync(target, ct);
            if (expandedBeforeWait > 0)
            {
                log?.Info($"Expanded {expandedBeforeWait} collapsed ChatGPT message control(s) before immediate envelope extraction.", "ChatGPT");
                await ScrollConversationToBottomAsync(target, ct);
            }

            var immediateExtracted = await ExtractConversationTextAsync(target, config, ct);
            log?.Info(
                $"Envelope immediate DOM extraction complete. scope={immediateExtracted.Diagnostics.CaptureScope} assistantMessages={immediateExtracted.Diagnostics.AssistantMessageCount} selectedAssistantIndex={immediateExtracted.Diagnostics.SelectedAssistantMessageIndex} selectedAssistantEnvelopeCount={immediateExtracted.Diagnostics.SelectedAssistantEnvelopeCount} assistantEnvelopeCount={immediateExtracted.Diagnostics.AssistantEnvelopeCount} bodyEnvelopeCount={immediateExtracted.Diagnostics.BodyEnvelopeCount} selectedEnvelopeCount={immediateExtracted.Diagnostics.EnvelopeCount} textLength={immediateExtracted.Diagnostics.TextLength} fallbackBody={immediateExtracted.Diagnostics.UsedBodyFallback} task_id={immediateExtracted.Diagnostics.SelectedTaskId}",
                "ChatGPT");
            if (!string.IsNullOrWhiteSpace(immediateExtracted.Text))
            {
                return new ChatGptDomCaptureResult(true, immediateExtracted.Text, immediateExtracted.Diagnostics, "DOM envelope extracted before busy/stable wait.", target.Url);
            }

            var stable = await WaitForStableResponseAsync(target, config, log, ct);
            if (!stable.Success)
            {
                return new ChatGptDomCaptureResult(false, string.Empty, null, stable.Message, target.Url);
            }

            await ScrollConversationToBottomAsync(target, ct);
            var expandedCount = await ExpandCollapsedMessagesAsync(target, ct);
            if (expandedCount > 0)
            {
                log?.Info($"Expanded {expandedCount} collapsed ChatGPT message control(s) before envelope extraction.", "ChatGPT");
                await ScrollConversationToBottomAsync(target, ct);
                var stableAfterExpand = await WaitForStableResponseAsync(target, config, log, ct);
                if (!stableAfterExpand.Success)
                {
                    return new ChatGptDomCaptureResult(false, string.Empty, null, stableAfterExpand.Message, target.Url);
                }
            }

            await ScrollConversationToBottomAsync(target, ct);
            var extracted = await ExtractConversationTextAsync(target, config, ct);
            log?.Info(
                $"Envelope DOM extraction complete. scope={extracted.Diagnostics.CaptureScope} assistantMessages={extracted.Diagnostics.AssistantMessageCount} selectedAssistantIndex={extracted.Diagnostics.SelectedAssistantMessageIndex} selectedAssistantEnvelopeCount={extracted.Diagnostics.SelectedAssistantEnvelopeCount} assistantEnvelopeCount={extracted.Diagnostics.AssistantEnvelopeCount} bodyEnvelopeCount={extracted.Diagnostics.BodyEnvelopeCount} selectedEnvelopeCount={extracted.Diagnostics.EnvelopeCount} textLength={extracted.Diagnostics.TextLength} fallbackBody={extracted.Diagnostics.UsedBodyFallback} task_id={extracted.Diagnostics.SelectedTaskId}",
                "ChatGPT");
            if (extracted.Diagnostics.UsedBodyFallback)
            {
                log?.Warning("Falling back to full body text; may include old/user envelopes.", "ChatGPT");
            }

            return new ChatGptDomCaptureResult(true, extracted.Text, extracted.Diagnostics, "Conversation text extracted.", target.Url);
        }
        catch (OperationCanceledException)
        {
            return new ChatGptDomCaptureResult(false, string.Empty, null, $"Timed out after {config.ChatGptCaptureTimeoutSeconds} seconds waiting for ChatGPT envelope capture.", state.LastChatGptTargetUrl);
        }
        catch (Exception ex)
        {
            return new ChatGptDomCaptureResult(false, string.Empty, null, $"ChatGPT envelope capture failed: {ex.Message}", state.LastChatGptTargetUrl);
        }
    }

    public async Task<ChatGptLineageCaptureResult> GetCurrentLineageSnapshotAsync(
        AppConfig config,
        CancellationToken cancellationToken,
        LogService? log = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var absoluteDeadlineUtc = DateTimeOffset.UtcNow + ChatGptAuthenticatedSnapshotService.AbsoluteDeadline;
        var stage = "target discovery";
        try
        {
            var expectedConversationId = ChatGptUrlMatcher.Parse(config.ChatGptDirectorUrl).ConversationId;
            if (string.IsNullOrWhiteSpace(expectedConversationId))
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    "The configured ChatGPT Director URL does not contain /c/{conversation_id}.",
                    ReasonCode: "CONFIGURED_CONVERSATION_ID_MISSING",
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineCts.CancelAfter(ChatGptAuthenticatedSnapshotService.AbsoluteDeadline);
            await EnsureEdgeCdpAsync(config, log, deadlineCts.Token);
            var discoveredTargets = await _cdpClient.ListTargetsAsync(config, deadlineCts.Token);
            var matchingTargets = FindConversationTargets(config, discoveredTargets);
            if (matchingTargets.Count == 0)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    "No configured ChatGPT Director tab is available.",
                    ReasonCode: "ACTIVE_CONVERSATION_TAB_MISSING",
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }
            if (matchingTargets.Count != 1)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    $"Expected exactly one matching ChatGPT conversation tab; found {matchingTargets.Count}.",
                    ReasonCode: "AMBIGUOUS_CONVERSATION_TABS",
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }
            var target = matchingTargets[0];

            stage = "active conversation readiness";
            var readinessWait = ChatGptPreWakeSnapshotService.MaximumSnapshotWait - stopwatch.Elapsed;
            if (readinessWait <= TimeSpan.Zero)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    "The active conversation readiness deadline expired during target discovery.",
                    ReasonCode: "ACTIVE_CONVERSATION_DISCOVERY_TIMEOUT",
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }
            var readiness = await ChatGptPreWakeSnapshotService.WaitForActiveConversationAsync(
                token => ObserveActiveConversationAsync(target, token),
                expectedConversationId,
                readinessWait,
                TimeSpan.FromMilliseconds(500),
                deadlineCts.Token);
            if (!readiness.Success)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    readiness.Message,
                    ReasonCode: readiness.ReasonCode,
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }

            stage = "authenticated conversation backend snapshot";
            var backendWait = ChatGptPreWakeSnapshotService.MaximumSnapshotWait - stopwatch.Elapsed;
            if (backendWait <= TimeSpan.Zero)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    "The 30-second pre-wake deadline expired before the authenticated backend snapshot started.",
                    ReasonCode: "CONVERSATION_BACKEND_SNAPSHOT_TIMEOUT",
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }
            var script = VerifiedLineageSnapshotScript.Replace("__WAKE_TOKEN__", "null", StringComparison.Ordinal);
            var value = await _cdpClient.EvaluateAsync(target, script, deadlineCts.Token);
            var parsed = ParseVerifiedLineage(value, target, string.Empty, requireResponse: false);
            if (!parsed.Success || parsed.Snapshot is null)
            {
                return parsed with { DurationMilliseconds = stopwatch.ElapsedMilliseconds };
            }
            parsed.Snapshot.AcquisitionDeadlineUtc = absoluteDeadlineUtc;

            stage = "same-tab post-acquisition revalidation";
            var postTargets = await _cdpClient.ListTargetsAsync(config, deadlineCts.Token);
            var postMatches = FindConversationTargets(config, postTargets);
            var postTarget = postMatches.Count == 1 ? postMatches[0] : null;
            var acquisition = new AuthenticatedSnapshotAcquisitionRecord
            {
                AcquisitionMethod = parsed.Snapshot.AuthenticatedAcquisitionMethod,
                MatchingTabCount = postMatches.Count,
                TargetIdBefore = parsed.Snapshot.BrowserTabIdentity,
                TargetIdAfter = postTarget?.Id ?? string.Empty,
                FrameId = parsed.Snapshot.BrowserFrameIdentity,
                UrlBefore = parsed.Snapshot.BrowserUrlBeforeAcquisition,
                UrlAfter = postTarget?.Url ?? parsed.Snapshot.BrowserUrlAfterAcquisition,
                VisibilityBefore = parsed.Snapshot.DocumentVisibilityState,
                VisibilityAfter = postTarget is null
                    ? "missing"
                    : (await ObserveActiveConversationAsync(postTarget, deadlineCts.Token)).VisibilityState,
                RequestMethod = parsed.Snapshot.AuthenticatedRequestMethod,
                EndpointPath = parsed.Snapshot.AuthenticatedEndpointPath,
                CredentialMode = parsed.Snapshot.AuthenticatedCredentialMode,
                HeaderNames = [.. parsed.Snapshot.AuthenticatedHeaderNames],
                SessionStatusCode = parsed.Snapshot.AuthenticationSessionStatusCode,
                ResponseStatusCode = parsed.Snapshot.ApiStatusCode,
                ResponseContentType = parsed.Snapshot.ResponseContentType,
                ResponseBodyAvailable = parsed.Snapshot.ResponseBodyAvailable,
                ResponseMalformed = parsed.Snapshot.ResponseMalformed,
                CacheMode = parsed.Snapshot.RequestCacheMode,
                CachedOnly = parsed.Snapshot.CachedOnly,
                StartedAtUtc = parsed.Snapshot.AcquisitionStartedAtUtc,
                CompletedAtUtc = parsed.Snapshot.AcquisitionCompletedAtUtc,
                AbsoluteDeadlineUtc = absoluteDeadlineUtc,
                CallerCompletedAtUtc = DateTimeOffset.UtcNow
            };
            var acquisitionValidation = ChatGptAuthenticatedSnapshotService.ValidateAcquisition(acquisition, expectedConversationId);
            if (!acquisitionValidation.Success)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    acquisitionValidation.Message,
                    parsed.Snapshot,
                    ReasonCode: acquisitionValidation.ReasonCode,
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }

            var validation = ChatGptPreWakeSnapshotService.ValidateSnapshot(
                parsed.Snapshot,
                expectedConversationId,
                DateTimeOffset.UtcNow,
                ChatGptPreWakeSnapshotService.MaximumSnapshotAge);
            if (!validation.Success)
            {
                return new ChatGptLineageCaptureResult(
                    false,
                    validation.Message,
                    parsed.Snapshot,
                    ReasonCode: validation.ReasonCode,
                    DurationMilliseconds: stopwatch.ElapsedMilliseconds);
            }

            log?.Info(
                $"Pre-wake snapshot verified in {stopwatch.ElapsedMilliseconds}ms: " +
                $"conversationId={parsed.Snapshot.ConversationId} currentNode={parsed.Snapshot.CurrentNode} " +
                $"currentPath={string.Join(">", parsed.Snapshot.CurrentPathMessageIds)} " +
                $"visibleActiveBranch={string.Join(">", parsed.Snapshot.VisibleActiveBranchMessageIds)} " +
                $"snapshotTimestamp={parsed.Snapshot.SnapshotTimestampUtc:O} " +
                ChatGptAuthenticatedSnapshotService.BuildRedactedDiagnostics(acquisition),
                "ChatGPT");
            return parsed with
            {
                ReasonCode = "OK",
                DurationMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var code = stage.Contains("authenticated", StringComparison.Ordinal) || stage.Contains("revalidation", StringComparison.Ordinal)
                ? "CONVERSATION_BACKEND_SNAPSHOT_TIMEOUT"
                : "ACTIVE_CONVERSATION_PROBE_TIMEOUT";
            return new ChatGptLineageCaptureResult(
                false,
                $"The 30-second pre-wake deadline expired during {stage}.",
                ReasonCode: code,
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ChatGptLineageCaptureResult(
                false,
                $"Pre-wake ChatGPT lineage verification failed during {stage}: {ex.Message}",
                ReasonCode: "PRE_WAKE_SNAPSHOT_FAILURE",
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
    }

    private static List<EdgeCdpTarget> FindConversationTargets(AppConfig config, IReadOnlyList<EdgeCdpTarget> targets) =>
        targets
            .Where(target => target.Type.Equals("page", StringComparison.OrdinalIgnoreCase))
            .Where(target => ChatGptUrlMatcher.Compare(config.ChatGptDirectorUrl, target.Url, requireSameConversation: true).IsMatch)
            .ToList();

    public async Task<ChatGptLineageCaptureResult> CaptureBoundAssistantResponseAsync(
        AppConfig config,
        WakeTransactionRecord transaction,
        CancellationToken cancellationToken,
        LogService? log = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.ChatGptCaptureTimeoutSeconds)));
        try
        {
            await EnsureEdgeCdpAsync(config, log, timeoutCts.Token);
            var target = await FindOrOpenTargetAsync(config, log, timeoutCts.Token);
            if (target is null)
            {
                return new ChatGptLineageCaptureResult(false, "No configured ChatGPT Director tab is available for the bound response.");
            }

            var stable = await WaitForStableResponseAsync(target, config, log, timeoutCts.Token);
            if (!stable.Success)
            {
                return new ChatGptLineageCaptureResult(false, stable.Message);
            }

            var script = VerifiedLineageSnapshotScript.Replace(
                "__WAKE_TOKEN__",
                JsonSerializer.Serialize(transaction.WakeToken),
                StringComparison.Ordinal);
            var value = await _cdpClient.EvaluateAsync(target, script, timeoutCts.Token);
            return ParseVerifiedLineage(value, target, transaction.WakeToken, requireResponse: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChatGptLineageCaptureResult(false, "The single bound-response capture timed out; no retry was attempted.");
        }
        catch (Exception ex)
        {
            return new ChatGptLineageCaptureResult(false, $"Bound ChatGPT response verification failed: {ex.Message}");
        }
    }

    private async Task EnsureEdgeCdpAsync(AppConfig config, LogService? log, CancellationToken cancellationToken)
    {
        log?.Info($"Checking Edge CDP endpoint http://{config.ChatGptCdpHost}:{config.ChatGptCdpPort}/json/version", "CDP");
        if (await _cdpClient.IsReachableAsync(config, cancellationToken))
        {
            log?.Info("Edge CDP endpoint reachable; reusing existing Edge CDP session.", "CDP");
            return;
        }

        log?.Warning("Edge CDP endpoint is not reachable; launching dedicated Edge profile.", "CDP");
        var edgePath = ResolveEdgeExecutable(config);
        var profileDir = Environment.ExpandEnvironmentVariables(config.EdgeUserDataDir);
        Directory.CreateDirectory(profileDir);

        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--remote-debugging-port={config.ChatGptCdpPort}");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDir}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-timer-throttling");
        startInfo.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        startInfo.ArgumentList.Add("--disable-renderer-backgrounding");
        startInfo.ArgumentList.Add("--disable-features=Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,BackForwardCache,TabFreeze");
        startInfo.ArgumentList.Add(config.ChatGptDirectorUrl);
        Process.Start(startInfo);
        log?.Info($"Edge launched with dedicated automation profile and background-throttling disabled: {profileDir}", "CDP");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, config.ChatGptSendTimeoutSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            if (await _cdpClient.IsReachableAsync(config, cancellationToken))
            {
                log?.Info("Edge CDP endpoint reachable after launch.", "CDP");
                return;
            }
        }

        throw new InvalidOperationException("Edge launched, but the CDP endpoint did not become reachable.");
    }

    private async Task<WakeMessageBinding> ResolveExactWakeMessageAsync(
        EdgeCdpTarget target,
        string wakeToken,
        CancellationToken cancellationToken)
    {
        var tokenJson = JsonSerializer.Serialize(wakeToken);
        var script = WakeMessageBindingScript.Replace("__WAKE_TOKEN__", tokenJson, StringComparison.Ordinal);
        var value = await _cdpClient.EvaluateAsync(target, script, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new WakeMessageBinding(false, string.Empty, string.Empty, null, false, "Backend wake binding returned no object.");
        }

        var success = GetBool(value.Value, "success");
        var createdAt = GetDouble(value.Value, "createdAt");
        return new WakeMessageBinding(
            success,
            GetString(value.Value, "messageId"),
            GetString(value.Value, "parentMessageId"),
            createdAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(createdAt * 1000)) : null,
            GetBool(value.Value, "onCurrentPath"),
            GetString(value.Value, "message"));
    }

    private async Task<EdgeCdpTarget?> FindOrOpenTargetAsync(
        AppConfig config,
        LogService? log,
        CancellationToken cancellationToken)
    {
        var targets = await _cdpClient.ListTargetsAsync(config, cancellationToken);
        var target = SelectTarget(config, targets, log);
        if (target is not null)
        {
            log?.Info("ChatGPT tab found.", "CDP");
            return target;
        }

        if (!config.ChatGptOpenIfMissing)
        {
            log?.Warning("ChatGPT tab not found and Open ChatGPT if missing is disabled.", "CDP");
            return null;
        }

        log?.Info("ChatGPT tab not found; opening configured Director URL.", "CDP");
        var opened = await _cdpClient.OpenTargetAsync(config, config.ChatGptDirectorUrl, cancellationToken);
        if (opened is null)
        {
            return null;
        }

        await Task.Delay(1500, cancellationToken);
        targets = await _cdpClient.ListTargetsAsync(config, cancellationToken);
        target = SelectTarget(config, targets, log) ?? opened;

        if (config.ChatGptExactUrlRequired)
        {
            var comparison = ChatGptUrlMatcher.Compare(config.ChatGptDirectorUrl, target.Url, requireSameConversation: true);
            LogUrlComparison(log, target, comparison);
            if (!comparison.IsMatch)
            {
                log?.Warning($"Opened tab is not the configured ChatGPT conversation. {comparison.Reason} Opened: {target.Url}", "CDP");
                return null;
            }
        }

        log?.Info("ChatGPT tab opened.", "CDP");
        return target;
    }

    private static EdgeCdpTarget? SelectTarget(
        AppConfig config,
        IReadOnlyList<EdgeCdpTarget> targets,
        LogService? log)
    {
        var pages = targets
            .Where(target => target.Type.Equals("page", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (config.ChatGptExactUrlRequired)
        {
            foreach (var target in pages)
            {
                var comparison = ChatGptUrlMatcher.Compare(config.ChatGptDirectorUrl, target.Url, requireSameConversation: true);
                LogUrlComparison(log, target, comparison);
                if (comparison.IsMatch)
                {
                    return target;
                }
            }

            return null;
        }

        return pages.FirstOrDefault(target =>
            ChatGptUrlMatcher.Parse(target.Url).IsChatGptHost &&
            (target.Title.Contains(config.ChatGptTitle, StringComparison.OrdinalIgnoreCase) ||
             ChatGptUrlMatcher.Compare(config.ChatGptDirectorUrl, target.Url, requireSameConversation: false).IsMatch ||
             target.Title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<BusyCheckResult> DetectBusyAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(target, BusyScript, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new BusyCheckResult(false, "Busy state could not be read.");
        }

        var busy = GetBool(value.Value, "busy");
        var reason = GetString(value.Value, "reason");
        var composerFound = GetBool(value.Value, "composerFound");
        if (!composerFound)
        {
            return new BusyCheckResult(false, "Composer not found yet; page may still be loading or require login.");
        }

        return new BusyCheckResult(busy, reason);
    }

    private async Task<ActionResult> WaitForStableResponseAsync(
        EdgeCdpTarget target,
        AppConfig config,
        LogService? log,
        CancellationToken cancellationToken)
    {
        var requiredStableFor = TimeSpan.FromSeconds(Math.Max(1, config.ChatGptStableResponseSeconds));
        var lastSignature = string.Empty;
        DateTimeOffset? stableSince = null;

        while (true)
        {
            var snapshot = await GetConversationSnapshotAsync(target, cancellationToken);
            if (snapshot.Busy)
            {
                stableSince = null;
                log?.Info($"ChatGPT busy during capture wait: {snapshot.Reason}", "ChatGPT");
            }
            else if (snapshot.Signature.Equals(lastSignature, StringComparison.Ordinal))
            {
                stableSince ??= DateTimeOffset.UtcNow;
                var stableFor = DateTimeOffset.UtcNow - stableSince.Value;
                if (stableFor >= requiredStableFor)
                {
                    return new ActionResult(true, $"Response stable for {stableFor.TotalSeconds:N0}s.");
                }
            }
            else
            {
                lastSignature = snapshot.Signature;
                stableSince = DateTimeOffset.UtcNow;
                log?.Info($"ChatGPT response text changed; waiting for {config.ChatGptStableResponseSeconds}s stable window.", "ChatGPT");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<ConversationSnapshot> GetConversationSnapshotAsync(
        EdgeCdpTarget target,
        CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(target, ConversationSnapshotScript, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new ConversationSnapshot("unknown", Busy: false, Reason: "Snapshot unavailable");
        }

        var signature = GetString(value.Value, "signature");
        var busy = GetBool(value.Value, "busy");
        var reason = GetString(value.Value, "reason");
        return new ConversationSnapshot(signature, busy, reason);
    }

    private async Task<ActiveConversationObservation> ObserveActiveConversationAsync(
        EdgeCdpTarget target,
        CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(target, ActiveConversationReadinessScript, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
            return new ActiveConversationObservation(string.Empty, "unknown", "unknown", 0);
        return new ActiveConversationObservation(
            GetString(value.Value, "conversationId"),
            GetString(value.Value, "visibilityState"),
            GetString(value.Value, "documentReadyState"),
            GetInt(value.Value, "visibleMessageCount"));
    }

    private async Task<ExtractedConversationText> ExtractConversationTextAsync(
        EdgeCdpTarget target,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var expression = EnvelopeExtractionScript.Replace(
            "__CAPTURE_SCOPE__",
            JsonSerializer.Serialize(ChatGptEnvelopeCapture.ResolveCaptureScope(config)));
        var value = await _cdpClient.EvaluateAsync(target, expression, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new ExtractedConversationText(
                string.Empty,
                new EnvelopeExtractionDiagnostics(0, 0, false, false, 0));
        }

        var text = GetString(value.Value, "text");
        var diagnostics = new EnvelopeExtractionDiagnostics(
            EnvelopeCount: GetInt(value.Value, "envelopeCount"),
            TextLength: GetInt(value.Value, "textLength"),
            FoundOpening: GetBool(value.Value, "foundOpening"),
            FoundClosing: GetBool(value.Value, "foundClosing"),
            AssistantMessageCount: GetInt(value.Value, "assistantMessageCount"),
            AssistantEnvelopeCount: GetInt(value.Value, "assistantEnvelopeCount"),
            BodyEnvelopeCount: GetInt(value.Value, "bodyEnvelopeCount"),
            UsedBodyFallback: GetBool(value.Value, "usedBodyFallback"),
            SelectedAssistantMessageIndex: GetInt(value.Value, "selectedAssistantMessageIndex"),
            SelectedAssistantEnvelopeCount: GetInt(value.Value, "selectedAssistantEnvelopeCount"),
            CaptureScope: GetString(value.Value, "captureScope"),
            SelectedTaskId: GetString(value.Value, "selectedTaskId"),
            SkippedDuplicateTaskIds: GetString(value.Value, "bodyCandidateTaskIds"));

        return new ExtractedConversationText(text, diagnostics);
    }

    private async Task<ExtractedConversationText> ExtractConversationDataTextAsync(
        EdgeCdpTarget target,
        LogService? log,
        CancellationToken cancellationToken)
    {
        JsonElement? value;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            value = await _cdpClient.EvaluateAsync(target, ConversationDataExtractionScript, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Warning("Conversation-data envelope extraction timed out after 15 seconds; falling back to DOM capture.", "ChatGPT");
            return new ExtractedConversationText(
                string.Empty,
                new EnvelopeExtractionDiagnostics(0, 0, false, false, 0, CaptureScope: "ConversationData", RejectionReason: "Conversation data script timed out after 15 seconds."));
        }
        catch (Exception ex)
        {
            log?.Warning($"Conversation-data envelope extraction failed: {ex.Message}", "ChatGPT");
            return new ExtractedConversationText(
                string.Empty,
                new EnvelopeExtractionDiagnostics(0, 0, false, false, 0, CaptureScope: "ConversationData", RejectionReason: ex.Message));
        }

        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new ExtractedConversationText(
                string.Empty,
                new EnvelopeExtractionDiagnostics(0, 0, false, false, 0, CaptureScope: "ConversationData", RejectionReason: "Conversation data script returned no object."));
        }

        var text = GetString(value.Value, "text");
        var diagnostics = new EnvelopeExtractionDiagnostics(
            EnvelopeCount: GetInt(value.Value, "envelopeCount"),
            TextLength: text.Length,
            FoundOpening: GetBool(value.Value, "foundOpening"),
            FoundClosing: GetBool(value.Value, "foundClosing"),
            AssistantMessageCount: GetInt(value.Value, "assistantMessageCount"),
            AssistantEnvelopeCount: GetInt(value.Value, "assistantEnvelopeCount"),
            BodyEnvelopeCount: 0,
            UsedBodyFallback: false,
            SelectedAssistantMessageIndex: GetInt(value.Value, "selectedAssistantMessageIndex"),
            SelectedAssistantEnvelopeCount: GetInt(value.Value, "selectedAssistantEnvelopeCount"),
            CaptureScope: "ConversationData",
            SelectedTaskId: GetString(value.Value, "selectedTaskId"),
            SkippedDuplicateTaskIds: GetString(value.Value, "candidateTaskIds"),
            RejectionReason: GetString(value.Value, "reason"));

        return new ExtractedConversationText(text, diagnostics);
    }

    private async Task<int> ExpandCollapsedMessagesAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(target, ExpandCollapsedMessagesScript, cancellationToken);
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return GetInt(value.Value, "clicked");
    }

    private async Task ScrollConversationToBottomAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        await _cdpClient.EvaluateAsync(target, ScrollConversationToBottomScript, cancellationToken);
        await Task.Delay(750, cancellationToken);
    }

    private async Task WaitForDocumentReadyAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var value = await _cdpClient.EvaluateAsync(
                    target,
                    "(() => document.readyState)()",
                    cancellationToken);
                if (value?.ValueKind == JsonValueKind.String &&
                    value.Value.GetString()?.Equals("complete", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await Task.Delay(1500, cancellationToken);
                    return;
                }
            }
            catch
            {
                // Reload can briefly invalidate the execution context; keep polling until the page is ready.
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task WaitForChatGptConversationHydratedAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var value = await _cdpClient.EvaluateAsync(
                    target,
                    """
(() => {
  const bodyText = document.body?.innerText || '';
  return {
    assistantMessages: document.querySelectorAll('[data-message-author-role="assistant"]').length,
    hasEnvelopeMarker: bodyText.includes('<<<DCS_CODEX_TASK_V1>>>'),
    textLength: bodyText.length
  };
})()
""",
                    cancellationToken);
                if (value is not null &&
                    value.Value.ValueKind == JsonValueKind.Object &&
                    (GetInt(value.Value, "assistantMessages") > 0 || GetBool(value.Value, "hasEnvelopeMarker")))
                {
                    await Task.Delay(1000, cancellationToken);
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task<int> GetAssistantMessageCountAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(
            target,
            "(() => document.querySelectorAll('[data-message-author-role=\"assistant\"]').length)()",
            cancellationToken);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var count) ? count : 0;
    }

    private async Task<ActionResult> InsertPromptAsync(
        EdgeCdpTarget target,
        string prompt,
        string token,
        CancellationToken cancellationToken)
    {
        var expression = InsertPromptScript
            .Replace("__PROMPT__", JsonSerializer.Serialize(prompt))
            .Replace("__TOKEN__", JsonSerializer.Serialize(token));
        var value = await _cdpClient.EvaluateAsync(target, expression, cancellationToken);
        return ToActionResult(value, "Prompt insertion failed.");
    }

    private async Task<ActionResult> ClickSendAsync(EdgeCdpTarget target, CancellationToken cancellationToken)
    {
        var value = await _cdpClient.EvaluateAsync(target, ClickSendScript, cancellationToken);
        return ToActionResult(value, "Send click failed.");
    }

    private async Task<ActionResult> ConfirmSendAsync(
        EdgeCdpTarget target,
        string token,
        string reportIdentifier,
        int initialAssistantCount,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, config.ChatGptSendTimeoutSeconds));
        DateTimeOffset? firstDurableCandidateAt = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var expression = ConfirmSendScript
                .Replace("__TOKEN__", JsonSerializer.Serialize(token))
                .Replace("__REPORT_IDENTIFIER__", JsonSerializer.Serialize(reportIdentifier))
                .Replace("__INITIAL_ASSISTANT_COUNT__", initialAssistantCount.ToString());
            var value = await _cdpClient.EvaluateAsync(target, expression, cancellationToken);
            var result = ToActionResult(value, "Send confirmation pending.");
            if (result.Success)
            {
                firstDurableCandidateAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - firstDurableCandidateAt.Value >= TimeSpan.FromSeconds(5))
                {
                    return new ActionResult(true, $"Durable conversation message confirmed for 5 seconds. {result.Message}");
                }
            }
            else
            {
                firstDurableCandidateAt = null;
            }

            await Task.Delay(500, cancellationToken);
        }

        return new ActionResult(false, "Timed out waiting for visible ChatGPT user message containing the wake token and report identifier.");
    }

    private static ActionResult ToActionResult(JsonElement? value, string fallback)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new ActionResult(false, fallback);
        }

        var success = GetBool(value.Value, "success") || GetBool(value.Value, "confirmed") || GetBool(value.Value, "clicked");
        var message = GetString(value.Value, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            message = GetString(value.Value, "reason");
        }

        return new ActionResult(success, string.IsNullOrWhiteSpace(message) ? fallback : message);
    }

    private static ChatGptSendResult Fail(AppState state, string token, string message)
    {
        state.LastChatGptWakeResult = message;
        return new ChatGptSendResult(false, false, false, token, message, state.LastChatGptTargetUrl);
    }

    private static string ExtractWakeToken(string prompt)
    {
        var line = prompt
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .FirstOrDefault(part => part.StartsWith("DCS_WATCHER_V2_WAKE:", StringComparison.Ordinal));

        return line?.Trim() ?? $"DCS_WATCHER_V2_WAKE:test:{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
    }

    private static string ExtractReportIdentifier(string prompt)
    {
        var lines = prompt.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].Trim().Equals("Source file:", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(lines[index + 1].Trim());
            }
        }

        return lines.FirstOrDefault(line =>
                   line.Contains("CGPT-REPORT-", StringComparison.OrdinalIgnoreCase) &&
                   line.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
               ?.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
               .LastOrDefault()
               ?.Trim() ?? string.Empty;
    }

    private static string ResolveEdgeExecutable(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.EdgeExecutablePath) && File.Exists(config.EdgeExecutablePath))
        {
            return config.EdgeExecutablePath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };

        var pathCandidate = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "msedge.exe"))
            .FirstOrDefault(File.Exists);

        return candidates.FirstOrDefault(File.Exists)
            ?? pathCandidate
            ?? throw new FileNotFoundException("Could not locate msedge.exe. Set EdgeExecutablePath in config.");
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.True;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private static ChatGptLineageCaptureResult ParseVerifiedLineage(
        JsonElement? value,
        EdgeCdpTarget target,
        string wakeToken,
        bool requireResponse)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return new ChatGptLineageCaptureResult(false, "ChatGPT backend lineage script returned no object.", ReasonCode: "CONVERSATION_BACKEND_NO_OBJECT");
        }

        var root = value.Value;
        if (!GetBool(root, "success"))
        {
            var reasonCode = GetString(root, "reasonCode");
            return new ChatGptLineageCaptureResult(
                false,
                GetString(root, "message"),
                ReasonCode: string.IsNullOrWhiteSpace(reasonCode) ? "CONVERSATION_BACKEND_REJECTED" : reasonCode);
        }

        var snapshot = new ConversationLineageSnapshot
        {
            ConversationId = GetString(root, "conversationId"),
            CurrentNode = GetString(root, "currentNode"),
            BrowserTabIdentity = target.Id,
            BrowserFrameIdentity = target.Id,
            BrowserUrlBeforeAcquisition = GetString(root, "initialUrl"),
            BrowserUrlAfterAcquisition = GetString(root, "finalUrl"),
            AuthenticatedAcquisitionMethod = GetString(root, "acquisitionMethod"),
            AuthenticatedRequestMethod = GetString(root, "requestMethod"),
            AuthenticatedEndpointPath = GetString(root, "endpointPath"),
            AuthenticatedCredentialMode = GetString(root, "credentialMode"),
            AuthenticationSessionStatusCode = GetInt(root, "sessionStatusCode"),
            ResponseContentType = GetString(root, "responseContentType"),
            ResponseBodyAvailable = GetBool(root, "responseBodyAvailable"),
            ResponseMalformed = GetBool(root, "responseMalformed"),
            RequestCacheMode = GetString(root, "requestCacheMode"),
            CachedOnly = GetBool(root, "cachedOnly"),
            AcquisitionStartedAtUtc = DateTimeOffset.TryParse(GetString(root, "acquisitionStartedAtUtc"), out var acquisitionStarted)
                ? acquisitionStarted.ToUniversalTime()
                : default,
            AcquisitionCompletedAtUtc = DateTimeOffset.TryParse(GetString(root, "acquisitionCompletedAtUtc"), out var acquisitionCompleted)
                ? acquisitionCompleted.ToUniversalTime()
                : default,
            BackendResponseTimestampUtc = DateTimeOffset.TryParse(GetString(root, "backendResponseTimestampUtc"), out var backendResponseTimestamp)
                ? backendResponseTimestamp.ToUniversalTime()
                : null,
            SnapshotTimestampUtc = DateTimeOffset.TryParse(GetString(root, "snapshotTimestampUtc"), out var snapshotTimestamp)
                ? snapshotTimestamp.ToUniversalTime()
                : default,
            DocumentVisibilityState = GetString(root, "initialVisibilityState"),
            ApiVerified = true,
            ApiStatusCode = GetInt(root, "apiStatusCode"),
            BrowserBackendAgree = GetBool(root, "browserBackendAgree")
        };
        snapshot.AuthenticatedHeaderNames = ReadStringArray(root, "headerNames");
        if (root.TryGetProperty("browserVisibleMessageIds", out var visible) && visible.ValueKind == JsonValueKind.Array)
        {
            snapshot.BrowserVisibleMessageIds = visible.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToList();
        }
        snapshot.CurrentPathMessageIds = ReadStringArray(root, "currentPathMessageIds");
        snapshot.VisibleActiveBranchMessageIds = ReadStringArray(root, "visibleActiveBranchMessageIds");
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nodes.EnumerateArray())
            {
                var id = GetString(item, "messageId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var node = new ConversationNodeRecord
                {
                    MessageId = id,
                    ParentMessageId = GetString(item, "parentMessageId"),
                    Role = GetString(item, "role"),
                    ContentType = GetString(item, "contentType"),
                    Content = GetString(item, "content"),
                    Complete = GetBool(item, "complete"),
                    IsVisuallyHidden = GetBool(item, "isVisuallyHidden"),
                    IsTemporalTurn = GetBool(item, "isTemporalTurn"),
                    RebaseSystemMessage = GetBool(item, "rebaseSystemMessage"),
                    RebaseDeveloperMessage = GetBool(item, "rebaseDeveloperMessage"),
                    RequestId = GetString(item, "requestId"),
                    TurnExchangeId = GetString(item, "turnExchangeId")
                };
                if (item.TryGetProperty("childMessageIds", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    node.ChildMessageIds = children.EnumerateArray()
                        .Where(child => child.ValueKind == JsonValueKind.String)
                        .Select(child => child.GetString() ?? string.Empty)
                        .Where(child => child.Length > 0)
                        .ToList();
                }
                var createdAt = GetDouble(item, "createdAt");
                node.CreatedAtUtc = createdAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(createdAt * 1000))
                    : null;
                snapshot.Nodes[id] = node;
            }
        }

        AssistantResponseObservation? response = null;
        if (root.TryGetProperty("response", out var responseElement) && responseElement.ValueKind == JsonValueKind.Object)
        {
            var createdAt = GetDouble(responseElement, "createdAt");
            response = new AssistantResponseObservation
            {
                MessageId = GetString(responseElement, "messageId"),
                ParentMessageId = GetString(responseElement, "parentMessageId"),
                Role = GetString(responseElement, "role"),
                Content = GetString(responseElement, "content"),
                Complete = GetBool(responseElement, "complete"),
                OnCurrentPath = GetBool(responseElement, "onCurrentPath"),
                WakeToken = wakeToken,
                SourceReport = GetString(responseElement, "sourceReport"),
                CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
                FallbackBody = false,
                ApiVerified = true,
                SelectedAssistantIndex = GetInt(responseElement, "selectedAssistantIndex"),
                AssistantSelectionAmbiguous = GetBool(responseElement, "assistantSelectionAmbiguous"),
                WholePageCaptureUsed = false,
                CurrentNodeAtCapture = snapshot.CurrentNode,
                CreatedAtUtc = createdAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(createdAt * 1000))
                    : null
            };
        }

        if (requireResponse && response is null)
        {
            return new ChatGptLineageCaptureResult(false, "The exact wake has no single complete current-path assistant response.", snapshot, ReasonCode: "BOUND_RESPONSE_MISSING");
        }
        return new ChatGptLineageCaptureResult(true, GetString(root, "message"), snapshot, response, "OK");
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToList()
            : [];
    }

    private static void LogUrlComparison(LogService? log, EdgeCdpTarget target, ChatGptUrlMatchResult comparison)
    {
        if (log is null)
        {
            return;
        }

        log.Info(
            "ChatGPT URL comparison: " +
            $"configuredConversationId={ValueOrNone(comparison.Configured.ConversationId)} " +
            $"tabConversationId={ValueOrNone(comparison.Tab.ConversationId)} " +
            $"configuredGptIdBase={ValueOrNone(comparison.Configured.GptBaseId)} " +
            $"tabGptIdBase={ValueOrNone(comparison.Tab.GptBaseId)} " +
            $"matchResult={comparison.IsMatch} reason={comparison.Reason} tabTitle={target.Title}",
            "CDP");
    }

    private static string ValueOrNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private sealed record BusyCheckResult(bool IsBusy, string Reason);

    private sealed record ActionResult(bool Success, string Message);

    private sealed record ConversationSnapshot(string Signature, bool Busy, string Reason);

    private sealed record ExtractedConversationText(string Text, EnvelopeExtractionDiagnostics Diagnostics);

    private sealed record WakeMessageBinding(
        bool Success,
        string MessageId,
        string ParentMessageId,
        DateTimeOffset? CreatedAtUtc,
        bool OnCurrentPath,
        string Message);

    private const string BusyScript = """
(() => {
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const label = (el) => `${el.getAttribute('aria-label') || ''} ${el.textContent || ''} ${el.getAttribute('data-testid') || ''}`.toLowerCase();
  const buttons = Array.from(document.querySelectorAll('button')).filter(visible);
  const stopButton = buttons.find((button) => /stop streaming|stop generating|stop responding|stop\b/.test(label(button)));
  const composer = document.querySelector('textarea[data-testid="prompt-textarea"], textarea#prompt-textarea, [contenteditable="true"][data-testid="prompt-textarea"], div#prompt-textarea[contenteditable="true"], [contenteditable="true"][role="textbox"], textarea, [contenteditable="true"]');
  const composerDisabled = !!composer && (
    composer.disabled ||
    composer.getAttribute('aria-disabled') === 'true' ||
    !!composer.closest('[aria-disabled="true"]')
  );
  return {
    busy: !!stopButton || composerDisabled,
    reason: stopButton ? 'stop control visible' : composerDisabled ? 'composer disabled' : 'no busy indicator',
    composerFound: !!composer
  };
})()
""";

    private const string InsertPromptScript = """
(() => {
  const prompt = __PROMPT__;
  const token = __TOKEN__;
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const candidates = [
    document.querySelector('textarea[data-testid="prompt-textarea"]'),
    document.querySelector('textarea#prompt-textarea'),
    document.querySelector('textarea[placeholder*="Message"]'),
    document.querySelector('[contenteditable="true"][data-testid="prompt-textarea"]'),
    document.querySelector('div#prompt-textarea[contenteditable="true"]'),
    document.querySelector('[contenteditable="true"][role="textbox"]'),
    ...Array.from(document.querySelectorAll('textarea, [contenteditable="true"]'))
  ].filter(Boolean);
  const composer = candidates.find(visible) || candidates[0];
  if (!composer) return { success: false, message: 'Composer not found.' };
  composer.focus();
  if (composer.tagName === 'TEXTAREA' || composer.tagName === 'INPUT') {
    const proto = composer.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(composer, prompt);
    else composer.value = prompt;
    composer.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: prompt }));
    composer.dispatchEvent(new Event('change', { bubbles: true }));
  } else {
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(composer);
    selection.removeAllRanges();
    selection.addRange(range);
    const inserted = document.execCommand && document.execCommand('insertText', false, prompt);
    const current = composer.innerText || composer.textContent || '';
    if (!inserted || !current.includes(token)) {
      composer.textContent = prompt;
      composer.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: prompt }));
    }
  }
  const text = composer.value || composer.innerText || composer.textContent || '';
  return { success: text.includes(token), message: text.includes(token) ? 'Composer contains wake token.' : 'Composer text did not contain wake token.' };
})()
""";

    private const string ClickSendScript = """
(() => {
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const label = (el) => `${el.getAttribute('aria-label') || ''} ${el.textContent || ''} ${el.getAttribute('data-testid') || ''}`.toLowerCase();
  const composer = document.querySelector('textarea[data-testid="prompt-textarea"], textarea#prompt-textarea, [contenteditable="true"][data-testid="prompt-textarea"], div#prompt-textarea[contenteditable="true"], [contenteditable="true"][role="textbox"], textarea, [contenteditable="true"]');
  const composerRect = composer?.getBoundingClientRect();
  const buttons = Array.from(document.querySelectorAll('button')).filter((button) => visible(button) && !button.disabled && button.getAttribute('aria-disabled') !== 'true');
  let button = buttons.find((candidate) => /send/.test(label(candidate)));
  if (!button && composerRect) {
    button = buttons
      .map((candidate) => ({ candidate, rect: candidate.getBoundingClientRect() }))
      .filter((entry) => entry.rect.top >= composerRect.top - 80 && entry.rect.left >= composerRect.left)
      .sort((a, b) => Math.abs(a.rect.right - composerRect.right) - Math.abs(b.rect.right - composerRect.right))[0]?.candidate;
  }
  if (!button) return { clicked: false, message: 'Enabled send button not found.' };
  button.click();
  return { clicked: true, message: `Clicked send candidate: ${label(button).trim()}` };
})()
""";

    private const string ConfirmSendScript = """
(() => {
  const token = __TOKEN__;
  const reportIdentifier = __REPORT_IDENTIFIER__;
  const initialAssistantCount = __INITIAL_ASSISTANT_COUNT__;
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const label = (el) => `${el.getAttribute('aria-label') || ''} ${el.textContent || ''} ${el.getAttribute('data-testid') || ''}`.toLowerCase();
  const composer = document.querySelector('textarea[data-testid="prompt-textarea"], textarea#prompt-textarea, [contenteditable="true"][data-testid="prompt-textarea"], div#prompt-textarea[contenteditable="true"], [contenteditable="true"][role="textbox"], textarea, [contenteditable="true"]');
  const composerText = composer ? (composer.value || composer.innerText || composer.textContent || '') : '';
  const tokenInComposer = composerText.includes(token);
  const stopVisible = Array.from(document.querySelectorAll('button')).filter(visible).some((button) => /stop streaming|stop generating|stop responding|stop\b/.test(label(button)));
  const assistantCount = document.querySelectorAll('[data-message-author-role="assistant"]').length;
  const conversationRoot = document.querySelector('main') || document.body;
  const messageNodes = Array.from(conversationRoot.querySelectorAll('[data-message-author-role="user"], div, article'))
    .filter((node) => {
      if (!visible(node) || (composer && (node === composer || node.contains(composer)))) return false;
      const text = node.innerText || node.textContent || '';
      if (!text.includes(token)) return false;
      return !Array.from(node.children).some((child) => {
        if (composer && (child === composer || child.contains(composer))) return false;
        return (child.innerText || child.textContent || '').includes(token);
      });
    });
  const userText = messageNodes.map((node) => node.innerText || node.textContent || '').join('\n\n');
  const tokenInUserMessage = userText.includes(token);
  const reportInUserMessage = !reportIdentifier || userText.includes(reportIdentifier);
  const confirmed = !tokenInComposer && tokenInUserMessage && reportInUserMessage;
  return {
    confirmed,
    success: confirmed,
    message: confirmed
      ? `confirmed messageNodes=${messageNodes.length} tokenInComposer=${tokenInComposer} tokenInUserMessage=${tokenInUserMessage} reportInUserMessage=${reportInUserMessage} stopVisible=${stopVisible} assistantCount=${assistantCount}`
      : `waiting messageNodes=${messageNodes.length} tokenInComposer=${tokenInComposer} tokenInUserMessage=${tokenInUserMessage} reportInUserMessage=${reportInUserMessage} stopVisible=${stopVisible} assistantCount=${assistantCount}`
  };
})()
""";

    private const string ConversationSnapshotScript = """
(() => {
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const label = (el) => `${el.getAttribute('aria-label') || ''} ${el.textContent || ''} ${el.getAttribute('data-testid') || ''}`.toLowerCase();
  const buttons = Array.from(document.querySelectorAll('button')).filter(visible);
  const stopButton = buttons.find((button) => /stop streaming|stop generating|stop responding|stop\b/.test(label(button)));
  const assistantContainers = Array.from(document.querySelectorAll('[data-message-author-role="assistant"], article, .markdown, .prose')).filter(visible);
  const text = assistantContainers.map((node) => node.innerText || node.textContent || '').join('\n\n') || document.body.innerText || '';
  let hash = 0;
  for (let i = 0; i < text.length; i++) hash = ((hash << 5) - hash + text.charCodeAt(i)) | 0;
  return {
    signature: `${text.length}:${hash}:${assistantContainers.length}`,
    busy: !!stopButton,
    reason: stopButton ? 'stop control visible' : 'not busy'
  };
})()
""";

    private const string WakeMessageBindingScript = """
(async () => {
  const wakeToken = __WAKE_TOKEN__;
  const conversationMatch = location.pathname.match(/\/c\/([^/?#]+)/i);
  if (!conversationMatch) return { success: false, message: 'Conversation ID is missing from URL.' };
  const initialUrl = location.href;
  if (document.visibilityState !== 'visible') return { success: false, message: 'The selected ChatGPT conversation is not visible.' };
  const sessionResponse = await fetch('/api/auth/session', { credentials: 'include', cache: 'no-store' });
  if (!sessionResponse.ok) return { success: false, message: `Authentication session returned HTTP ${sessionResponse.status}.` };
  let session = await sessionResponse.json();
  let accessToken = session && typeof session.accessToken === 'string' ? session.accessToken : '';
  session = null;
  if (!accessToken) return { success: false, message: 'The authenticated browser session did not provide an access token.' };
  const response = await fetch(`/backend-api/conversation/${conversationMatch[1]}`, {
    method: 'GET', credentials: 'include', cache: 'no-store', headers: { accept: 'application/json', authorization: `Bearer ${accessToken}` }
  });
  accessToken = '';
  if (!response.ok) return { success: false, message: `Conversation API returned HTTP ${response.status}.` };
  if (location.href !== initialUrl || document.visibilityState !== 'visible') return { success: false, message: 'The selected ChatGPT conversation changed during wake binding.' };
  const data = await response.json();
  const mapping = data.mapping && typeof data.mapping === 'object' ? data.mapping : {};
  const messageText = (message) => {
    const parts = message && message.content && message.content.parts;
    return Array.isArray(parts) ? parts.filter((part) => typeof part === 'string').join('\n') : '';
  };
  const matches = Object.entries(mapping).filter(([id, node]) => {
    const message = node && node.message;
    return message && message.author && message.author.role === 'user' && messageText(message).includes(wakeToken);
  });
  if (matches.length !== 1) return { success: false, message: `Expected exactly one backend wake message; found ${matches.length}.` };
  const [messageId, node] = matches[0];
  const currentPath = new Set();
  let current = data.current_node || '';
  while (current && !currentPath.has(current)) {
    currentPath.add(current);
    current = mapping[current] && mapping[current].parent ? mapping[current].parent : '';
  }
  return {
    success: true,
    messageId,
    parentMessageId: node.parent || '',
    createdAt: Number(node.message && node.message.create_time || 0),
    onCurrentPath: currentPath.has(messageId),
    message: 'Exact backend wake message bound.'
  };
})()
""";

    private const string ActiveConversationReadinessScript = """
(() => {
  const conversationMatch = location.pathname.match(/\/c\/([^/?#]+)/i);
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const visibleMessageCount = Array.from(document.querySelectorAll('[data-message-author-role], article')).filter(visible).length;
  return {
    conversationId: conversationMatch ? conversationMatch[1] : '',
    visibilityState: document.visibilityState || 'unknown',
    documentReadyState: document.readyState || 'unknown',
    visibleMessageCount
  };
})()
""";

    private const string VerifiedLineageSnapshotScript = """
(async () => {
  const wakeToken = __WAKE_TOKEN__;
  const conversationMatch = location.pathname.match(/\/c\/([^/?#]+)/i);
  if (!conversationMatch) return { success: false, reasonCode: 'CONVERSATION_ID_MISSING', message: 'Conversation ID is missing from the visible ChatGPT URL.' };
  const conversationId = conversationMatch[1];
  const initialUrl = location.href;
  const initialVisibilityState = document.visibilityState || 'unknown';
  const acquisitionStartedAtUtc = new Date().toISOString();
  if (initialVisibilityState !== 'visible') return { success: false, reasonCode: 'ACTIVE_CONVERSATION_NOT_VISIBLE', message: `The intended ChatGPT conversation tab is ${initialVisibilityState}.` };
  let response;
  let data;
  let sessionStatusCode = 0;
  let responseContentType = '';
  let responseBodyAvailable = false;
  let responseMalformed = false;
  let backendResponseTimestampUtc = '';
  let cachedOnly = false;
  try {
    const sessionResponse = await fetch('/api/auth/session', { credentials: 'include', cache: 'no-store' });
    sessionStatusCode = sessionResponse.status;
    if (!sessionResponse.ok) return { success: false, reasonCode: `AUTH_SESSION_HTTP_${sessionResponse.status}`, message: `Browser-managed authentication session returned HTTP ${sessionResponse.status}.` };
    let session = await sessionResponse.json();
    let accessToken = session && typeof session.accessToken === 'string' ? session.accessToken : '';
    session = null;
    if (!accessToken) return { success: false, reasonCode: 'AUTH_SESSION_ACCESS_TOKEN_MISSING', message: 'The authenticated browser session did not provide an access token.' };
    response = await fetch(`/backend-api/conversation/${conversationId}`, {
      method: 'GET', credentials: 'include', cache: 'no-store', headers: { accept: 'application/json', authorization: `Bearer ${accessToken}` }
    });
    accessToken = '';
    backendResponseTimestampUtc = new Date().toISOString();
    responseContentType = response.headers.get('content-type') || '';
    if (!response.ok) return { success: false, reasonCode: `CONVERSATION_BACKEND_HTTP_${response.status}`, message: `Conversation API returned HTTP ${response.status}.` };
    const responseText = await response.text();
    responseBodyAvailable = responseText.length > 0;
    try { data = JSON.parse(responseText); }
    catch { responseMalformed = true; return { success: false, reasonCode: 'CONVERSATION_BACKEND_RESPONSE_MALFORMED', message: 'Authenticated conversation response was malformed.' }; }
    const resourceEntries = performance.getEntriesByName(response.url).filter((entry) => entry.entryType === 'resource');
    const resourceEntry = resourceEntries.length ? resourceEntries[resourceEntries.length - 1] : null;
    cachedOnly = !!resourceEntry && resourceEntry.transferSize === 0 && resourceEntry.encodedBodySize > 0;
  } catch (error) {
    return {
      success: false,
      reasonCode: 'CONVERSATION_BACKEND_FETCH_FAILED',
      message: `Authenticated conversation backend fetch failed: ${error && error.message ? error.message : String(error)}`
    };
  }
  const finalUrl = location.href;
  const finalVisibilityState = document.visibilityState || 'unknown';
  if (finalUrl !== initialUrl) return { success: false, reasonCode: 'NAVIGATION_DURING_ACQUISITION', message: 'The selected ChatGPT tab navigated during authenticated snapshot acquisition.' };
  if (finalVisibilityState !== 'visible') return { success: false, reasonCode: 'TAB_BECAME_HIDDEN_DURING_ACQUISITION', message: `The intended ChatGPT conversation tab became ${finalVisibilityState} during acquisition.` };
  if (!responseBodyAvailable) return { success: false, reasonCode: 'CONVERSATION_BACKEND_BODY_UNAVAILABLE', message: 'The authenticated conversation response body was unavailable.' };
  if (cachedOnly) return { success: false, reasonCode: 'CONVERSATION_BACKEND_CACHED_ONLY', message: 'The lineage response was available only from cache.' };
  const acquisitionCompletedAtUtc = new Date().toISOString();
  const acquisition = {
    acquisitionMethod: 'in-page-authenticated-request',
    initialUrl,
    finalUrl,
    initialVisibilityState,
    finalVisibilityState,
    requestMethod: 'GET',
    endpointPath: `/backend-api/conversation/${conversationId}`,
    credentialMode: 'include',
    headerNames: ['authorization'],
    sessionStatusCode,
    responseContentType,
    responseBodyAvailable,
    responseMalformed,
    requestCacheMode: 'no-store',
    cachedOnly,
    acquisitionStartedAtUtc,
    acquisitionCompletedAtUtc,
    backendResponseTimestampUtc
  };
  const mapping = data.mapping && typeof data.mapping === 'object' ? data.mapping : {};
  const currentNode = data.current_node || '';
  const stringifyPart = (part) => {
    if (part == null) return '';
    if (typeof part === 'string') return part;
    if (typeof part.text === 'string') return part.text;
    if (typeof part.content === 'string') return part.content;
    if (Array.isArray(part.parts)) return part.parts.map(stringifyPart).join('\n');
    return '';
  };
  const messageText = (message) => {
    const content = message && message.content;
    if (!content) return '';
    if (typeof content.text === 'string') return content.text;
    if (Array.isArray(content.parts)) return content.parts.map(stringifyPart).join('\n');
    return stringifyPart(content);
  };
  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const visibleMessages = Array.from(document.querySelectorAll('[data-message-author-role], article'))
    .filter(visible)
    .map((el) => ({ role: el.getAttribute('data-message-author-role') || '', text: normalize(el.innerText || el.textContent || '') }));
  const contentVisible = (content, role, requiredToken = '') => {
    const normalized = normalize(content);
    if (!normalized) return false;
    const head = normalized.slice(0, Math.min(96, normalized.length));
    const tail = normalized.slice(Math.max(0, normalized.length - 96));
    return visibleMessages.some((item) =>
      (!role || !item.role || item.role === role) &&
      (!requiredToken || item.text.includes(requiredToken)) &&
      item.text.includes(head) && item.text.includes(tail));
  };
  const currentPath = [];
  const currentSet = new Set();
  let cursor = currentNode;
  while (cursor && !currentSet.has(cursor)) {
    currentSet.add(cursor);
    currentPath.push(cursor);
    cursor = mapping[cursor] && mapping[cursor].parent ? mapping[cursor].parent : '';
  }
  const nodes = Object.entries(mapping).map(([messageId, node]) => {
    const message = node && node.message;
    const status = message && message.status || '';
    const metadata = message && message.metadata || {};
    return {
      messageId,
      parentMessageId: node && node.parent || '',
      childMessageIds: Array.isArray(node && node.children) ? node.children : [],
      role: message && message.author && message.author.role || '',
      contentType: message && message.content && message.content.content_type || '',
      content: messageText(message),
      complete: status !== 'in_progress' && status !== 'streaming',
      createdAt: Number(message && message.create_time || 0),
      isVisuallyHidden: metadata.is_visually_hidden_from_conversation === true,
      isTemporalTurn: metadata.is_temporal_turn === true,
      rebaseSystemMessage: metadata.rebase_system_message === true,
      rebaseDeveloperMessage: metadata.rebase_developer_message === true,
      requestId: typeof metadata.request_id === 'string' ? metadata.request_id : '',
      turnExchangeId: typeof metadata.turn_exchange_id === 'string' ? metadata.turn_exchange_id : ''
    };
  });
  const byId = new Map(nodes.map((node) => [node.messageId, node]));
  const current = byId.get(currentNode);
  const currentVisible = !!current && contentVisible(current.content, current.role);
  const orderedCurrentPath = currentPath.slice().reverse();
  const visibleActiveBranch = currentVisible && document.visibilityState === 'visible' ? orderedCurrentPath : [];
  const browserVisibleMessageIds = currentVisible ? [currentNode] : [];
  let responseRecord = null;
  let exactWake = null;
  if (wakeToken) {
    const wakeMatches = nodes.filter((node) => node.role === 'user' && node.content.includes(wakeToken));
    if (wakeMatches.length !== 1) return { success: false, message: `Expected exactly one backend wake message; found ${wakeMatches.length}.` };
    exactWake = wakeMatches[0];
    if (exactWake.childMessageIds.length !== 1) return { success: false, message: `Expected one backend child of the wake; found ${exactWake.childMessageIds.length}.` };
    const responseChain = [];
    const isPlatformRebaseNode = (node) => node &&
      node.role === 'system' && node.contentType === 'text' && !normalize(node.content) && node.complete &&
      node.isVisuallyHidden && node.isTemporalTurn &&
      (node.rebaseSystemMessage !== node.rebaseDeveloperMessage) &&
      !!node.requestId && !!node.turnExchangeId;
    let chainNode = byId.get(exactWake.childMessageIds[0]);
    while (chainNode) {
      if (chainNode.role !== 'assistant' && !isPlatformRebaseNode(chainNode)) {
        return { success: false, message: 'The wake response chain contains an unauthenticated non-assistant node.' };
      }
      responseChain.push(chainNode);
      if (chainNode.messageId === currentNode) break;
      if (chainNode.childMessageIds.length !== 1) return { success: false, message: 'The wake response chain is branched or incomplete.' };
      chainNode = byId.get(chainNode.childMessageIds[0]);
    }
    if (!chainNode || chainNode.messageId !== currentNode) return { success: false, message: 'The wake response chain does not reach current_node.' };
    const assistantCandidates = responseChain.filter((node) => node.role === 'assistant' &&
      node.content.includes('<<<DCS_CODEX_TASK_V1>>>') &&
      node.content.includes('<<<END_DCS_CODEX_TASK_V1>>>'));
    if (assistantCandidates.length !== 1) return { success: false, message: `Expected exactly one envelope-bearing assistant in the current response chain; found ${assistantCandidates.length}.` };
    const assistant = assistantCandidates[0];
    const intermediateNodes = responseChain.slice(0, responseChain.indexOf(assistant));
    const allowedInternalTypes = new Set(['model_editable_context', 'reasoning_recap', 'thoughts']);
    const identifiersMatchTurn = (node) => node.requestId === exactWake.requestId &&
      node.requestId === assistant.requestId && node.turnExchangeId === exactWake.turnExchangeId &&
      node.turnExchangeId === assistant.turnExchangeId;
    if (intermediateNodes.some((node) =>
      node.role === 'assistant'
        ? (!allowedInternalTypes.has(node.contentType) || contentVisible(node.content, 'assistant'))
        : (!isPlatformRebaseNode(node) || !identifiersMatchTurn(node)))) {
      return { success: false, message: 'The wake response chain contains an actionable or visible intermediate assistant node.' };
    }
    const wakeVisible = contentVisible(exactWake.content, 'user', wakeToken);
    const assistantVisible = contentVisible(assistant.content, 'assistant');
    if (wakeVisible) browserVisibleMessageIds.push(exactWake.messageId);
    if (assistantVisible) browserVisibleMessageIds.push(assistant.messageId);
    const assistantsOnPath = currentPath.slice().reverse().map((id) => byId.get(id)).filter((node) => node && node.role === 'assistant');
    const selectedIndex = assistantsOnPath.findIndex((node) => node.messageId === assistant.messageId);
    const sourceReport = (assistant.content.match(/(?:^|\n)\s*source_report\s*:\s*([^\r\n]+)/i) || [,''])[1].trim();
    responseRecord = {
      messageId: assistant.messageId,
      parentMessageId: exactWake.messageId,
      role: assistant.role,
      content: assistant.content,
      complete: assistant.complete,
      onCurrentPath: currentSet.has(assistant.messageId),
      sourceReport,
      selectedAssistantIndex: selectedIndex,
      assistantSelectionAmbiguous: selectedIndex < 0,
      createdAt: assistant.createdAt
    };
    const agree = document.visibilityState === 'visible' && currentNode === assistant.messageId && currentSet.has(exactWake.messageId) && wakeVisible && assistantVisible;
    return {
      ...acquisition,
      success: true,
      message: agree ? 'Exact wake and assistant response are backend-verified on the visible current path.' : 'Browser and backend current-path state do not agree.',
      conversationId,
      currentNode,
      apiStatusCode: response.status,
      browserBackendAgree: agree,
      snapshotTimestampUtc: new Date().toISOString(),
      documentVisibilityState: document.visibilityState || 'unknown',
      currentPathMessageIds: orderedCurrentPath,
      visibleActiveBranchMessageIds: visibleActiveBranch,
      browserVisibleMessageIds: Array.from(new Set(browserVisibleMessageIds)),
      nodes,
      response: responseRecord
    };
  }
  return {
    ...acquisition,
    success: true,
    message: currentVisible ? 'Current ChatGPT branch is backend-verified and browser-visible.' : 'Backend current_node is not browser-visible.',
    conversationId,
    currentNode,
    apiStatusCode: response.status,
    browserBackendAgree: currentVisible && document.visibilityState === 'visible',
    snapshotTimestampUtc: new Date().toISOString(),
    documentVisibilityState: document.visibilityState || 'unknown',
    currentPathMessageIds: orderedCurrentPath,
    visibleActiveBranchMessageIds: visibleActiveBranch,
    browserVisibleMessageIds: Array.from(new Set(browserVisibleMessageIds)),
    nodes
  };
})()
""";

    private const string ConversationDataExtractionScript = """
(async () => {
  const openMarker = '<<<DCS_CODEX_TASK_V1>>>';
  const closeMarker = '<<<END_DCS_CODEX_TASK_V1>>>';
  const conversationMatch = location.pathname.match(/\/c\/([^/?#]+)/i);
  if (!conversationMatch) {
    return { text: '', reason: 'No /c/{conversation_id} was found in the ChatGPT URL.' };
  }

  const conversationId = conversationMatch[1];
  const endpoints = [
    `/backend-api/conversation/${conversationId}`,
    `/backend-api/conversation/${conversationId}?tree_format=true`
  ];

  let data = null;
  let lastError = '';
  for (const endpoint of endpoints) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 7000);
    try {
      const response = await fetch(endpoint, {
        credentials: 'include',
        cache: 'no-store',
        headers: { accept: 'application/json' },
        signal: controller.signal
      });
      if (!response.ok) {
        lastError = `${endpoint} returned HTTP ${response.status}`;
        continue;
      }

      data = await response.json();
      break;
    } catch (error) {
      lastError = `${endpoint} failed: ${error && error.name === 'AbortError' ? 'timed out after 7 seconds' : error && error.message ? error.message : String(error)}`;
    } finally {
      clearTimeout(timeout);
    }
  }

  if (!data) {
    return { text: '', reason: lastError || 'ChatGPT conversation endpoint returned no data.' };
  }

  const stringifyPart = (part) => {
    if (part == null) return '';
    if (typeof part === 'string') return part;
    if (typeof part.text === 'string') return part.text;
    if (typeof part.content === 'string') return part.content;
    if (Array.isArray(part.parts)) return part.parts.map(stringifyPart).join('\n');
    try {
      return JSON.stringify(part);
    } catch {
      return String(part);
    }
  };

  const messageText = (message) => {
    const content = message && message.content;
    if (!content) return '';
    if (typeof content.text === 'string') return content.text;
    if (typeof content.result === 'string') return content.result;
    if (Array.isArray(content.parts)) return content.parts.map(stringifyPart).join('\n');
    if (Array.isArray(content.content)) return content.content.map(stringifyPart).join('\n');
    return stringifyPart(content);
  };

  const values = data.mapping && typeof data.mapping === 'object'
    ? Object.values(data.mapping)
    : Array.isArray(data.messages)
      ? data.messages.map((message) => ({ message }))
      : [];

  const assistantMessages = [];
  values.forEach((node, index) => {
    const message = node && node.message ? node.message : node;
    const role = message && message.author && message.author.role;
    if (role !== 'assistant') return;
    const text = messageText(message);
    if (!text) return;
    assistantMessages.push({
      index,
      createTime: Number(message.create_time || message.update_time || 0),
      text
    });
  });

  const extractEnvelopes = (text) => {
    const envelopes = [];
    let search = 0;
    while (search < text.length) {
      const open = text.indexOf(openMarker, search);
      if (open < 0) break;
      const close = text.indexOf(closeMarker, open + openMarker.length);
      if (close < 0) {
        envelopes.push({ text: text.slice(open), complete: false });
        break;
      }

      const end = close + closeMarker.length;
      envelopes.push({ text: text.slice(open, end), complete: true });
      search = end;
    }

    return envelopes;
  };

  const readField = (text, name) => {
    const lineMatch = text.match(new RegExp(`(?:^|\\n)\\s*${name}\\s*:\\s*(.+)`, 'i'));
    if (lineMatch) return lineMatch[1].trim();
    const fieldNames = ['task_id', 'origin', 'repo', 'target', 'mode', 'created_at', 'source_report'];
    const headerEnd = text.indexOf('BEGIN_INSTRUCTION');
    const header = (headerEnd >= 0 ? text.slice(0, headerEnd) : text).replace(openMarker, ' ');
    const pattern = new RegExp(`(?:^|\\s)${name}\\s*:\\s*(.*?)(?=\\s+(?:${fieldNames.join('|')})\\s*:|$)`, 'is');
    const match = header.match(pattern);
    return match ? match[1].replace(/\s+/g, '').trim() : '';
  };

  const candidates = [];
  assistantMessages.forEach((message, assistantOrder) => {
    const envelopes = extractEnvelopes(message.text);
    envelopes.forEach((envelope, envelopeOrder) => {
      const taskId = readField(envelope.text, 'task_id');
      const sourceReport = readField(envelope.text, 'source_report');
      const target = readField(envelope.text, 'target');
      const createdAtText = readField(envelope.text, 'created_at');
      const createdAtMs = Date.parse(createdAtText);
      const realDirectorTask =
        envelope.complete &&
        /^(?:SC|MEC)\d+(?:R\d+)?-\d{8}-\d{6}$/i.test(taskId) &&
        /^CGPT-REPORT-/i.test(sourceReport) &&
        /^codex-director$/i.test(target);
      candidates.push({
        text: envelope.text,
        complete: envelope.complete,
        realDirectorTask,
        taskId,
        sourceReport,
        createdAtMs: Number.isFinite(createdAtMs) ? createdAtMs : 0,
        assistantOrder,
        envelopeOrder
      });
    });
  });

  const realCandidates = candidates.filter((candidate) => candidate.realDirectorTask);
  realCandidates.sort((a, b) =>
    (a.createdAtMs - b.createdAtMs) ||
    (a.assistantOrder - b.assistantOrder) ||
    (a.envelopeOrder - b.envelopeOrder));

  const selected = realCandidates[realCandidates.length - 1] || null;
  return {
    text: selected ? selected.text : '',
    reason: selected ? 'Conversation data envelope selected.' : `No complete real director envelope found in conversation data. candidates=${candidates.map((c) => c.taskId || '(no-task)').slice(-12).join(',')}`,
    envelopeCount: selected ? 1 : 0,
    foundOpening: !!selected,
    foundClosing: !!selected,
    assistantMessageCount: assistantMessages.length,
    assistantEnvelopeCount: candidates.length,
    selectedAssistantMessageIndex: selected ? selected.assistantOrder : -1,
    selectedAssistantEnvelopeCount: selected ? 1 : 0,
    selectedTaskId: selected ? selected.taskId : '',
    candidateTaskIds: candidates.map((c) => c.taskId || '(no-task)').slice(-12).join(',')
  };
})()
""";

    private const string ScrollConversationToBottomScript = """
(() => {
  const scrollToBottom = (el) => {
    if (!el) return false;
    try {
      el.scrollTop = el.scrollHeight;
      if (typeof el.scrollTo === 'function') {
        el.scrollTo({ top: el.scrollHeight, behavior: 'instant' });
      }
      return true;
    } catch {
      return false;
    }
  };
  let scrolled = 0;
  const roots = [document.scrollingElement, document.documentElement, document.body].filter(Boolean);
  for (const root of roots) {
    if (scrollToBottom(root)) scrolled++;
  }
  const candidates = Array.from(document.querySelectorAll('main, [role="main"], div, section'))
    .filter((el) => el.scrollHeight > el.clientHeight + 40);
  candidates.sort((a, b) => (b.scrollHeight - b.clientHeight) - (a.scrollHeight - a.clientHeight));
  for (const el of candidates.slice(0, 12)) {
    if (scrollToBottom(el)) scrolled++;
  }
  const lastMessage = Array.from(document.querySelectorAll('[data-message-author-role], article')).pop();
  try {
    lastMessage?.scrollIntoView?.({ block: 'end', inline: 'nearest' });
  } catch {
  }
  return { scrolled };
})()
""";

    private const string ExpandCollapsedMessagesScript = """
(() => {
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const label = (el) => `${el.innerText || ''} ${el.textContent || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-testid') || ''}`.toLowerCase();
  const assistantRoots = Array.from(document.querySelectorAll('main [data-message-author-role="assistant"], [data-message-author-role="assistant"]')).filter(visible);
  const expansionPattern = /\b(show more|show all|read more|see more|expand|continue reading)\b/;
  let clicked = 0;
  for (const root of assistantRoots) {
    const controls = Array.from(root.querySelectorAll('button, [role="button"], summary')).filter(visible);
    for (const control of controls) {
      if (!expansionPattern.test(label(control))) continue;
      try {
        control.click();
        clicked++;
      } catch {
      }
    }
  }
  return { clicked };
})()
""";

    private const string EnvelopeExtractionScript = """
(() => {
  const openMarker = '<<<DCS_CODEX_TASK_V1>>>';
  const closeMarker = '<<<END_DCS_CODEX_TASK_V1>>>';
  const captureScope = __CAPTURE_SCOPE__;
  const visible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const countEnvelopes = (text) => {
    let count = 0;
    let search = 0;
    while (true) {
      const open = text.indexOf(openMarker, search);
      if (open < 0) break;
      const close = text.indexOf(closeMarker, open + openMarker.length);
      if (close < 0) break;
      count++;
      search = close + closeMarker.length;
    }
    return count;
  };
  const extractEnvelopeTexts = (text) => {
    const envelopes = [];
    let search = 0;
    while (true) {
      const open = text.indexOf(openMarker, search);
      if (open < 0) break;
      const close = text.indexOf(closeMarker, open + openMarker.length);
      if (close < 0) break;
      envelopes.push(text.slice(open, close + closeMarker.length));
      search = close + closeMarker.length;
    }
    return envelopes;
  };
  const looksLikeDirectorTask = (text) =>
    /(?:^|\s)task_id\s*:\s*(?:SC|MEC)\d+(?:R\d+)?-\d{8}-\d{6}/i.test(text) &&
    /(?:^|\s)source_report\s*:\s*CGPT-REPORT-/i.test(text) &&
    /(?:^|\s)target\s*:\s*codex-director/i.test(text);
  const hasEnvelopeMarker = (text) => text.includes(openMarker);
  const extractLastEnvelopeText = (text) => {
    let last = '';
    let search = 0;
    while (true) {
      const open = text.indexOf(openMarker, search);
      if (open < 0) break;
      const close = text.indexOf(closeMarker, open + openMarker.length);
      if (close < 0) break;
      last = text.slice(open, close + closeMarker.length);
      search = close + closeMarker.length;
    }
    return last;
  };
  const readTaskId = (text) => {
    const match = text.match(/(?:^|\s)task_id\s*:\s*([A-Za-z0-9_-]+)/i);
    return match ? match[1].trim() : '';
  };
  const uniqueVisible = (nodes) => {
    const seen = new Set();
    return Array.from(nodes).filter((node) => {
      if (!visible(node) || seen.has(node)) return false;
      seen.add(node);
      return true;
    });
  };
  const directAssistantNodes = uniqueVisible(document.querySelectorAll('main [data-message-author-role="assistant"], [data-message-author-role="assistant"]'));
  const articleAssistantNodes = directAssistantNodes.length > 0
    ? []
    : uniqueVisible(Array.from(document.querySelectorAll('main article, article')).filter((article) =>
        article.matches('[data-message-author-role="assistant"]') ||
        !!article.querySelector('[data-message-author-role="assistant"]')));
  const markdownAssistantNodes = directAssistantNodes.length > 0 || articleAssistantNodes.length > 0
    ? []
    : uniqueVisible(Array.from(document.querySelectorAll('.markdown, .prose')).filter((node) => {
        const owner = node.closest('[data-message-author-role], article');
        return !!owner && (
          owner.matches('[data-message-author-role="assistant"]') ||
          !!owner.querySelector('[data-message-author-role="assistant"]')
        );
      }));
  const assistantNodes = directAssistantNodes.length > 0
    ? directAssistantNodes
    : articleAssistantNodes.length > 0
      ? articleAssistantNodes
      : markdownAssistantNodes;
  const assistantTexts = assistantNodes.map((node) => node.innerText || node.textContent || '').filter(Boolean);
  const assistantText = assistantTexts.join('\n\n');
  const bodyText = document.body?.innerText || document.body?.textContent || '';
  const assistantEnvelopeCount = countEnvelopes(assistantText);
  const bodyEnvelopeCount = countEnvelopes(bodyText);
  const bodyEnvelopeTexts = extractEnvelopeTexts(bodyText);
  const bodyLatestEnvelopeText = [...bodyEnvelopeTexts].reverse().find(looksLikeDirectorTask) || extractLastEnvelopeText(bodyText);
  let usedBodyFallback = false;
  let selectedAssistantMessageIndex = -1;
  let selectedAssistantEnvelopeCount = 0;
  let text = '';
  if (captureScope === 'WholeConversationNewest') {
    text = assistantTexts.length > 0 ? assistantText : bodyText;
    usedBodyFallback = assistantTexts.length === 0;
    for (let i = assistantTexts.length - 1; i >= 0; i--) {
      if (hasEnvelopeMarker(assistantTexts[i])) {
        selectedAssistantMessageIndex = i;
        selectedAssistantEnvelopeCount = countEnvelopes(assistantTexts[i]);
        break;
      }
    }
  } else {
    for (let i = assistantTexts.length - 1; i >= 0; i--) {
      if (hasEnvelopeMarker(assistantTexts[i])) {
        const count = countEnvelopes(assistantTexts[i]);
        text = assistantTexts[i];
        selectedAssistantMessageIndex = i;
        selectedAssistantEnvelopeCount = count;
        break;
      }
    }
    if (!text) {
      usedBodyFallback = true;
      const lastOpen = bodyText.lastIndexOf(openMarker);
      text = extractLastEnvelopeText(bodyText) || (lastOpen >= 0 ? bodyText.slice(lastOpen) : bodyText);
    }
  }
  if (bodyLatestEnvelopeText && bodyEnvelopeCount > assistantEnvelopeCount) {
    text = bodyLatestEnvelopeText;
    usedBodyFallback = true;
    selectedAssistantMessageIndex = -1;
    selectedAssistantEnvelopeCount = 0;
  }
  const envelopeCount = countEnvelopes(text);
  return {
    text,
    envelopeCount,
    assistantEnvelopeCount,
    bodyEnvelopeCount,
    usedBodyFallback,
    textLength: text.length,
    foundOpening: text.includes(openMarker),
    foundClosing: text.includes(closeMarker),
    assistantMessageCount: assistantTexts.length,
    selectedAssistantMessageIndex,
    selectedAssistantEnvelopeCount,
    captureScope,
    selectedTaskId: readTaskId(text),
    bodyCandidateTaskIds: bodyEnvelopeTexts.map(readTaskId).filter(Boolean).slice(-12).join(',')
  };
})()
""";
}
