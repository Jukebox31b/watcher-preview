using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

[Obsolete("Compiled intake pins were removed. Supply a validated InstallationTrustContext.")]
public static class Stage3CompiledIntakePins
{
    public const string PolicySignerKeyId = "";
    public const string PolicySignerPublicKeySpkiBase64 = "";
    public const string PolicySignerFingerprintSha256 = "";
    public const string TrustRootFingerprintSha256 = "";
    public const string ExpectedDirectorThreadId = "";
    public const long MinimumPolicyGeneration = 1;
    public const string PolicyCounterInstanceId = "";

    public static string PolicyCounterScopePath => throw Removed();
    public static Stage2PublicKeyRecord PolicySignerPublicKey => throw Removed();

    private static InvalidOperationException Removed() => new(
        "Compiled intake trust is unavailable. Provision and validate installation trust before intake.");
}
