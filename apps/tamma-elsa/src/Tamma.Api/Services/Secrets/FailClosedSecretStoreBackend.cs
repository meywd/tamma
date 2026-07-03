using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Fail-closed <see cref="ISecretStoreBackend"/> registered when a
/// deployment has a real secret-store database configured but the
/// envelope KEK (<c>TAMMA_SECRET_STORE_KEK_PRIMARY</c>) is missing.
///
/// <para>The persistent Postgres backend cannot be constructed without
/// the KEK, and silently substituting the volatile
/// <see cref="InMemorySecretStoreBackend"/> for real (tenant BYOK)
/// secrets is the exact silent failure this project forbids: the
/// metadata rows persist while the ciphertext evaporates on restart. So
/// instead of writing plaintext to volatile memory, every WRITE throws
/// loudly with a remediation message.</para>
///
/// <para><b>Reads return absent, never throw</b>: since no write ever
/// succeeds, there is nothing to read, so
/// <see cref="GetVersionPlaintextAsync"/> returns <c>null</c> (the
/// contract's "no readable plaintext" signal) rather than delegating to a
/// map that would raise <see cref="KeyNotFoundException"/>. Returning
/// <c>null</c> keeps ambient BYOK read probes on the
/// "credential absent → platform fallback" path AND means a future caller
/// that does not catch <see cref="KeyNotFoundException"/> cannot 500 off a
/// fail-closed read. <see cref="DeleteVersionAsync"/> is a no-op (there is
/// never anything to scrub). The three current read seams
/// (<c>CabinetTenantProviderKeyReader</c>,
/// <c>SecretStorePlatformCredentialReader</c>,
/// <c>RuntimeSecretResolver</c>) already treat both <c>null</c> and a
/// thrown <see cref="KeyNotFoundException"/> as absent, so this is safe.</para>
///
/// <para>This mirrors the env-gated production hard-fail used by
/// <c>TenantSecretProtector.FromConfiguration</c> (Cranl:EncryptionKey
/// is REQUIRED in production; the silent fallback is dev-only). Startup
/// itself is NOT broken — the host boots and health endpoints answer;
/// the failure surfaces at the first secret write with a clear cause.</para>
/// </summary>
public sealed class FailClosedSecretStoreBackend : ISecretStoreBackend
{
    /// <summary>
    /// Stable machine-readable prefix so callers / tests can assert the
    /// fail-closed cause without matching the whole message.
    /// </summary>
    public const string ReasonCode = "persistent_secret_backend_not_configured";

    private readonly ILogger<FailClosedSecretStoreBackend> _logger;

    public FailClosedSecretStoreBackend(
        ILogger<FailClosedSecretStoreBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PutVersionAsync(
        Guid secretId,
        int versionNumber,
        string plaintext,
        CancellationToken ct = default)
    {
        _logger.LogError(
            "Refusing to persist secret {SecretId} v{Version}: the persistent " +
            "secret backend is not configured. Set {KekEnv} (and a secret-store " +
            "connection string) to enable envelope-encrypted storage; the volatile " +
            "in-memory backend must never silently back a real secret.",
            secretId, versionNumber, Postgres.EnvKekProvider.PrimaryEnvVar);

        throw new InvalidOperationException(
            $"{ReasonCode}: cannot store secret {secretId} v{versionNumber} — the " +
            $"persistent secret backend is not configured. Set " +
            $"{Postgres.EnvKekProvider.PrimaryEnvVar} to enable the Postgres " +
            $"envelope-encrypted store. (Refusing to persist plaintext to volatile " +
            $"in-memory storage.)");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always returns <c>null</c> (absent) — a fail-closed backend never
    /// persists anything, so there is never plaintext to return. Never throws
    /// <see cref="KeyNotFoundException"/>, so a caller that does not catch it
    /// cannot 500 off a fail-closed read.
    /// </remarks>
    public Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <inheritdoc />
    /// <remarks>No-op — nothing was ever persisted, so there is nothing to
    /// scrub. Idempotent, never throws.</remarks>
    public Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}
