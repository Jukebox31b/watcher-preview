using DcsWatcherV2.Models;
using DcsWatcherV2.Services;

namespace DcsWatcherV2.Security;

public sealed class ProfileProvisioningService
{
    private readonly InstallationTrustAnchorService _trust;

    public ProfileProvisioningService(InstallationTrustAnchorService? trust = null)
    {
        _trust = trust ?? new InstallationTrustAnchorService();
    }

    public InstallationTrustResult ProvisionInstallation(
        string destinationCodexThreadId,
        string? securityRoot = null,
        DateTimeOffset? nowUtc = null) => _trust.Provision(new InstallationTrustProvisioningOptions
    {
        SecurityRoot = securityRoot,
        DestinationId = destinationCodexThreadId,
        NowUtc = nowUtc
    });

    public InstallationTrustResult LoadInstallation(string? securityRoot = null) => _trust.Load(securityRoot);

    public InstallationTrustResult RotatePolicyAuthority(string? securityRoot = null, DateTimeOffset? nowUtc = null) =>
        _trust.RotatePolicyKey(securityRoot, nowUtc);

    public InstallationTrustResult RevokePolicyAuthority(
        string keyId,
        string reason,
        string? securityRoot = null,
        DateTimeOffset? nowUtc = null) => _trust.RevokePolicyKey(keyId, reason, securityRoot, nowUtc);

    public InstallationTrustResult ApproveDestinations(
        IReadOnlyCollection<string> destinationIds,
        string? securityRoot = null,
        DateTimeOffset? nowUtc = null) => _trust.ReplaceApprovedDestinations(destinationIds, securityRoot, nowUtc);

    public InstallationTrustResult ExportPublicVerificationMaterial(string path, string? securityRoot = null) =>
        _trust.ExportPublicVerificationMaterial(path, securityRoot);

    public IStage2ProvenanceSigner OpenPolicySigner(InstallationTrustContext context) =>
        _trust.OpenActivePolicySigner(context);
}
