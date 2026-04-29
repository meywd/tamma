namespace Tamma.Api.Services.Onboarding;

/// <summary>
/// Null-seam implementation of <see cref="IPlatformConnectService"/>.
/// Registered when <see cref="Tamma.Api.Services.Secrets.Reveal.ISecretRevealService"/>
/// is not configured (test environments + dev hosts without a Postgres
/// connection string for the secret cabinet).
///
/// <para>Every method returns a deterministic
/// "service unavailable" shape so the picker UI can render the
/// onboarding endpoints without 500-ing. The endpoint layer surfaces
/// the failure as a 400 with hint <c>"secret_store_unavailable"</c>;
/// the operator must wire <c>ConnectionStrings:ControlPlane</c> /
/// <c>ConnectionStrings:SecretStore</c> to enable real connects.</para>
/// </summary>
public sealed class NullPlatformConnectService : IPlatformConnectService
{
    public Task<PlatformConnectResult> ConnectAsync(
        PlatformConnectRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(PlatformConnectResult.Failure(
            "secret_store_unavailable",
            "Platform connect requires the secret cabinet to be configured. " +
            "Set ConnectionStrings:SecretStore or ConnectionStrings:ControlPlane."));
    }

    public Task<IReadOnlyList<PlatformConnectionDto>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PlatformConnectionDto>>(
            Array.Empty<PlatformConnectionDto>());
    }
}
