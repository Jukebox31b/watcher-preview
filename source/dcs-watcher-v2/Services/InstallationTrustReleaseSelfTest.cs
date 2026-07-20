using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;

namespace DcsWatcherV2.Services;

internal static class InstallationTrustReleaseSelfTest
{
    private static readonly string[] CommitFaultSteps =
    [
        "before-bundle-write",
        "after-bundle-write",
        "before-counter-advance",
        "after-counter-advance"
    ];

    public static void Run(int testNumber)
    {
        switch (testNumber)
        {
            case 1: FreshProvisioning(); return;
            case 2: NoPrivateMaterial(); return;
            case 3: AclAllowlistEnforced(); return;
            case 4: BundleTamperingRejected(); return;
            case 5: WrongBundleRejected(); return;
            case 6:
                UnknownPolicySignerRejected();
                PackagedEnvelopeAndStage3FixtureRemainValid();
                return;
            case 7: CrashConsistencyRotationAndRevocation(); return;
            case 8: DestinationChangeRejected(); return;
            case 9: MissingTrustAndJunctionEscapeRejected(); return;
            case 10: PublicExportContainsVerificationMaterialOnly(); return;
            default: throw new ArgumentOutOfRangeException(nameof(testNumber));
        }
    }

    private static void FreshProvisioning() => WithTrust((service, context, _) =>
    {
        var loaded = service.Load(context.SecurityRoot);
        if (!loaded.Accepted || loaded.Context is null ||
            !loaded.Context.IsDestinationApproved("synthetic-destination") ||
            loaded.Context.Bundle.Generation != 1)
            throw new InvalidOperationException($"Fresh installation trust did not validate: {loaded.ReasonCode}");
    });

    private static void NoPrivateMaterial() => WithTrust((_, context, _) =>
    {
        var text = File.ReadAllText(context.BundlePath, Encoding.UTF8);
        foreach (var forbidden in new[] { "private", "cng_key_name", "pkcs8", "secret" })
        {
            if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Installation trust serialized forbidden private-key metadata: {forbidden}");
        }
    });

    private static void AclAllowlistEnforced()
    {
        ArbitrarySidAclRejected();
        InheritedUnsafeAclRejected();
    }

    private static void ArbitrarySidAclRejected() => WithTrust((service, context, _) =>
    {
        var security = new FileInfo(context.BundlePath).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-1001"),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(context.BundlePath).SetAccessControl(security);
        var loaded = service.Load(context.SecurityRoot);
        if (loaded.Accepted || !loaded.ReasonCode.Equals("INSTALLATION_TRUST_ACL_INVALID", StringComparison.Ordinal))
            throw new InvalidOperationException("An arbitrary additional SID on installation trust was not rejected.");
    });

