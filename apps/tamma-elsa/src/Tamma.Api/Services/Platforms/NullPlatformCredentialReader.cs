using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-2 — null-object fallback used when the Story 29-2 secret
/// store DbContext factory is not registered (dev / test
/// environments without a secret store). Returns null for every
/// read — the resolver downstream interprets that as "no driver
/// available" and surfaces a deterministic
/// <see cref="NullGitPlatformDriver"/>-equivalent response (currently
/// just <c>null</c>; integration tests fall back to
/// <see cref="PlatformResult{T}.ServiceUnavailable"/>).
///
/// <para>Mirrors the
/// <c>NoSecretStoreAlertChannelSecretReader</c> pattern from
/// Story 1.5-37 — same shape, same intent.</para>
/// </summary>
public sealed class NullPlatformCredentialReader : IPlatformCredentialReader
{
    public Task<string?> ReadActivePlaintextAsync(
        string scope,
        Guid? tenantId,
        string name,
        CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
