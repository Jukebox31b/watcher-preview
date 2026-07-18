using System.Text;
using System.Text.Json;
using DcsWatcherV2.Models;
using DcsWatcherV2.Security;
using DcsWatcherV2.Services;

if (args.Length == 6 && args[0].Equals("verify-offline-regression", StringComparison.OrdinalIgnoreCase))
{
    return RunOfflineRegression(args);
}
if (args.Length == 5 && args[0].Equals("verify-manual-pilot", StringComparison.OrdinalIgnoreCase))
{
    return RunManualPilot(args);
}
if (args.Length == 5 && args[0].Equals("verify-limited-automatic", StringComparison.OrdinalIgnoreCase))
{
    return RunLimitedAutomatic(args);
}

Console.Error.WriteLine("Usage: verify-offline-regression <frame-path> <signed-policy-path> <result-path> <fixture-root> <installation-security-root>");
Console.Error.WriteLine("   or: verify-manual-pilot <frame-path> <signed-policy-path> <result-path> <installation-security-root>");
Console.Error.WriteLine("   or: verify-limited-automatic <frame-path> <signed-policy-path> <result-path> <installation-security-root>");
return 64;

static int RunOfflineRegression(string[] args)
{
    var framePath = Path.GetFullPath(args[1]);
    var policyPath = Path.GetFullPath(args[2]);
    var resultPath = Path.GetFullPath(args[3]);
    var fixtureRoot = Path.GetFullPath(args[4]);
    var installationSecurityRoot = Path.GetFullPath(args[5]);
    Stage3IntakeResult result;
    try
    {
        var policyBytes = File.ReadAllBytes(policyPath);
        var trust = RequireInstallationTrust(installationSecurityRoot);
        var policyService = Stage3IntakePolicyService.CreateOfflineRegression(fixtureRoot, trust);
        var policyValidation = policyService.ValidatePinned(policyBytes, DateTimeOffset.UtcNow);
        if (!policyValidation.Accepted || policyValidation.Policy is null)
            throw new InvalidDataException($"{policyValidation.ReasonCode}: {policyValidation.Message}");
        var config = policyValidation.Policy.Configuration;
        using var signer = WindowsCngStage2ProvenanceSigner.OpenExisting(
            config.IntakeCheckpointSignerKeyId,
            config.IntakeCheckpointCngKeyName);
        result = new Stage3CodexIntakeGate(policyService, trust).ProcessFrame(
            File.ReadAllBytes(framePath), policyBytes, signer, DateTimeOffset.UtcNow);
    }
    catch (Exception ex)
    {
        result = Failure(ex);
    }
    WriteResult(resultPath, result);
    return result.Disposition.Equals("ACCEPTED_FOR_TEST_SINK", StringComparison.Ordinal) ? 0 : 1;
}