    private static void InheritedUnsafeAclRejected()
    {
        var parent = NewRoot();
        var root = Path.Combine(parent, "security");
        var service = new InstallationTrustAnchorService();
        InstallationTrustContext? context = null;
        var untrusted = new SecurityIdentifier("S-1-5-21-444444444-555555555-666666666-1002");
        try
        {
            Directory.CreateDirectory(parent);
            context = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = root,
                DestinationId = "synthetic-destination"
            }));
            var parentAcl = new DirectoryInfo(parent).GetAccessControl();
            parentAcl.AddAccessRule(new FileSystemAccessRule(
                untrusted,
                FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(parent).SetAccessControl(parentAcl);

            var rootAcl = new DirectoryInfo(root).GetAccessControl();
            rootAcl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(root).SetAccessControl(rootAcl);
            var inherited = new DirectoryInfo(root).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(rule => rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference.Equals(untrusted) &&
                    (rule.FileSystemRights & FileSystemRights.Write) != 0);
            if (!inherited) throw new InvalidOperationException("The unsafe inherited ACL test did not establish its precondition.");

            var loaded = service.Load(root);
            if (loaded.Accepted || !loaded.ReasonCode.Equals("INSTALLATION_TRUST_ACL_INVALID", StringComparison.Ordinal))
                throw new InvalidOperationException("Inherited unsafe write access was not rejected.");
        }
        finally
        {
            if (context is not null) service.DeleteForOfflineTests(context);
            TryDelete(parent);
        }
    }

    private static void BundleTamperingRejected() => WithTrust((service, context, _) =>
    {
        var bytes = File.ReadAllBytes(context.BundlePath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(context.BundlePath, bytes);
        var loaded = service.Load(context.SecurityRoot);
        if (loaded.Accepted || loaded.ReasonCode is not ("INSTALLATION_TRUST_NONCANONICAL" or "INSTALLATION_TRUST_LOAD_FAILED"))
            throw new InvalidOperationException($"Tampered trust bundle was not rejected exactly: {loaded.ReasonCode}");
    });

    private static void WrongBundleRejected()
    {
        var firstRoot = NewRoot();
        var secondRoot = NewRoot();
        var service = new InstallationTrustAnchorService();
        InstallationTrustContext? first = null;
        InstallationTrustContext? second = null;
        try
        {
            first = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = firstRoot,
                DestinationId = "synthetic-destination"
            }));
            second = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = secondRoot,
                DestinationId = "synthetic-destination"
            }));
            File.Copy(first.BundlePath, second.BundlePath, overwrite: true);
            var loaded = service.Load(second.SecurityRoot);
            if (loaded.Accepted || loaded.ReasonCode is not ("INSTALLATION_TRUST_COUNTER_IDENTITY_INVALID" or "EXTERNAL_MONOTONIC_DIGEST_MISMATCH"))
                throw new InvalidOperationException($"A bundle from another installation was accepted: {loaded.ReasonCode}");
        }
        finally
        {
            if (first is not null) service.DeleteForOfflineTests(first);
            if (second is not null) service.DeleteForOfflineTests(second);
            TryDelete(firstRoot);
            TryDelete(secondRoot);
        }
    }

    private static void UnknownPolicySignerRejected() => WithTrust((service, context, _) =>
    {
        var nowUtc = DateTimeOffset.UtcNow;
        using var signer = service.OpenActivePolicySigner(context);
        var policyService = new Stage3IntakePolicyService(context);
        var signedPolicy = policyService.Sign(new Stage3CodexIntakePolicyV1
        {
            PolicyGeneration = context.MinimumPolicyGeneration,
            AllowedSourceCommit = new string('a', 40),
            AllowedCompilerIdentity = "Release self-test",
            IssueTimeUtc = nowUtc.AddMinutes(-1).ToUniversalTime().ToString("O"),
            ExpiryTimeUtc = nowUtc.AddMinutes(5).ToUniversalTime().ToString("O"),
            Configuration = new Stage3CodexIntakeConfiguration
            {
                ExpectedDirectorThreadId = "synthetic-destination"
            }
        }, signer);
        var policy = JsonSerializer.Deserialize<Stage3CodexIntakePolicyV1>(signedPolicy, Stage2CanonicalJson.Options)!;
        policy.SignerKeyId = "unknown-installation-policy-key";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(policy, Stage2CanonicalJson.Options);
        var result = policyService.ValidatePinned(bytes, nowUtc);
        if (result.Accepted || !result.ReasonCode.Equals("INTAKE_POLICY_SIGNER_INVALID", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown policy signer was not rejected: {result.ReasonCode}");
    });

    private static void PackagedEnvelopeAndStage3FixtureRemainValid()
    {
        const string taskId = "SC-STAGE3-20260716-170000";
        const string envelope = """
<<<DCS_CODEX_TASK_V1>>>
task_id: SC-STAGE3-20260716-170000
source_report: CGPT-REPORT-20260716-170000-stage3-offline-fixture.md
<<<END_DCS_CODEX_TASK_V1>>>
""";
        var lfEnvelope = envelope
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        foreach (var candidate in new[] { lfEnvelope, lfEnvelope.Replace("\n", "\r\n", StringComparison.Ordinal) })
        {
            if (!BranchLineageSafetyService.ReadField(candidate, "task_id").Equals(taskId, StringComparison.Ordinal) ||
                !BranchLineageSafetyService.ReadField(candidate, "source_report").Equals(Stage3RegressionFixture.SourceReport, StringComparison.Ordinal))
                throw new InvalidOperationException("Packaged raw LF/CRLF envelope field extraction failed before Stage 3 fixture construction.");
        }

        try
        {
            using var fixture = new Stage3RegressionFixture();
            var fixtureEnvelope = Encoding.UTF8.GetString(fixture.EnvelopeBytes);
            if (!BranchLineageSafetyService.ReadField(fixtureEnvelope, "task_id").Equals(taskId, StringComparison.Ordinal) ||
                !BranchLineageSafetyService.ReadField(fixtureEnvelope, "source_report").Equals(Stage3RegressionFixture.SourceReport, StringComparison.Ordinal) ||
                !fixture.Transaction.Provenance.TaskId.Equals(taskId, StringComparison.Ordinal) ||
                !fixture.Transaction.Provenance.SourceReport.Equals(Stage3RegressionFixture.SourceReport, StringComparison.Ordinal))
                throw new InvalidOperationException("The constructed Stage 3 transaction does not preserve its envelope identity fields.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Packaged Stage 3 fixture transaction diagnostic failed after raw envelope extraction succeeded: {ex.Message}", ex);
        }
    }

    private static void CrashConsistencyRotationAndRevocation()
    {
        foreach (var step in CommitFaultSteps)
        {
            ProvisioningInterruptionRecovers(step);
            RotationInterruptionRecovers(step);
            RevocationInterruptionRecovers(step);
        }
        NormalRotationRevocationAndRollbackRejection();
    }

    private static void ProvisioningInterruptionRecovers(string step)
    {
        var root = NewRoot();
        var interrupted = new InstallationTrustAnchorService { FaultStopAfterStep = step };
        InstallationTrustContext? recoveredContext = null;
        try
        {
            var result = interrupted.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = root,
                DestinationId = "synthetic-destination"
            });
            if (result.Accepted) throw new InvalidOperationException($"Provisioning did not stop at {step}.");
            var recovery = new InstallationTrustAnchorService();
            recoveredContext = Require(recovery.Load(root));
            if (recoveredContext.Bundle.Generation != 1)
                throw new InvalidOperationException($"Provisioning recovery at {step} produced another generation.");
            var repeated = recovery.Load(root);
            if (!repeated.Accepted || repeated.Context?.Bundle.BundleDigestSha256 != recoveredContext.Bundle.BundleDigestSha256)
                throw new InvalidOperationException($"Provisioning recovery at {step} was not idempotent.");
        }
        finally
        {
            if (recoveredContext is not null) new InstallationTrustAnchorService().DeleteForOfflineTests(recoveredContext);
            TryDelete(root);
        }
    }

    private static void RotationInterruptionRecovers(string step)
    {
        var root = NewRoot();
        var service = new InstallationTrustAnchorService();
        InstallationTrustContext? cleanup = null;
        try
        {
            cleanup = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = root,
                DestinationId = "synthetic-destination"
            }));
            var interrupted = new InstallationTrustAnchorService { FaultStopAfterStep = step };
            var result = interrupted.RotatePolicyKey(root);
            if (result.Accepted) throw new InvalidOperationException($"Rotation did not stop at {step}.");

            var intentPath = Path.Combine(root, InstallationTrustAnchorService.CommitIntentFileName);
            var intentBytes = File.ReadAllBytes(intentPath);
            var recovery = new InstallationTrustAnchorService();
            cleanup = Require(recovery.Load(root));
            if (cleanup.Bundle.Generation != 2)
                throw new InvalidOperationException($"Rotation recovery at {step} did not roll forward exactly once.");

            File.WriteAllBytes(intentPath, intentBytes);
            var repeated = recovery.Load(root);
            if (!repeated.Accepted || repeated.Context?.Bundle.Generation != 2 || File.Exists(intentPath))
                throw new InvalidOperationException($"Repeated rotation recovery at {step} was not idempotent.");
        }
        finally
        {
            if (cleanup is not null) service.DeleteForOfflineTests(cleanup);
            TryDelete(root);
        }
    }

    private static void RevocationInterruptionRecovers(string step)
    {
        var root = NewRoot();
        var service = new InstallationTrustAnchorService();
        InstallationTrustContext? cleanup = null;
        try
        {
            var initial = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = root,
                DestinationId = "synthetic-destination"
            }));
            var oldKeyId = initial.ActivePolicySigner.KeyId;
            cleanup = Require(service.RotatePolicyKey(root));
            var interrupted = new InstallationTrustAnchorService { FaultStopAfterStep = step };
            var result = interrupted.RevokePolicyKey(oldKeyId, "interrupted offline revocation", root);
            if (result.Accepted) throw new InvalidOperationException($"Revocation did not stop at {step}.");

            var recovery = new InstallationTrustAnchorService();
            cleanup = Require(recovery.Load(root));
            var oldKey = cleanup.Bundle.PolicySigningKeys.Single(key => key.KeyId.Equals(oldKeyId, StringComparison.Ordinal));
            if (cleanup.Bundle.Generation != 3 || !oldKey.Status.Equals("revoked", StringComparison.Ordinal))
                throw new InvalidOperationException($"Revocation recovery at {step} did not roll forward exactly once.");
            var repeated = recovery.Load(root);
            if (!repeated.Accepted || repeated.Context?.Bundle.Generation != 3)
                throw new InvalidOperationException($"Revocation recovery at {step} was not idempotent.");
        }
        finally
        {
            if (cleanup is not null) service.DeleteForOfflineTests(cleanup);
            TryDelete(root);
        }
    }

    private static void NormalRotationRevocationAndRollbackRejection() => WithTrust((service, context, _) =>
    {
        var generationOne = File.ReadAllBytes(context.BundlePath);
        var oldKeyId = context.ActivePolicySigner.KeyId;
        var rotated = service.RotatePolicyKey(context.SecurityRoot);
        if (!rotated.Accepted || rotated.Context is null || rotated.Context.ActivePolicySigner.KeyId.Equals(oldKeyId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Policy-key rotation failed: {rotated.ReasonCode}");
        var generationTwo = File.ReadAllBytes(rotated.Context.BundlePath);
        File.WriteAllBytes(rotated.Context.BundlePath, generationOne);
        var rolledBack = service.Load(context.SecurityRoot);
        if (rolledBack.Accepted || !rolledBack.ReasonCode.Equals("EXTERNAL_GENERATION_ROLLBACK", StringComparison.Ordinal))
            throw new InvalidOperationException($"A prior signed trust generation was not rejected: {rolledBack.ReasonCode}");
        File.WriteAllBytes(rotated.Context.BundlePath, generationTwo);
        var revoked = service.RevokePolicyKey(oldKeyId, "offline rotation test", context.SecurityRoot);
        if (!revoked.Accepted || revoked.Context is null ||
            revoked.Context.Bundle.PolicySigningKeys.Single(key => key.KeyId.Equals(oldKeyId, StringComparison.Ordinal)).Status != "revoked")
            throw new InvalidOperationException($"Retired policy key was not revoked: {revoked.ReasonCode}");
    });

    private static void DestinationChangeRejected() => WithTrust((service, context, unusedRoot) =>
    {
        var changed = service.ReplaceApprovedDestinations(["replacement-destination"], context.SecurityRoot);
        if (!changed.Accepted || changed.Context is null || changed.Context.IsDestinationApproved("synthetic-destination") ||
            !changed.Context.IsDestinationApproved("replacement-destination"))
            throw new InvalidOperationException($"Destination rotation failed: {changed.ReasonCode}");
        using var signer = service.OpenActivePolicySigner(changed.Context);
        var policy = new Stage3CodexIntakePolicyV1
        {
            Configuration = new Stage3CodexIntakeConfiguration { ExpectedDirectorThreadId = "synthetic-destination" }
        };
        try
        {
            _ = new Stage3IntakePolicyService(changed.Context).Sign(policy, signer);
            throw new InvalidOperationException("Removed destination remained authorizable.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not approved", StringComparison.Ordinal))
        {
        }
        _ = unusedRoot;
    });

    private static void MissingTrustAndJunctionEscapeRejected()
    {
        MissingTrustRejected();
        JunctionEscapeRejected();
    }

    private static void MissingTrustRejected()
    {
        var root = NewRoot();
        try
        {
            var loaded = new InstallationTrustAnchorService().Load(root);
            if (loaded.Accepted || !loaded.ReasonCode.Equals("INSTALLATION_TRUST_MISSING", StringComparison.Ordinal))
                throw new InvalidOperationException($"Missing installation trust was not rejected: {loaded.ReasonCode}");
        }
        finally { TryDelete(root); }
    }

    private static void JunctionEscapeRejected()
    {
        var sourceRoot = NewRoot();
        var linkParent = NewRoot();
        var junction = Path.Combine(linkParent, "security-junction");
        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, ".git"));
            Directory.CreateDirectory(linkParent);
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c mklink /J \"{junction}\" \"{sourceRoot}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not create the offline junction fixture.");
            if (!process.WaitForExit(10000) || process.ExitCode != 0 || !Directory.Exists(junction))
                throw new InvalidOperationException("Could not establish the offline junction fixture: " + process.StandardError.ReadToEnd());

            var result = new InstallationTrustAnchorService().Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = Path.Combine(junction, "security"),
                DestinationId = "synthetic-destination"
            });
            if (result.Accepted || !result.ReasonCode.Equals("INSTALLATION_TRUST_SOURCE_PATH_REJECTED", StringComparison.Ordinal))
                throw new InvalidOperationException($"A junction escape into a Git source tree was not rejected: {result.ReasonCode}");
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction); }
            catch { }
            TryDelete(linkParent);
            TryDelete(sourceRoot);
        }
    }

    private static void PublicExportContainsVerificationMaterialOnly() => WithTrust((service, context, root) =>
    {
        var export = Path.Combine(root, "export", "installation-public.json");
        var result = service.ExportPublicVerificationMaterial(export, context.SecurityRoot);
        if (!result.Accepted || !File.Exists(export))
            throw new InvalidOperationException($"Public trust export failed: {result.ReasonCode}");
        var bytes = File.ReadAllBytes(export);
        if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(context.BundlePath)))
            throw new InvalidOperationException("Public export differs from the validated public trust bundle.");
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("private", StringComparison.OrdinalIgnoreCase) || text.Contains("cng_key_name", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Public export contains private-key metadata.");
    });

    private static void WithTrust(Action<InstallationTrustAnchorService, InstallationTrustContext, string> test)
    {
        var root = NewRoot();
        var service = new InstallationTrustAnchorService();
        InstallationTrustContext? context = null;
        try
        {
            context = Require(service.Provision(new InstallationTrustProvisioningOptions
            {
                SecurityRoot = root,
                DestinationId = "synthetic-destination",
                NowUtc = DateTimeOffset.UtcNow
            }));
            test(service, context, root);
        }
        finally
        {
            var latest = service.Load(root);
            if (latest.Accepted && latest.Context is not null) context = latest.Context;
            if (context is not null) service.DeleteForOfflineTests(context);
            TryDelete(root);
        }
    }

    private static InstallationTrustContext Require(InstallationTrustResult result) =>
        result.Accepted && result.Context is not null
            ? result.Context
            : throw new InvalidOperationException($"{result.ReasonCode}: {result.Message}");

    private static string NewRoot() => Path.Combine(
        Path.GetTempPath(),
        "DcsWatcherV2-Stage3-InstallationTrust-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
