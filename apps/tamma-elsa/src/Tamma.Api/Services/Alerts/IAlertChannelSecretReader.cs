using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 1.5-37 (Wave C.1) — read-only seam over the secret store
/// that the three credentialled channels (Slack / PagerDuty /
/// webhook) use to resolve their sensitive values at delivery time.
///
/// <para><b>Why a dedicated interface</b>: channel delivery must not
/// import the full <see cref="ISecretStore"/> surface (write + rotate
/// + version probes) — that would be an authorisation hazard in
/// unit tests. The reader is the minimal contract: "give me the
/// active-version plaintext for this secret id, or throw if it
/// doesn't exist". Tests stub this with an in-memory dictionary and
/// never touch the real cabinet.</para>
///
/// <para>The default implementation
/// (<see cref="DefaultAlertChannelSecretReader"/>) resolves through
/// Story 29-2's <see cref="SecretsDbContext"/> + Story 29-1's
/// <see cref="ISecretStoreBackend"/>, exactly matching the pattern
/// used by <see cref="Tamma.Api.Services.Secrets.Stopgap.RuntimeSecretResolver"/>.</para>
/// </summary>
public interface IAlertChannelSecretReader
{
    /// <summary>
    /// Resolve the active plaintext for <paramref name="secretId"/>.
    /// Returns null when the secret exists but has no active
    /// version or its ciphertext has been scrubbed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No secret row matches
    /// <paramref name="secretId"/>.</exception>
    Task<string?> GetPlaintextAsync(Guid secretId, CancellationToken ct);
}

/// <summary>
/// Fallback implementation used when the Story 29-2 secret store
/// DbContext factory is not registered. Channels that require a
/// credential will get a clean <see cref="InvalidOperationException"/>
/// on delivery rather than a DI-time crash at startup. Tests that
/// don't use the secret-backed channels (email-only paths) never
/// hit this code.
/// </summary>
public sealed class NoSecretStoreAlertChannelSecretReader : IAlertChannelSecretReader
{
    public Task<string?> GetPlaintextAsync(Guid secretId, CancellationToken ct) =>
        throw new InvalidOperationException(
            "Secret store is not configured in this environment. " +
            "Alert channels requiring a credential cannot deliver. " +
            "Wire AddTammaPostgresSecrets (Story 29-2) to enable.");
}

/// <summary>
/// Default implementation — reads the active version's plaintext
/// via <see cref="SecretsDbContext"/> + <see cref="ISecretStoreBackend"/>.
/// </summary>
public sealed class DefaultAlertChannelSecretReader : IAlertChannelSecretReader
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;

    public DefaultAlertChannelSecretReader(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        _secretsFactory = secretsFactory;
        _backend = backend;
    }

    public async Task<string?> GetPlaintextAsync(Guid secretId, CancellationToken ct)
    {
        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ctx.Secrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);

        if (row is null)
            throw new KeyNotFoundException(
                $"No secret cabinet row found for id {secretId:D}.");

        if (row.ActiveVersionNumber <= 0)
            return null;

        return await _backend
            .GetVersionPlaintextAsync(row.Id, row.ActiveVersionNumber, ct)
            .ConfigureAwait(false);
    }
}
