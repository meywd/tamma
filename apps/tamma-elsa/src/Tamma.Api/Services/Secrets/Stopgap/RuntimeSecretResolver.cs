using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// Default <see cref="IRuntimeSecretResolver"/>. Reads platform-scoped
/// cabinet rows by <see cref="StopgapSecretDescriptor.CabinetName"/>,
/// caches plaintext in-process for a short TTL, and (during the
/// Story 29-9 grace window) falls back to
/// <see cref="IConfiguration"/> / env vars when the cabinet is empty.
///
/// <para>Construct with <paramref name="allowEnvFallback"/> = true for
/// Story 29-9 (coexistence release); Story 29-10 flips it to false via
/// the <c>AddTammaRuntimeSecretResolver</c> wiring switch.</para>
///
/// <para><b>Caching</b>: plaintext is held under a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by cabinet
/// name. Cache TTL defaults to 60 seconds; the resolver's consumers
/// in Story 29-7 will invalidate entries via
/// <see cref="Invalidate"/> on <c>SECRET.ROTATE.ACTIVATED</c> events.</para>
/// </summary>
public sealed class RuntimeSecretResolver : IRuntimeSecretResolver
{
    /// <summary>Default cache TTL — 60 seconds per story plan §5.1.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RuntimeSecretResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly bool _allowEnvFallback;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly HashSet<string> _fallbackWarned = new(StringComparer.Ordinal);
    private readonly object _warnLock = new();

    public RuntimeSecretResolver(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        IConfiguration configuration,
        ILogger<RuntimeSecretResolver> logger,
        TimeProvider timeProvider,
        bool allowEnvFallback,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _secretsFactory = secretsFactory;
        _backend = backend;
        _configuration = configuration;
        _logger = logger;
        _timeProvider = timeProvider;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _allowEnvFallback = allowEnvFallback;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string cabinetName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cabinetName);
        if (string.IsNullOrWhiteSpace(cabinetName))
            throw new ArgumentException(
                "Cabinet name must be non-empty.", nameof(cabinetName));

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cabinetName, out var cached)
            && cached.ExpiresAt > now)
        {
            return cached.Plaintext;
        }

        // Cabinet first.
        var fromCabinet = await TryReadCabinetAsync(cabinetName, ct)
            .ConfigureAwait(false);
        if (fromCabinet is not null)
        {
            Store(cabinetName, fromCabinet, now);
            return fromCabinet;
        }

        // Story 29-10: no fallback. Fail-fast so misconfigured
        // deployments are visible at startup rather than serving 500s
        // on first request.
        if (!_allowEnvFallback)
        {
            throw new MissingSecretException(cabinetName);
        }

        // Fallback: config / env. Warn once per cabinet name so the
        // coexistence window is visible in startup logs.
        var descriptor = StopgapSecretMap.Platform
            .FirstOrDefault(d => d.CabinetName == cabinetName);
        if (descriptor is null)
        {
            return null;
        }

        var fromConfig = descriptor.ResolveFromConfig(_configuration);
        if (fromConfig is not null)
        {
            WarnOnce(cabinetName, descriptor.PreviousLocation);
            Store(cabinetName, fromConfig, now);
            return fromConfig;
        }

        return null;
    }

    /// <summary>
    /// Invalidate the cached entry for <paramref name="cabinetName"/>.
    /// Called by Story 29-7's rotation dispatcher on
    /// <c>SECRET.ROTATE.ACTIVATED</c> so stale values are evicted
    /// promptly.
    /// </summary>
    public void Invalidate(string cabinetName) =>
        _cache.TryRemove(cabinetName, out _);

    private async Task<string?> TryReadCabinetAsync(
        string cabinetName, CancellationToken ct)
    {
        try
        {
            await using var ctx = await _secretsFactory
                .CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Secrets
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.Name == cabinetName
                         && s.Scope == "platform",
                    ct)
                .ConfigureAwait(false);
            if (row is null || row.ActiveVersionNumber <= 0)
            {
                return null;
            }
            var plaintext = await _backend
                .GetVersionPlaintextAsync(row.Id, row.ActiveVersionNumber, ct)
                .ConfigureAwait(false);
            return plaintext;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cabinet probe for {CabinetName} threw; " +
                "falling back to config path.", cabinetName);
            return null;
        }
    }

    private void Store(string cabinetName, string plaintext, DateTimeOffset now) =>
        _cache[cabinetName] = new CacheEntry(plaintext, now.Add(_cacheTtl));

    private void WarnOnce(string cabinetName, string previousLocation)
    {
        lock (_warnLock)
        {
            if (_fallbackWarned.Add(cabinetName))
            {
                _logger.LogWarning(
                    "Using env-var/config fallback for cabinet secret " +
                    "'{CabinetName}' (previously '{PreviousLocation}'); " +
                    "deprecated — run `migrate-secrets` (Story 29-9) and " +
                    "see Story 29-10 for removal.",
                    cabinetName, previousLocation);
            }
        }
    }

    private readonly record struct CacheEntry(string Plaintext, DateTimeOffset ExpiresAt);
}
