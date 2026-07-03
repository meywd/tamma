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
/// loudly with a remediation message. Reads / deletes delegate to an
/// inner in-memory map (which, since no write ever succeeds, simply
/// reports the version as absent) so ambient BYOK read probes degrade to
/// "credential absent → platform fallback" rather than crashing.</para>
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

    private readonly ISecretStoreBackend _inner;
    private readonly ILogger<FailClosedSecretStoreBackend> _logger;

    public FailClosedSecretStoreBackend(
        ILogger<FailClosedSecretStoreBackend> logger)
        : this(new InMemorySecretStoreBackend(), logger)
    {
    }

    internal FailClosedSecretStoreBackend(
        ISecretStoreBackend inner,
        ILogger<FailClosedSecretStoreBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
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
    public Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default) =>
        _inner.GetVersionPlaintextAsync(secretId, versionNumber, ct);

    /// <inheritdoc />
    public Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default) =>
        _inner.DeleteVersionAsync(secretId, versionNumber, ct);
}
