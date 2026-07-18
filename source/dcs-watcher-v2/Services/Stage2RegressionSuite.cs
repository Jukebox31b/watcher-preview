using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public sealed class Stage2RegressionSuite
{
    private static readonly string ThreadId = string.Join("-", "00000000", "0000", "4000", "8000", "000000000002");

    public IReadOnlyList<Stage2TestCaseResult> Run()
    {
        var tests = new List<Stage2TestCaseResult>();
        Run(tests, 1, "valid same-branch direct response", ValidSameBranch);
        Run(tests, 2, "sibling-branch response", SiblingBranch);
        Run(tests, 3, "genuine assistant message with onCurrentPath=false", OffCurrentPath);
        Run(tests, 4, "wrong direct parent", WrongDirectParent);
        Run(tests, 5, "correct conversation ID but wrong wake", WrongWake);
        Run(tests, 6, "ancestry missing the wake", AncestryMissingWake);
        Run(tests, 7, "current_node changed before capture", CurrentNodeChanged);
        Run(tests, 8, "current_node belongs to sibling branch", CurrentNodeSibling);
        Run(tests, 9, "stale response from an earlier wake", StaleResponse);
        Run(tests, 10, "fallbackBody=true", FallbackBody);
        Run(tests, 11, "whole-page capture", WholePageCapture);
        Run(tests, 12, "ambiguous selectedAssistantIndex", AmbiguousAssistantIndex);
        Run(tests, 13, "multiple complete envelopes", MultipleEnvelopes);
        Run(tests, 14, "partial envelope", PartialEnvelope);
        Run(tests, 15, "malformed envelope", MalformedEnvelope);
        Run(tests, 16, "modified envelope after signing", ModifiedEnvelope);
        Run(tests, 17, "modified provenance after signing", ModifiedProvenance);
        Run(tests, 18, "wrong signer key", WrongSigner);
        Run(tests, 19, "unknown signer key", UnknownSigner);
        Run(tests, 20, "revoked signer key", RevokedSigner);
        Run(tests, 21, "missing signature", MissingSignature);
        Run(tests, 22, "expired provenance", ExpiredProvenance);
        Run(tests, 23, "excessive clock skew", ClockSkew);
        Run(tests, 24, "reused nonce", ReusedNonce);
        Run(tests, 25, "reused transaction ID", ReusedTransaction);
        Run(tests, 26, "replayed accepted transaction", ReplayedAcceptedTransaction);
        Run(tests, 27, "same envelope sent to a different Codex thread", WrongDestination);
        Run(tests, 28, "wrong source report", WrongSourceReport);
        Run(tests, 29, "unsafe control byte", UnsafeControlByte);
        Run(tests, 30, "invalid UTF-8", InvalidUtf8);
        Run(tests, 31, "oversized envelope", OversizedEnvelope);
        Run(tests, 32, "duplicate JSON keys", DuplicateJsonKeys);
        Run(tests, 33, "noncanonical JSON serialization", NoncanonicalJson);
        Run(tests, 34, "Watcher restart with replay ledger preserved", RestartReplayLedger);
        Run(tests, 35, "interrupted transaction before signing", InterruptedBeforeSigning);
        Run(tests, 36, "interrupted transaction after signing before test sink", InterruptedAfterSigning);
        Run(tests, 37, "interrupted transaction after test-sink acceptance", InterruptedAfterAcceptance);
        Run(tests, 38, "manual paste mode remains distinct", ManualModeDistinct);
        Run(tests, 39, "manual file hash mismatch", ManualFileHashMismatch);
        Run(tests, 40, "valid manual file authorization", ValidManualAuthorization);
        Run(tests, 41, "valid automatic transaction accepted by test sink", ValidTestSinkAcceptance);
        Run(tests, 42, "valid signed transaction rejected after one replay", SignedReplayRejected);
        Run(tests, 43, "branch divergence warning generated", DivergenceWarning);
        Run(tests, 44, "no automatic retry after divergence", NoRetryAfterDivergence);
        Run(tests, 45, "no IPC output generated after failed validation", NoOutputAfterFailure);
        Run(tests, 46, "reused assistant response for a different wake", ReusedAssistantResponse);
        Run(tests, 47, "same envelope under a new transaction", ReusedEnvelope);
        Run(tests, 48, "valid offline end-to-end flow accepts then rejects replay", OfflineEndToEnd);
        Run(tests, 49, "historical sibling-branch pattern cannot deliver", HistoricalSiblingBranch);
        Run(tests, 50, "unknown JSON field is rejected", UnknownJsonField);
        Run(tests, 51, "Stage 2 policy rejects every live wake", Stage2RejectsLiveWake);
        Run(tests, 52, "Stage 2 policy rejects live instruction capture", Stage2RejectsLiveCapture);
        Run(tests, 53, "invalid signed envelope schema is rejected", InvalidEnvelopeSchema);
        Run(tests, 54, "response-content hash is independently verified", ResponseContentHashMismatch);
        Run(tests, 55, "missing build identity is rejected", MissingBuildIdentity);
        Run(tests, 56, "malformed nonce is rejected", MalformedNonce);
        Run(tests, 57, "invalid message timestamps are rejected", InvalidMessageTimestamps);
        Run(tests, 58, "key ID cannot be rebound", KeyIdRebindRejected);
        Run(tests, 59, "revoked key ID cannot be reactivated", RevokedKeyReactivationRejected);
        Run(tests, 60, "registry algorithm mismatch is rejected", RegistryAlgorithmMismatch);
        Run(tests, 61, "non-P256 public key is rejected", NonP256KeyRejected);
        Run(tests, 62, "empty public key material is bounded rejection", EmptyPublicKeyRejected);
        Run(tests, 63, "divergence contains stop banner and human warning", ExactDivergenceWarnings);
        Run(tests, 64, "altered bound response content is rejected", AlteredResponseContent);
        Run(tests, 65, "assistant timestamp before wake is rejected", AssistantBeforeWake);
        Run(tests, 66, "unique empty assistant metadata chain is accepted", EmptyAssistantMetadataChainAccepted);
        Run(tests, 67, "actionable assistant intermediate is rejected", ActionableAssistantIntermediateRejected);
        Run(tests, 68, "branched assistant metadata chain is rejected", BranchedAssistantMetadataChainRejected);
        Run(tests, 69, "unique hidden thoughts metadata chain is accepted", HiddenThoughtsMetadataChainAccepted);
        Run(tests, 70, "authenticated hidden platform rebase chain is accepted", AuthenticatedPlatformRebaseChainAccepted);
        Run(tests, 71, "unflagged system node is rejected", UnflaggedSystemNodeRejected);
        Run(tests, 72, "cross-turn platform rebase node is rejected", CrossTurnPlatformRebaseRejected);
        return tests;
    }

    private static void ValidSameBranch()
    {
        using var fixture = new Fixture();
        AssertPipelineAccepted(fixture.Build());
    }

    private static void SiblingBranch()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["parent"].ChildMessageIds = ["visible-sibling", "wake"];
        fixture.Snapshot.Nodes["visible-sibling"] = Node("visible-sibling", "parent", "user", "What is your report status");
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void OffCurrentPath()
    {
        using var fixture = new Fixture();
        fixture.Response.OnCurrentPath = false;
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void WrongDirectParent()
    {
        using var fixture = new Fixture();
        fixture.Response.ParentMessageId = "parent";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void EmptyAssistantMetadataChainAccepted()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["model-context"];
        fixture.Snapshot.Nodes["model-context"] = Node("model-context", "wake", "assistant", string.Empty, ["reasoning-recap"]);
        fixture.Snapshot.Nodes["model-context"].ContentType = "model_editable_context";
        fixture.Snapshot.Nodes["reasoning-recap"] = Node("reasoning-recap", "model-context", "assistant", "Worked for a couple of seconds", ["response"]);
        fixture.Snapshot.Nodes["reasoning-recap"].ContentType = "reasoning_recap";
        fixture.Snapshot.Nodes["response"].ParentMessageId = "reasoning-recap";
        AssertPipelineAccepted(fixture.Build());
    }

    private static void ActionableAssistantIntermediateRejected()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["model-context"];
        fixture.Snapshot.Nodes["model-context"] = Node("model-context", "wake", "assistant", "unexpected content", ["response"]);
        fixture.Snapshot.Nodes["model-context"].ContentType = "text";
        fixture.Snapshot.Nodes["response"].ParentMessageId = "model-context";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void HiddenThoughtsMetadataChainAccepted()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["thoughts"];
        fixture.Snapshot.Nodes["thoughts"] = Node("thoughts", "wake", "assistant", string.Empty, ["response"]);
        fixture.Snapshot.Nodes["thoughts"].ContentType = "thoughts";
        fixture.Snapshot.Nodes["response"].ParentMessageId = "thoughts";
        AssertPipelineAccepted(fixture.Build());
    }

    private static void AuthenticatedPlatformRebaseChainAccepted()
    {
        using var fixture = new Fixture();
        SetTurnIdentity(fixture.Snapshot.Nodes["wake"]);
        SetTurnIdentity(fixture.Snapshot.Nodes["response"]);
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["rebase-system"];
        fixture.Snapshot.Nodes["rebase-system"] = PlatformRebaseNode("rebase-system", "wake", ["rebase-developer"], system: true);
        fixture.Snapshot.Nodes["rebase-developer"] = PlatformRebaseNode("rebase-developer", "rebase-system", ["response"], system: false);
        fixture.Snapshot.Nodes["response"].ParentMessageId = "rebase-developer";
        AssertPipelineAccepted(fixture.Build());
    }

    private static void UnflaggedSystemNodeRejected()
    {
        using var fixture = new Fixture();
        SetTurnIdentity(fixture.Snapshot.Nodes["wake"]);
        SetTurnIdentity(fixture.Snapshot.Nodes["response"]);
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["system"];
        fixture.Snapshot.Nodes["system"] = PlatformRebaseNode("system", "wake", ["response"], system: true);
        fixture.Snapshot.Nodes["system"].RebaseSystemMessage = false;
        fixture.Snapshot.Nodes["response"].ParentMessageId = "system";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void CrossTurnPlatformRebaseRejected()
    {
        using var fixture = new Fixture();
        SetTurnIdentity(fixture.Snapshot.Nodes["wake"]);
        SetTurnIdentity(fixture.Snapshot.Nodes["response"]);
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["system"];
        fixture.Snapshot.Nodes["system"] = PlatformRebaseNode("system", "wake", ["response"], system: true);
        fixture.Snapshot.Nodes["system"].TurnExchangeId = "different-turn";
        fixture.Snapshot.Nodes["response"].ParentMessageId = "system";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void BranchedAssistantMetadataChainRejected()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["wake"].ChildMessageIds = ["model-context"];
        fixture.Snapshot.Nodes["model-context"] = Node("model-context", "wake", "assistant", string.Empty, ["response", "sibling"]);
        fixture.Snapshot.Nodes["model-context"].ContentType = "model_editable_context";
        fixture.Snapshot.Nodes["response"].ParentMessageId = "model-context";
        fixture.Snapshot.Nodes["sibling"] = Node("sibling", "model-context", "assistant", string.Empty);
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void WrongWake()
    {
        using var fixture = new Fixture();
        fixture.Response.WakeToken = "earlier-wake-token";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void AncestryMissingWake()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["response"].ParentMessageId = "parent";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void CurrentNodeChanged()
    {
        using var fixture = new Fixture();
        fixture.Response.CurrentNodeAtCapture = "earlier-node";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void CurrentNodeSibling()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["sibling"] = Node("sibling", "parent", "assistant", "sibling");
        fixture.Snapshot.CurrentNode = "sibling";
        fixture.Response.CurrentNodeAtCapture = "sibling";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void StaleResponse()
    {
        using var fixture = new Fixture();
        fixture.Response.MessageId = "stale-response";
        fixture.Response.ParentMessageId = "old-wake";
        AssertPipelineRejected(fixture.Build(), "BRANCH_DIVERGENCE");
    }

    private static void FallbackBody()
    {
        using var fixture = new Fixture();
        fixture.Response.FallbackBody = true;
        AssertPipelineRejected(fixture.Build(), "FALLBACK_CAPTURE");
    }

    private static void WholePageCapture()
    {
        using var fixture = new Fixture();
        fixture.Response.WholePageCaptureUsed = true;
        AssertPipelineRejected(fixture.Build(), "WHOLE_PAGE_CAPTURE");
    }

    private static void AmbiguousAssistantIndex()
    {
        using var fixture = new Fixture();
        fixture.Response.AssistantSelectionAmbiguous = true;
        AssertPipelineRejected(fixture.Build(), "ASSISTANT_SELECTION_AMBIGUOUS");
    }

    private static void MultipleEnvelopes()
    {
        using var fixture = new Fixture();
        fixture.Response.Content += "\n" + BuildEnvelope("SC9999-20260716-130000");
        fixture.Snapshot.Nodes["response"].Content = fixture.Response.Content;
        AssertPipelineRejected(fixture.Build(), "LINEAGE_REJECTED");
    }

    private static void PartialEnvelope()
    {
        using var fixture = new Fixture();
        fixture.Response.Content = fixture.Response.Content.Replace(ChatGptEnvelopeCapture.CloseMarker, string.Empty, StringComparison.Ordinal);
        fixture.Snapshot.Nodes["response"].Content = fixture.Response.Content;
        AssertPipelineRejected(fixture.Build(), "LINEAGE_REJECTED");
    }

    private static void MalformedEnvelope()
    {
        var validator = new Stage2EnvelopeValidator();
        var malformed = Encoding.UTF8.GetBytes(BuildEnvelope().Replace("task_id:", "missing_task:", StringComparison.Ordinal));
        AssertEnvelopeRejected(validator.Validate(malformed), "TASK_ID_MISSING");
    }

    private static void ModifiedEnvelope()
    {
        using var fixture = new Fixture();
        var result = fixture.Build();
        var transaction = Parse(result.PayloadBytes!);
        transaction.EnvelopeBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildEnvelope("SC2000-20260716-120000", "modified " + new string('z', 120))));
        AssertVerifierRejected(fixture.Verify(Stage2CanonicalJson.SerializeTransaction(transaction)), "ENVELOPE_SIZE_MISMATCH", "ENVELOPE_HASH_MISMATCH");
    }

    private static void ModifiedProvenance()
    {
        using var fixture = new Fixture();
        var result = fixture.Build();
        var transaction = Parse(result.PayloadBytes!);
        transaction.Provenance.SourceReport = "CGPT-REPORT-modified.md";
        AssertVerifierRejected(fixture.Verify(Stage2CanonicalJson.SerializeTransaction(transaction)), "SIGNATURE_INVALID");
    }

    private static void WrongSigner()
    {
        using var fixture = new Fixture();
        var result = fixture.Build();
        using var other = new EphemeralStage2ProvenanceSigner(fixture.Signer.KeyId);
        var payload = MutateAndSign(result.PayloadBytes!, other, _ => { });
        AssertVerifierRejected(fixture.Verify(payload), "SIGNATURE_INVALID");
    }

    private static void UnknownSigner()
    {
        using var fixture = new Fixture(registerKey: false);
        AssertVerifierRejected(fixture.Verify(fixture.Build().PayloadBytes!), "SIGNER_UNKNOWN");
    }

    private static void RevokedSigner()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        fixture.Registry.Revoke(fixture.Signer.KeyId, fixture.Now);
        AssertVerifierRejected(fixture.Verify(payload), "SIGNER_REVOKED");
    }

    private static void MissingSignature()
    {
        using var fixture = new Fixture();
        var transaction = Parse(fixture.Build().PayloadBytes!);
        transaction.Provenance.SignatureOrMac = string.Empty;
        AssertVerifierRejected(fixture.Verify(Stage2CanonicalJson.SerializeTransaction(transaction)), "SIGNATURE_MISSING");
    }

    private static void ExpiredProvenance()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance =>
        {
            provenance.WakeMessageCreatedAt = fixture.Now.AddMinutes(-13).ToString("O");
            provenance.AssistantMessageCreatedAt = fixture.Now.AddMinutes(-12).ToString("O");
            provenance.BackendVerificationTimestamp = fixture.Now.AddMinutes(-11).ToString("O");
            provenance.IssueTimeUtc = fixture.Now.AddMinutes(-10).ToString("O");
            provenance.ExpiryTimeUtc = fixture.Now.AddSeconds(-1).ToString("O");
        });
        AssertVerifierRejected(fixture.Verify(payload), "PROVENANCE_EXPIRED");
    }

    private static void ClockSkew()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance =>
        {
            provenance.IssueTimeUtc = fixture.Now.AddMinutes(10).ToString("O");
            provenance.ExpiryTimeUtc = fixture.Now.AddMinutes(15).ToString("O");
        });
        AssertVerifierRejected(fixture.Verify(payload), "CLOCK_SKEW");
    }

    private static void ReusedNonce()
    {
        using var fixture = new Fixture();
        var first = fixture.Build();
        AssertVerifierAccepted(fixture.Verify(first.PayloadBytes!));
        var payload = MutateAndSign(first.PayloadBytes!, fixture.Signer, provenance =>
        {
            provenance.TransactionId = Guid.NewGuid().ToString("D");
            UpdateReplayKey(provenance);
        });
        AssertVerifierRejected(fixture.Verify(payload), "NONCE_REPLAY");
    }

    private static void ReusedTransaction()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        AssertVerifierAccepted(fixture.Verify(payload));
        AssertVerifierRejected(fixture.Verify(payload), "TRANSACTION_REPLAY");
    }

    private static void ReplayedAcceptedTransaction() => ReusedTransaction();

    private static void WrongDestination()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance =>
        {
            provenance.DestinationCodexThreadId = "another-thread";
            UpdateReplayKey(provenance);
        });
        AssertVerifierRejected(fixture.Verify(payload), "DESTINATION_THREAD_MISMATCH");
    }

    private static void WrongSourceReport()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance => provenance.SourceReport = "CGPT-REPORT-wrong.md");
        AssertVerifierRejected(fixture.Verify(payload), "SOURCE_REPORT_MISMATCH");
    }

    private static void UnsafeControlByte()
    {
        var bytes = Encoding.UTF8.GetBytes(BuildEnvelope());
        bytes[bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Exact instruction"))] = 0x08;
        AssertEnvelopeRejected(new Stage2EnvelopeValidator().Validate(bytes), "UNSAFE_CONTROL_BYTE");
    }

    private static void InvalidUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes(BuildEnvelope());
        bytes[20] = 0xFF;
        AssertEnvelopeRejected(new Stage2EnvelopeValidator().Validate(bytes), "INVALID_UTF8");
    }

    private static void OversizedEnvelope()
    {
        AssertEnvelopeRejected(new Stage2EnvelopeValidator().Validate(Encoding.UTF8.GetBytes(BuildEnvelope()), 100), "ENVELOPE_OVERSIZED");
    }

    private static void DuplicateJsonKeys()
    {
        using var fixture = new Fixture();
        var text = Encoding.UTF8.GetString(fixture.Build().PayloadBytes!);
        text = text.Replace("{\"schema\":", "{\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal);
        AssertVerifierRejected(fixture.Verify(Encoding.UTF8.GetBytes(text)), "DUPLICATE_JSON_KEY");
    }

    private static void NoncanonicalJson()
    {
        using var fixture = new Fixture();
        var text = Encoding.UTF8.GetString(fixture.Build().PayloadBytes!);
        AssertVerifierRejected(fixture.Verify(Encoding.UTF8.GetBytes(" " + text)), "NONCANONICAL_JSON");
    }

    private static void RestartReplayLedger()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        AssertVerifierAccepted(fixture.Verify(payload));
        var restarted = new CodexStage2TestVerifier(fixture.Registry, new Stage2ReplayLedger(fixture.IntakeLedgerPath), ThreadId);
        AssertVerifierRejected(restarted.Verify(payload, fixture.Now), "TRANSACTION_REPLAY");
    }

    private static void InterruptedBeforeSigning()
    {
        using var fixture = new Fixture();
        var result = fixture.Build(Stage2FaultPoint.BeforeSigning);
        AssertPipelineRejected(result, "FAULT_BEFORE_SIGNING");
        if (!File.Exists(result.TransactionRecordPath) || result.PayloadBytes is not null)
        {
            throw new InvalidOperationException("Pre-sign interruption was not durably recorded or emitted output.");
        }
    }

    private static void InterruptedAfterSigning()
    {
        using var fixture = new Fixture();
        var result = fixture.Build(Stage2FaultPoint.AfterSigningBeforeTestSink);
        AssertPipelineRejected(result, "FAULT_AFTER_SIGNING");
        if (!File.ReadAllText(result.TransactionRecordPath).Contains("signed-dry-run", StringComparison.Ordinal) || result.PayloadBytes is not null)
        {
            throw new InvalidOperationException("Post-sign interruption record is incomplete or emitted output.");
        }
    }

    private static void InterruptedAfterAcceptance()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        AssertVerifierAccepted(fixture.Verify(payload));
        AssertVerifierRejected(fixture.Verify(payload), "TRANSACTION_REPLAY");
    }

    private static void ManualModeDistinct()
    {
        var manual = new ManualInstructionAuthorizationV1();
        if (manual.Schema.Contains("WATCHER", StringComparison.OrdinalIgnoreCase) || manual.Schema.Contains("signed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Manual mode impersonates Watcher provenance.");
        }
    }

    private static void ManualFileHashMismatch()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "manual.txt");
        File.WriteAllText(path, "manual payload");
        var authorization = BuildManualAuthorization(path);
        authorization.ExpectedSha256 = new string('0', 64);
        var result = new ManualInstructionAuthorizationValidator().ValidateFile(authorization, ThreadId);
        if (result.Accepted || result.ReasonCode != "MANUAL_FILE_HASH_MISMATCH")
        {
            throw new InvalidOperationException($"Expected manual hash rejection, got {result.ReasonCode}.");
        }
    }

    private static void ValidManualAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "manual.txt");
        File.WriteAllText(path, "manual payload");
        var result = new ManualInstructionAuthorizationValidator().ValidateFile(BuildManualAuthorization(path), ThreadId);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private static void ValidTestSinkAcceptance()
    {
        using var fixture = new Fixture();
        var sink = fixture.CreateSink();
        var result = sink.Receive(fixture.Build().PayloadBytes!, fixture.Now);
        AssertVerifierAccepted(result);
        if (sink.ReceiveCount != 1 || sink.AcceptedCount != 1)
        {
            throw new InvalidOperationException("Test sink counters are incorrect.");
        }
    }

    private static void SignedReplayRejected() => ReusedTransaction();

    private static void DivergenceWarning()
    {
        using var fixture = new Fixture();
        fixture.Response.OnCurrentPath = false;
        var result = fixture.Build();
        AssertPipelineRejected(result, "BRANCH_DIVERGENCE");
        if (!result.Message.Contains(Stage2DryRunPipeline.DivergenceWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Required human-visible divergence warning was not generated.");
        }
    }

    private static void NoRetryAfterDivergence()
    {
        using var fixture = new Fixture();
        fixture.Response.OnCurrentPath = false;
        var result = fixture.Build();
        AssertPipelineRejected(result, "BRANCH_DIVERGENCE");
        var record = File.ReadAllText(result.TransactionRecordPath);
        if (!record.Contains("\"automatic_retry\":false", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Divergence record permits automatic retry.");
        }
    }

    private static void NoOutputAfterFailure()
    {
        using var fixture = new Fixture();
        fixture.Response.FallbackBody = true;
        var sink = fixture.CreateSink();
        var result = fixture.Build();
        AssertPipelineRejected(result, "FALLBACK_CAPTURE");
        if (result.PayloadBytes is not null || sink.ReceiveCount != 0)
        {
            throw new InvalidOperationException("Failed validation generated output or reached the sink.");
        }
    }

    private static void ReusedAssistantResponse()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        AssertVerifierAccepted(fixture.Verify(payload));
        var second = MutateAndSign(payload, fixture.Signer, provenance =>
        {
            provenance.TransactionId = Guid.NewGuid().ToString("D");
            provenance.Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            provenance.WakeMessageId = "wake-2";
            provenance.ExpectedParentMessageId = "wake-2";
            provenance.AssistantParentMessageId = "wake-2";
            var wakeIndex = provenance.AncestryMessageIds.IndexOf("wake");
            provenance.AncestryMessageIds[wakeIndex] = "wake-2";
            provenance.AncestryDigestSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", provenance.AncestryMessageIds)));
            UpdateReplayKey(provenance);
        });
        AssertVerifierRejected(fixture.Verify(second), "ASSISTANT_RESPONSE_REPLAY");
    }

    private static void ReusedEnvelope()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        AssertVerifierAccepted(fixture.Verify(payload));
        var second = MutateAndSign(payload, fixture.Signer, provenance =>
        {
            provenance.TransactionId = Guid.NewGuid().ToString("D");
            provenance.Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            UpdateReplayKey(provenance);
        });
        AssertVerifierRejected(fixture.Verify(second), "ENVELOPE_REPLAY");
    }

    private static void OfflineEndToEnd()
    {
        using var fixture = new Fixture();
        var preservedReportFixture = Encoding.UTF8.GetBytes("task_id: SC1999\nresult: PASS\n");
        if (preservedReportFixture.Length == 0)
        {
            throw new InvalidOperationException("Report fixture missing.");
        }

        var signed = fixture.Build();
        AssertPipelineAccepted(signed);
        var sink = fixture.CreateSink();
        AssertVerifierAccepted(sink.Receive(signed.PayloadBytes!, fixture.Now));
        AssertVerifierRejected(sink.Receive(signed.PayloadBytes!, fixture.Now), "TRANSACTION_REPLAY");
    }

    private static void HistoricalSiblingBranch()
    {
        using var fixture = new Fixture();
        fixture.Snapshot.Nodes["visible-user"] = Node("visible-user", "parent", "user", "What is your report status");
        fixture.Snapshot.Nodes["parent"].ChildMessageIds = ["visible-user", "wake"];
        fixture.Snapshot.CurrentNode = "visible-user";
        fixture.Snapshot.BrowserVisibleMessageIds = ["visible-user"];
        fixture.Response.OnCurrentPath = false;
        fixture.Response.CurrentNodeAtCapture = "visible-user";
        var sink = fixture.CreateSink();
        var result = fixture.Build();
        AssertPipelineRejected(result, "BRANCH_DIVERGENCE");
        if (result.Provenance is not null || result.PayloadBytes is not null || sink.ReceiveCount != 0)
        {
            throw new InvalidOperationException("Historical sibling-branch pattern produced provenance or sink traffic.");
        }
    }

    private static void UnknownJsonField()
    {
        using var fixture = new Fixture();
        var text = Encoding.UTF8.GetString(fixture.Build().PayloadBytes!);
        text = text.Insert(text.Length - 1, ",\"unexpected\":true");
        var result = fixture.Verify(Encoding.UTF8.GetBytes(text));
        if (result.Accepted)
        {
            throw new InvalidOperationException("Unknown JSON field was accepted.");
        }
    }

    private static void Stage2RejectsLiveWake()
    {
        var config = new AppConfig { OperatingStage = nameof(WatcherOperatingStage.Stage2SignedDryRun) };
        if (WatcherSafetyPolicy.CanPostWake(config, new WakeTransactionRecord(), out _))
        {
            throw new InvalidOperationException("Stage 2 allowed a live ChatGPT wake.");
        }
    }

    private static void Stage2RejectsLiveCapture()
    {
        var config = new AppConfig { OperatingStage = nameof(WatcherOperatingStage.Stage2SignedDryRun) };
        var observation = new AssistantResponseObservation
        {
            CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
            ApiVerified = true,
            OnCurrentPath = true
        };
        if (WatcherSafetyPolicy.CanCaptureInstructionForAuthorization(config, observation, out _))
        {
            throw new InvalidOperationException("Stage 2 allowed a live instruction capture.");
        }
    }

    private static void InvalidEnvelopeSchema()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance => provenance.EnvelopeSchema = "DCS_CODEX_TASK_V0");
        AssertVerifierRejected(fixture.Verify(payload), "ENVELOPE_SCHEMA_INVALID");
    }

    private static void ResponseContentHashMismatch()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance => provenance.ResponseMessageContentSha256 = new string('0', 64));
        AssertVerifierRejected(fixture.Verify(payload), "RESPONSE_HASH_MISMATCH");
    }

    private static void MissingBuildIdentity()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance => provenance.WatcherConfigurationSha256 = string.Empty);
        AssertVerifierRejected(fixture.Verify(payload), "HASH_FIELD_INVALID");
    }

    private static void MalformedNonce()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance =>
        {
            provenance.Nonce = "not-a-nonce";
            UpdateReplayKey(provenance);
        });
        AssertVerifierRejected(fixture.Verify(payload), "NONCE_INVALID");
    }

    private static void InvalidMessageTimestamps()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance => provenance.BackendVerificationTimestamp = "not-a-time");
        AssertVerifierRejected(fixture.Verify(payload), "TIME_INVALID");
    }

    private static void KeyIdRebindRejected()
    {
        using var fixture = new Fixture();
        using var replacement = new EphemeralStage2ProvenanceSigner(fixture.Signer.KeyId);
        try
        {
            fixture.Registry.AddOrUpdate(replacement, now: fixture.Now);
            throw new InvalidOperationException("Registry silently rebound an existing key ID.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be rebound", StringComparison.Ordinal))
        {
        }
    }

    private static void RevokedKeyReactivationRejected()
    {
        using var fixture = new Fixture();
        fixture.Registry.Revoke(fixture.Signer.KeyId, fixture.Now);
        try
        {
            fixture.Registry.AddOrUpdate(fixture.Signer, now: fixture.Now.AddMinutes(1));
            throw new InvalidOperationException("Registry reactivated a revoked key ID.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be reactivated", StringComparison.Ordinal))
        {
        }
    }

    private static void RegistryAlgorithmMismatch()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        var document = fixture.Registry.Load();
        document.Keys[0].Algorithm = "ECDSA_P384_SHA384";
        File.WriteAllBytes(fixture.Registry.Path, JsonSerializer.SerializeToUtf8Bytes(document, Stage2CanonicalJson.Options));
        AssertVerifierRejected(fixture.Verify(payload), "SIGNER_ALGORITHM_INVALID");
    }

    private static void NonP256KeyRejected()
    {
        using var fixture = new Fixture(registerKey: false);
        using var p384 = new P384TestSigner(fixture.Signer.KeyId);
        fixture.Registry.AddOrUpdate(p384, now: fixture.Now);
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, p384, _ => { });
        AssertVerifierRejected(fixture.Verify(payload), "SIGNATURE_INVALID");
    }

    private static void EmptyPublicKeyRejected()
    {
        using var fixture = new Fixture();
        var payload = fixture.Build().PayloadBytes!;
        var document = fixture.Registry.Load();
        document.Keys[0].PublicKeySpkiBase64 = string.Empty;
        File.WriteAllBytes(fixture.Registry.Path, JsonSerializer.SerializeToUtf8Bytes(document, Stage2CanonicalJson.Options));
        AssertVerifierRejected(fixture.Verify(payload), "SIGNATURE_INVALID");
    }

    private static void ExactDivergenceWarnings()
    {
        using var fixture = new Fixture();
        fixture.Response.OnCurrentPath = false;
        var result = fixture.Build();
        if (!result.Message.Contains(BranchLineageSafetyService.DivergenceWarning, StringComparison.Ordinal) ||
            !result.Message.Contains(Stage2DryRunPipeline.HumanDivergenceWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Divergence output omitted a required warning.");
        }
    }

    private static void AlteredResponseContent()
    {
        using var fixture = new Fixture();
        var transaction = Parse(fixture.Build().PayloadBytes!);
        transaction.ResponseMessageContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("altered response"));
        AssertVerifierRejected(fixture.Verify(Stage2CanonicalJson.SerializeTransaction(transaction)), "RESPONSE_HASH_MISMATCH", "RESPONSE_ENVELOPE_BINDING_INVALID");
    }

    private static void AssistantBeforeWake()
    {
        using var fixture = new Fixture();
        var payload = MutateAndSign(fixture.Build().PayloadBytes!, fixture.Signer, provenance =>
            provenance.AssistantMessageCreatedAt = fixture.Now.AddMinutes(-30).ToString("O"));
        AssertVerifierRejected(fixture.Verify(payload), "TIME_INVALID");
    }

    private static byte[] MutateAndSign(
        byte[] payload,
        IStage2ProvenanceSigner signer,
        Action<Stage2InstructionProvenanceV1> mutation)
    {
        var transaction = Parse(payload);
        mutation(transaction.Provenance);
        transaction.Provenance.SignatureOrMac = string.Empty;
        transaction.Provenance.SignatureOrMac = Convert.ToBase64String(signer.Sign(Stage2CanonicalJson.SerializeUnsignedProvenance(transaction.Provenance)));
        return Stage2CanonicalJson.SerializeTransaction(transaction);
    }

    private static void UpdateReplayKey(Stage2InstructionProvenanceV1 provenance)
    {
        provenance.ReplayLedgerKey = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("|", new[]
        {
            provenance.TransactionId, provenance.Nonce, provenance.EnvelopeSha256,
            provenance.WakeMessageId, provenance.AssistantMessageId, provenance.DestinationCodexThreadId
        })));
    }

    private static Stage2BoundInstructionTransactionV1 Parse(byte[] payload)
    {
        return JsonSerializer.Deserialize<Stage2BoundInstructionTransactionV1>(payload, Stage2CanonicalJson.Options)
            ?? throw new InvalidOperationException("Could not parse test transaction.");
    }

    private static ManualInstructionAuthorizationV1 BuildManualAuthorization(string path)
    {
        var text = "I manually authorize this exact file for the visible Director thread.";
        var bytes = File.ReadAllBytes(path);
        return new ManualInstructionAuthorizationV1
        {
            AbsoluteFilePath = Path.GetFullPath(path),
            ExpectedSizeBytes = bytes.LongLength,
            ExpectedSha256 = Stage2Crypto.Sha256Hex(bytes),
            ReceivingCodexThreadId = ThreadId,
            DirectManuallyPastedAuthorizationText = text,
            AuthorizationTextSha256 = Stage2Crypto.Sha256Hex(Encoding.UTF8.GetBytes(text)),
            ReceiptTimestampUtc = DateTimeOffset.UtcNow
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

    private static ConversationNodeRecord PlatformRebaseNode(string id, string parent, List<string> children, bool system)
    {
        var node = Node(id, parent, "system", string.Empty, children);
        node.ContentType = "text";
        node.IsVisuallyHidden = true;
        node.IsTemporalTurn = true;
        node.RebaseSystemMessage = system;
        node.RebaseDeveloperMessage = !system;
        SetTurnIdentity(node);
        return node;
    }

    private static void SetTurnIdentity(ConversationNodeRecord node)
    {
        node.RequestId = "request-1";
        node.TurnExchangeId = "turn-1";
    }

    private static void Run(List<Stage2TestCaseResult> tests, int number, string name, Action test)
    {
        try
        {
            test();
            tests.Add(new Stage2TestCaseResult { Number = number, Name = name, Passed = true, Details = "PASS" });
        }
        catch (Exception ex)
        {
            tests.Add(new Stage2TestCaseResult { Number = number, Name = name, Passed = false, Details = ex.Message });
        }
    }

    private static void AssertPipelineAccepted(Stage2PipelineResult result)
    {
        if (!result.Success || result.PayloadBytes is null || result.Provenance is null)
        {
            throw new InvalidOperationException($"Expected signed dry-run transaction, got {result.ReasonCode}: {result.Message}");
        }
    }

    private static void AssertPipelineRejected(Stage2PipelineResult result, string expectedCode)
    {
        if (result.Success || !result.ReasonCode.Equals(expectedCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected {expectedCode}, got {result.ReasonCode}: {result.Message}");
        }
    }

    private static void AssertVerifierAccepted(CodexTestVerificationResult result)
    {
        if (!result.Accepted || !result.Disposition.Equals("ACCEPTED_FOR_TEST_SINK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected test-sink acceptance, got {result.ReasonCode}: {result.Message}");
        }
    }

    private static void AssertVerifierRejected(CodexTestVerificationResult result, params string[] expectedCodes)
    {
        if (result.Accepted || !result.Disposition.Equals("REJECTED", StringComparison.Ordinal) ||
            !expectedCodes.Contains(result.ReasonCode, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Expected rejection [{string.Join(",", expectedCodes)}], got {result.Disposition}/{result.ReasonCode}: {result.Message}");
        }
    }

    private static void AssertEnvelopeRejected(Stage2EnvelopeValidationResult result, string expectedCode)
    {
        if (result.Valid || !result.ReasonCode.Equals(expectedCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected envelope rejection {expectedCode}, got {result.ReasonCode}: {result.Reason}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public Fixture(bool registerKey = true)
        {
            Now = new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero);
            Signer = new EphemeralStage2ProvenanceSigner("stage2-test-key");
            Registry = new Stage2PublicKeyRegistry(Path.Combine(_directory.Path, "keys.json"));
            if (registerKey)
            {
                Registry.AddOrUpdate(Signer, now: Now);
            }

            IntakeLedgerPath = Path.Combine(_directory.Path, "intake-ledger.json");
            Pipeline = new Stage2DryRunPipeline(
                Signer,
                new Stage2ReplayLedger(Path.Combine(_directory.Path, "outbound-ledger.json")),
                Path.Combine(_directory.Path, "transactions"));
            Wake = Stage2DryRunPipeline.PrepareSyntheticWake(
                "conversation", "parent", ["root", "parent"], "tab|chatgpt.com/c/conversation",
                "wake-token", "CGPT-REPORT-20260716-110000-sc1999-source.md", "SC2000-20260716-120000");
            Wake.WakeMessageId = "wake";
            Wake.WakeParentMessageId = "parent";
            Wake.WakeCreatedAtUtc = Now.AddSeconds(-4);
            Wake.Status = "synthetic-wake-posted";

            var envelope = BuildEnvelope();
            Snapshot = new ConversationLineageSnapshot
            {
                ConversationId = "conversation",
                CurrentNode = "response",
                BrowserTabIdentity = Wake.BrowserTabIdentity,
                ApiVerified = true,
                ApiStatusCode = 200,
                BrowserBackendAgree = true,
                BrowserVisibleMessageIds = ["wake", "response"],
                Nodes = new Dictionary<string, ConversationNodeRecord>(StringComparer.Ordinal)
                {
                    ["root"] = Node("root", string.Empty, "system", "root", ["parent"]),
                    ["parent"] = Node("parent", "root", "assistant", "visible terminal", ["wake"]),
                    ["wake"] = Node("wake", "parent", "user", Wake.WakeToken, ["response"]),
                    ["response"] = Node("response", "wake", "assistant", envelope)
                }
            };
            Response = new AssistantResponseObservation
            {
                MessageId = "response",
                ParentMessageId = "wake",
                Role = "assistant",
                Content = envelope,
                Complete = true,
                OnCurrentPath = true,
                WakeToken = Wake.WakeToken,
                SourceReport = Wake.IntendedSourceReport,
                CaptureMethod = BranchLineageSafetyService.AuthorizedCaptureMethod,
                FallbackBody = false,
                WholePageCaptureUsed = false,
                ApiVerified = true,
                SelectedAssistantIndex = 0,
                AssistantSelectionAmbiguous = false,
                CurrentNodeAtCapture = "response",
                CreatedAtUtc = Now.AddSeconds(-1)
            };
        }

        public DateTimeOffset Now { get; }
        public EphemeralStage2ProvenanceSigner Signer { get; }
        public Stage2PublicKeyRegistry Registry { get; }
        public string IntakeLedgerPath { get; }
        public Stage2DryRunPipeline Pipeline { get; }
        public WakeTransactionRecord Wake { get; }
        public ConversationLineageSnapshot Snapshot { get; }
        public AssistantResponseObservation Response { get; }

        public Stage2PipelineResult Build(Stage2FaultPoint fault = Stage2FaultPoint.None)
        {
            return Pipeline.BuildSignedDryRunTransaction(
                Wake, Snapshot, Response, ThreadId,
                new Stage2BuildIdentity
                {
                    SourceCommit = new string('a', 40),
                    SourceTreeSha256 = new string('b', 64),
                    ExecutableSha256 = new string('c', 64),
                    ConfigurationSha256 = new string('d', 64)
                },
                Now,
                fault);
        }

        public CodexStage2TestVerifier CreateVerifier()
        {
            return new CodexStage2TestVerifier(Registry, new Stage2ReplayLedger(IntakeLedgerPath), ThreadId);
        }

        public Stage2CodexTestSink CreateSink() => new(CreateVerifier());

        public CodexTestVerificationResult Verify(byte[] payload) => CreateVerifier().Verify(payload, Now);

        public void Dispose()
        {
            Signer.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dcs-watcher-stage2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Test evidence can be removed on the next run if another process briefly holds it.
            }
        }
    }

    private sealed class P384TestSigner : IStage2ProvenanceSigner
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        public P384TestSigner(string keyId)
        {
            KeyId = keyId;
            PublicKeySpki = _key.ExportSubjectPublicKeyInfo();
            PublicKeyFingerprintSha256 = Stage2Crypto.Sha256Hex(PublicKeySpki);
        }

        public string KeyId { get; }
        public string PublicKeyFingerprintSha256 { get; }
        public byte[] PublicKeySpki { get; }
        public byte[] Sign(byte[] canonicalUnsignedProvenance) =>
            _key.SignData(canonicalUnsignedProvenance, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        public void Dispose() => _key.Dispose();
    }
}
