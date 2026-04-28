using System.Collections.Concurrent;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// In-process placeholder <see cref="ISecretStoreBackend"/> used by
/// tests + dev wiring until Story 29-2 lands the Postgres
/// envelope-encrypted backend. Stores plaintext in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// <c>(secretId, versionNumber)</c>; honours the
/// <see cref="DeleteVersionAsync"/> scrub semantics by leaving the
/// row in place but nulling the payload (matches the audit-log
/// retention policy).
///
/// <para>Thread-safe; cheap to construct. <b>Not</b> for production
/// — there is no encryption, no persistence, and no rotation safety.
/// Constructor-callers in non-test code should be pointed at the
/// Story 29-2 backend instead.</para>
/// </summary>
public sealed class InMemorySecretStoreBackend : ISecretStoreBackend
{
    private readonly ConcurrentDictionary<(Guid SecretId, int Version), string?> _values =
        new();

    public Task PutVersionAsync(
        Guid secretId,
        int versionNumber,
        string plaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (versionNumber <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber),
                versionNumber,
                "Version numbers are 1-based.");

        _values[(secretId, versionNumber)] = plaintext;
        return Task.CompletedTask;
    }

    public Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default)
    {
        if (!_values.TryGetValue((secretId, versionNumber), out var value))
        {
            throw new KeyNotFoundException(
                $"No version row for secretId={secretId}, " +
                $"versionNumber={versionNumber}.");
        }

        return Task.FromResult(value);
    }

    public Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default)
    {
        // Scrub but keep the row so an audit query still sees that
        // the version existed. Mirrors the Story 29-2 contract: the
        // version row never disappears; only its ciphertext is zeroed.
        _values.AddOrUpdate(
            (secretId, versionNumber),
            addValue: null,
            updateValueFactory: (_, _) => null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test-only: snapshot every stored entry. Lets a fixture assert
    /// "the rotation handler did push a new version" without poking
    /// the private dictionary.
    /// </summary>
    public IReadOnlyDictionary<(Guid SecretId, int Version), string?> Snapshot() =>
        _values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Test-only: drop every stored entry.</summary>
    public void Clear() => _values.Clear();
}