static int RunManualPilot(string[] args)
{
    var framePath = Path.GetFullPath(args[1]);
    var policyPath = Path.GetFullPath(args[2]);
    var resultPath = Path.GetFullPath(args[3]);
    var installationSecurityRoot = Path.GetFullPath(args[4]);
    Stage3IntakeResult result;
    try
    {
        var policyBytes = File.ReadAllBytes(policyPath);
        var trust = RequireInstallationTrust(installationSecurityRoot);
        var policyService = new Stage3IntakePolicyService(trust);
        var policyValidation = policyService.ValidatePinned(policyBytes, DateTimeOffset.UtcNow);
        if (!policyValidation.Accepted || policyValidation.Policy is null)
            throw new InvalidDataException($"{policyValidation.ReasonCode}: {policyValidation.Message}");
        var intakeConfiguration = policyValidation.Policy.Configuration;
        var appConfig = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllBytes(intakeConfiguration.ConfigurationTemplatePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Active Watcher configuration is empty.");
        if (!WatcherSafetyPolicy.CanRunStage3ManualPilot(appConfig, out var safetyReason))
            throw new InvalidOperationException(safetyReason);
        if (!appConfig.CodexThreadId.Equals(intakeConfiguration.ExpectedDirectorThreadId, StringComparison.Ordinal) ||
            !trust.IsDestinationApproved(appConfig.CodexThreadId))
            throw new InvalidOperationException("The active Codex thread does not match the installation-approved manual-pilot destination.");

        using var signer = WindowsCngStage2ProvenanceSigner.OpenExisting(
            intakeConfiguration.IntakeCheckpointSignerKeyId,
            intakeConfiguration.IntakeCheckpointCngKeyName);
        result = new Stage3CodexIntakeGate(policyService, trust).ProcessManualPilotFrame(
            File.ReadAllBytes(framePath),
            policyBytes,
            signer,
            DateTimeOffset.UtcNow,
            verified => DeliverVerifiedPilot(appConfig, verified));
    }
    catch (Exception ex)
    {
        result = Failure(ex);
    }
    WriteResult(resultPath, result);
    return result.Disposition.Equals("ACCEPTED_FOR_LIVE_CODEX", StringComparison.Ordinal) ? 0 : 1;
}

static int RunLimitedAutomatic(string[] args)
{
    var framePath = Path.GetFullPath(args[1]);
    var policyPath = Path.GetFullPath(args[2]);
    var resultPath = Path.GetFullPath(args[3]);
    var installationSecurityRoot = Path.GetFullPath(args[4]);
    Stage3IntakeResult result;
    try
    {
        var policyBytes = File.ReadAllBytes(policyPath);
        var trust = RequireInstallationTrust(installationSecurityRoot);
        var policyService = new Stage3IntakePolicyService(trust);
        var policyValidation = policyService.ValidatePinned(policyBytes, DateTimeOffset.UtcNow);
        if (!policyValidation.Accepted || policyValidation.Policy is null)
            throw new InvalidDataException($"{policyValidation.ReasonCode}: {policyValidation.Message}");
        var intakeConfiguration = policyValidation.Policy.Configuration;
        var appConfig = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllBytes(intakeConfiguration.ConfigurationTemplatePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Active Watcher configuration is empty.");
        if (!WatcherSafetyPolicy.CanRunStage4LimitedAutomatic(appConfig, out var safetyReason))
            throw new InvalidOperationException(safetyReason);
        if (!appConfig.CodexThreadId.Equals(intakeConfiguration.ExpectedDirectorThreadId, StringComparison.Ordinal) ||
            !trust.IsDestinationApproved(appConfig.CodexThreadId))
            throw new InvalidOperationException("The active Codex thread does not match the installation-approved Stage 4 destination.");

        using var signer = WindowsCngStage2ProvenanceSigner.OpenExisting(
            intakeConfiguration.IntakeCheckpointSignerKeyId,
            intakeConfiguration.IntakeCheckpointCngKeyName);
        result = new Stage3CodexIntakeGate(policyService, trust).ProcessLimitedAutomaticFrame(
            File.ReadAllBytes(framePath), policyBytes, signer, DateTimeOffset.UtcNow,
            verified => DeliverVerifiedPilot(appConfig, verified));
    }
    catch (Exception ex)
    {
        result = Failure(ex);
    }
    WriteResult(resultPath, result);
    return result.Disposition.Equals("ACCEPTED_FOR_LIVE_CODEX", StringComparison.Ordinal) ? 0 : 1;
}

static Stage3LiveDeliveryResult DeliverVerifiedPilot(AppConfig config, Stage3VerifiedPilotInstruction verified)
{
    var log = new LogService(new ConfigService());
    log.Initialize(config);
    var envelope = new UTF8Encoding(false, true).GetString(verified.EnvelopeBytes);
    var prompt = string.Join(Environment.NewLine, new[]
    {
        "VERIFIED WATCHER STAGE 3 MANUAL PILOT",
        $"transaction_id: {verified.Provenance.TransactionId}",
        $"envelope_sha256: {verified.Provenance.EnvelopeSha256}",
        $"provenance_sha256: {verified.ProvenanceSha256}",
        $"signer_fingerprint: {verified.SignerFingerprint}",
        $"destination_codex_thread: {verified.Provenance.DestinationCodexThreadId}",
        "acceptance_result: ACCEPTED_BY_INTAKE_GATE",
        string.Empty,
        envelope
    });
    var ipc = new CodexIpcClient().StartTurnAsync(config, prompt, log).GetAwaiter().GetResult();
    return ipc.Confirmed
        ? new Stage3LiveDeliveryResult(true, "OK", ipc.Message, ipc.TurnId ?? string.Empty)
        : new Stage3LiveDeliveryResult(false, "CODEX_IPC_NOT_CONFIRMED", ipc.Message);
}

static InstallationTrustContext RequireInstallationTrust(string securityRoot)
{
    var loaded = new InstallationTrustAnchorService().Load(securityRoot);
    return loaded.Accepted && loaded.Context is not null
        ? loaded.Context
        : throw new InvalidDataException($"{loaded.ReasonCode}: {loaded.Message}");
}

static Stage3IntakeResult Failure(Exception ex) => new()
{
    Disposition = "REJECTED",
    ReasonCode = "INTAKE_PROCESS_FAILURE",
    Message = ex.Message,
    ActionableInstructionExposed = false
};

static void WriteResult(string resultPath, Stage3IntakeResult result)
{
    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
    File.WriteAllBytes(resultPath, JsonSerializer.SerializeToUtf8Bytes(result, Stage2CanonicalJson.Options));
    Console.WriteLine($"{result.Disposition}:{result.ReasonCode}");
}
