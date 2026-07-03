using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-3 test double for <see cref="ISecretStore"/>. Implements only
/// the three methods <see cref="Tamma.Api.Services.Provisioning.V2.ProvisioningSecretRegistrar"/>
/// exercises (Create / Get / RetireVersion) and faithfully reproduces the
/// real <c>SecretStore</c> facade's contract so the registrar tests reflect
/// production behaviour:
///
/// <list type="bullet">
///   <item><description><see cref="CreateAsync"/> throws
///     <see cref="InvalidOperationException"/> on a duplicate
///     <c>(scope, tenant, name)</c>; mints <c>ActiveVersionNumber = 1</c>
///     when an initial plaintext is supplied.</description></item>
///   <item><description><see cref="RetireVersionAsync"/> throws
///     <see cref="KeyNotFoundException"/> when the secret is absent and
///     <see cref="InvalidOperationException"/> when asked to retire the
///     current active version (the facade's "no delete for the sole active
///     version" guard).</description></item>
/// </list>
///
/// Every mutating call is recorded for assertions. Read-only helpers not
/// used by the registrar throw <see cref="NotSupportedException"/> so an
/// accidental new dependency is caught loudly.
/// </summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, SecretMetadata> _store = new(StringComparer.Ordinal);

    public List<CreateSecretRequest> CreateCalls { get; } = new();
    public List<(SecretRef Reference, int VersionNumber)> RetireVersionCalls { get; } = new();

    /// <summary>Override to script a create failure (fail-loud tests).</summary>
    public Func<CreateSecretRequest, Task<SecretMetadata>>? OnCreate { get; set; }

    public Task<SecretMetadata> CreateAsync(
        CreateSecretRequest request, CancellationToken ct = default)
    {
        CreateCalls.Add(request);
        if (OnCreate is not null)
        {
            return OnCreate(request);
        }

        var key = new SecretRef(request.Scope, request.TenantId, request.Name).ToStorageKey();
        if (_store.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"A secret named '{request.Name}' already exists in scope {request.Scope}.");
        }

        var meta = SecretMetadataFactory.Create(
            request.Name,
            request.Scope,
            request.TenantId,
            request.Purpose,
            request.ConsumerRefs,
            request.OwnerUserId,
            request.RotationSchedule,
            DateTimeOffset.UtcNow);

        if (!string.IsNullOrEmpty(request.InitialPlaintext))
        {
            meta = meta with { ActiveVersionNumber = 1 };
        }

        _store[key] = meta;
        return Task.FromResult(meta);
    }

    public Task<SecretMetadata?> GetAsync(
        SecretRef reference, CancellationToken ct = default)
    {
        _store.TryGetValue(reference.ToStorageKey(), out var meta);
        return Task.FromResult<SecretMetadata?>(meta);
    }

    public Task<SecretMetadata> RetireVersionAsync(
        SecretRef reference, int versionNumber, CancellationToken ct = default)
    {
        RetireVersionCalls.Add((reference, versionNumber));

        var key = reference.ToStorageKey();
        if (!_store.TryGetValue(key, out var meta))
        {
            throw new KeyNotFoundException($"No secret matches {key}.");
        }

        // Faithful to the real facade: the sole active version cannot be
        // retired (there is no successor to fall back to; ISecretStore has no
        // row-delete). Register's idempotency + the reconciliation sweep cover
        // this in production.
        if (meta.ActiveVersionNumber == versionNumber)
        {
            throw new InvalidOperationException(
                "Cannot retire the active version. Rotate first so the successor " +
                "is in place before the current version is taken away.");
        }

        return Task.FromResult(meta);
    }

    /// <summary>Test helper — seed an already-registered secret (simulates a
    /// resumed / re-attempted provision that already minted the row).</summary>
    public void SeedActiveSecret(SecretRef reference)
    {
        var meta = SecretMetadataFactory.Create(
            reference.Name,
            reference.Scope,
            reference.TenantId,
            SecretPurpose.HmacSharedSecret,
            Array.Empty<ConsumerRef>(),
            Guid.Parse("00000000-0000-0000-0000-000000000030"),
            RotationSchedule.None,
            DateTimeOffset.UtcNow) with
        { ActiveVersionNumber = 1 };
        _store[reference.ToStorageKey()] = meta;
    }

    // ── Unused by the registrar ──────────────────────────────────────────
    public Task<IReadOnlyList<SecretMetadata>> ListAsync(
        SecretListFilter filter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<SecretMetadata> RotateAsync(
        SecretRef reference, RotateSecretRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<SecretVersion?> GetVersionAsync(
        SecretRef reference, int versionNumber, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
        SecretRef reference, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
