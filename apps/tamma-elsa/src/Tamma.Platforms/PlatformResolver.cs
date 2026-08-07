using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms;

/// <summary>
/// Story 31-2 implementation of <see cref="IPlatformResolver"/>.
/// Composes:
///
/// <list type="number">
///   <item><see cref="ITenantPlatformInstallationRepository"/> —
///         row lookup keyed by <c>(tenantId, kind)</c> /
///         <c>(kind, externalId)</c> / <c>id</c>.</item>
///   <item><see cref="IPlatformCredentialReader"/> — Story 29 secret
///         store seam; reads the active-version plaintext for the
///         row's <c>credential_secret_*</c> tuple.</item>
///   <item><see cref="IGitPlatformDriverFactory"/> — per-kind factory
///         (registered as keyed singleton); builds the driver bound
///         to the row's base URL + decrypted credential.</item>
/// </list>
///
/// <para>The resolver caches the composed driver per
/// <c>(tenantId, kind)</c> via <see cref="PlatformDriverCache"/>.
/// Cache invalidation is event-driven (see
/// <see cref="PlatformDriverCache.InvalidateTenantAsync"/>); 31-2's
/// hosted service tails the platform-event log for
/// <c>PLATFORM.INSTALLATION.*</c> events.</para>
///
/// <para>Cross-tenant safety: every database read goes through the
/// repository's tenant-scoped methods. The webhook path
/// (<see cref="ResolveForWebhookAsync"/>) takes a kind + external id
/// without a caller-supplied tenant id and resolves the tenant
/// through the repository — there is no spoof surface because the
/// caller never injects a tenant id.</para>
/// </summary>
public sealed class PlatformResolver : IPlatformResolver
{
    private readonly ITenantPlatformInstallationRepository _repo;
    private readonly IPlatformCredentialReader _credentials;
    private readonly IServiceProvider _services;
    private readonly PlatformDriverCache _cache;
    private readonly ILogger<PlatformResolver> _logger;
    private readonly SingleUserPlatformOptions? _configPlatform;

    public PlatformResolver(
        ITenantPlatformInstallationRepository repo,
        IPlatformCredentialReader credentials,
        IServiceProvider services,
        PlatformDriverCache cache,
        ILogger<PlatformResolver> logger,
        SingleUserPlatformOptions? configPlatform = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _repo = repo;
        _credentials = credentials;
        _services = services;
        _cache = cache;
        _logger = logger;
        _configPlatform = configPlatform;
    }

    /// <inheritdoc />
    public async Task<IGitPlatformDriver?> ResolveForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var row = await _repo.GetByTenantPrimaryAsync(tenantId, ct).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug(
                "No primary platform installation for tenant {TenantId}", tenantId);
            return null;
        }
        return await ResolveAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IGitPlatformDriver?> ResolveForTenantAsync(
        Guid tenantId,
        PlatformKind kind,
        CancellationToken ct = default)
    {
        // Cache check first — same tenant + kind in the TTL window
        // returns the cached driver without a DB hit.
        if (_cache.TryGet(tenantId, kind, out var cached) && cached is not null)
        {
            return cached;
        }

        var row = await _repo
            .GetByTenantKindAsync(tenantId, ToWireKind(kind), ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug(
                "No {Kind} installation for tenant {TenantId}", kind, tenantId);
            return null;
        }

        return await ComposeAndCacheAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MediationDriverResolution?> ResolveForMediationAsync(
        Guid? tenantId, CancellationToken ct = default)
    {
        // ── Tier 1 (BYOK / tenant-owned): the tenant's primary
        //    installation row of ANY kind (SaaS scoping answer; also
        //    covers a single-user deployment that connected via the
        //    onboarding picker). Row-with-unreadable-credential falls
        //    through to the config tier, mirroring the pre-P2
        //    GitTokenResolver's BYOK→platform fallback. ──
        if (tenantId is { } tid && tid != Guid.Empty)
        {
            var row = await _repo.GetByTenantPrimaryAsync(tid, ct).ConfigureAwait(false);
            if (row is not null)
            {
                var driver = await ResolveAsync(row, ct).ConfigureAwait(false);
                if (driver is not null)
                {
                    return new MediationDriverResolution(
                        driver, MediationCredentialSource.TenantInstallation);
                }
                _logger.LogWarning(
                    "Tenant {TenantId} has installation row {RowId} (kind={Kind}) but the driver "
                    + "could not be composed — falling back to the Platform: config tier",
                    tid, row.Id, row.PlatformKind);
            }
        }

        // ── Tier 2 (config-backed source — single-user scoping answer,
        //    and the SaaS deployment-level system tier): synthesize an
        //    in-memory installation from the Platform: section. Never
        //    persisted (no config↔DB drift; idempotent). ──
        var configDriver = await ResolveFromConfigAsync(tenantId, ct).ConfigureAwait(false);
        if (configDriver is not null)
        {
            return new MediationDriverResolution(
                configDriver, MediationCredentialSource.PlatformDefault);
        }

        // ── Tier 3: nothing — the mediation fails closed. ──
        return null;
    }

    /// <summary>
    /// Compose (and cache) a driver from the <c>Platform:</c> config
    /// section. Cached per (tenantId-or-empty, kind) through the same
    /// <see cref="PlatformDriverCache"/> as DB-backed drivers, so the
    /// event-driven invalidation and TTL self-heal apply uniformly.
    /// </summary>
    private async Task<IGitPlatformDriver?> ResolveFromConfigAsync(
        Guid? tenantId, CancellationToken ct)
    {
        var options = _configPlatform;
        if (options is null || !options.IsConfigured)
        {
            return null;
        }
        if (!TryParseKind(options.Kind!.Trim().ToLowerInvariant(), out var kind))
        {
            _logger.LogWarning(
                "Platform: config section names unknown kind '{Kind}' — ignoring the config tier",
                options.Kind);
            return null;
        }

        var cacheTenant = tenantId ?? Guid.Empty;
        if (_cache.TryGet(cacheTenant, kind, out var cached) && cached is not null)
        {
            return cached;
        }

        // Credential: inline env/config plaintext wins; else the
        // secret cabinet (scope defaults to "platform" — a deployment-
        // level secret with no tenant owner).
        var plaintext = options.Credential;
        if (string.IsNullOrWhiteSpace(plaintext)
            && !string.IsNullOrWhiteSpace(options.CredentialSecretName))
        {
            var scope = string.IsNullOrWhiteSpace(options.CredentialSecretScope)
                ? "platform"
                : options.CredentialSecretScope;
            plaintext = await _credentials
                .ReadActivePlaintextAsync(
                    scope,
                    scope == "tenant" ? tenantId : null,
                    options.CredentialSecretName!,
                    ct)
                .ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            _logger.LogWarning(
                "Platform: config section is active (kind={Kind}) but no credential could be "
                + "resolved (neither Platform:Credential nor Platform:CredentialSecretName "
                + "produced plaintext)", kind);
            return null;
        }

        var factory = _services.GetKeyedService<IGitPlatformDriverFactory>(kind)
            ?? throw new InvalidOperationException(
                $"Platform: config names kind {kind} but no IGitPlatformDriverFactory is "
                + "registered for it.");

        // The synthesized, never-persisted installation. Id is derived
        // deterministically from the kind so repeated resolutions are
        // stable for diagnostics; TenantId carries the caller's tenant
        // (or Guid.Empty in single-user mode).
        var installation = new PlatformInstallation(
            Id: Guid.Empty,
            TenantId: cacheTenant,
            Kind: kind,
            BaseUrl: options.BaseUrl ?? string.Empty,
            InstallationExternalId: options.InstallationExternalId);

        var driver = await factory.CreateAsync(installation, plaintext!, ct).ConfigureAwait(false);
        _cache.Set(cacheTenant, kind, driver);
        return driver;
    }

    /// <inheritdoc />
    public async Task<IGitPlatformDriver?> ResolveForWebhookAsync(
        PlatformKind kind,
        string installationExternalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationExternalId);

        var row = await _repo
            .GetByExternalIdAsync(ToWireKind(kind), installationExternalId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug(
                "No installation row for kind={Kind} externalId={ExternalId}",
                kind, installationExternalId);
            return null;
        }

        return await ResolveAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IGitPlatformDriver?> ResolveByInstallationIdAsync(
        Guid installationRowId, CancellationToken ct = default)
    {
        var row = await _repo.GetByIdAsync(installationRowId, ct).ConfigureAwait(false);
        if (row is null) return null;
        return await ResolveAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlatformInstallation>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var rows = await _repo.ListByTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return Array.Empty<PlatformInstallation>();
        }

        var result = new List<PlatformInstallation>(rows.Count);
        foreach (var row in rows)
        {
            if (TryParseKind(row.PlatformKind, out var kind))
            {
                result.Add(ToInstallationRecord(row, kind));
            }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────

    private async Task<IGitPlatformDriver?> ResolveAsync(
        TenantPlatformInstallation row, CancellationToken ct)
    {
        if (!TryParseKind(row.PlatformKind, out var kind))
        {
            _logger.LogWarning(
                "Skipping installation row id={RowId} with unknown PlatformKind={Kind}",
                row.Id, row.PlatformKind);
            return null;
        }

        if (_cache.TryGet(row.TenantId, kind, out var cached) && cached is not null)
        {
            return cached;
        }

        return await ComposeAndCacheAsync(row, ct).ConfigureAwait(false);
    }

    private async Task<IGitPlatformDriver?> ComposeAndCacheAsync(
        TenantPlatformInstallation row, CancellationToken ct)
    {
        if (!TryParseKind(row.PlatformKind, out var kind))
        {
            return null;
        }

        // Fetch plaintext via Epic 29 seam — every secret read goes
        // through ISecretStore + ISecretStoreBackend (wrapped here as
        // IPlatformCredentialReader). Plaintext lives only on the
        // stack between this call and the factory invocation below.
        var plaintext = await _credentials
            .ReadActivePlaintextAsync(
                row.CredentialSecretScope,
                row.CredentialSecretScope == "tenant" ? row.TenantId : (Guid?)null,
                row.CredentialSecretName,
                ct)
            .ConfigureAwait(false);

        if (plaintext is null)
        {
            _logger.LogWarning(
                "Installation row id={RowId} (tenant={TenantId} kind={Kind}) " +
                "references a credential secret ({Scope}/{Name}) that has no " +
                "active plaintext — driver resolution failed",
                row.Id, row.TenantId, kind,
                row.CredentialSecretScope, row.CredentialSecretName);
            return null;
        }

        // Look up the per-kind factory via keyed DI. A missing
        // registration means a driver was scheduled but the host
        // didn't register its factory — surface that as an
        // InvalidOperationException so the misconfiguration is loud.
        var factory = _services.GetKeyedService<IGitPlatformDriverFactory>(kind)
            ?? throw new InvalidOperationException(
                $"No IGitPlatformDriverFactory registered for PlatformKind={kind}. " +
                $"Either register the kind's driver project or fall back to " +
                $"AddNullGitPlatformDriver(PlatformKind.{kind}).");

        if (factory.Kind != kind)
        {
            throw new InvalidOperationException(
                $"IGitPlatformDriverFactory registered under key {kind} " +
                $"reports Kind={factory.Kind}; refusing to mint a driver " +
                $"under a mismatched key.");
        }

        var installation = ToInstallationRecord(row, kind);
        var driver = await factory
            .CreateAsync(installation, plaintext, ct)
            .ConfigureAwait(false);

        _cache.Set(row.TenantId, kind, driver);
        return driver;
    }

    /// <summary>
    /// Convert a <see cref="PlatformKind"/> enum to the lower-snake
    /// string the database stores. Centralised so a future addition
    /// (e.g. a new enum value) can't drift between read + write.
    /// </summary>
    public static string ToWireKind(PlatformKind kind) => kind switch
    {
        PlatformKind.GitHub => "github",
        PlatformKind.Gitea => "gitea",
        PlatformKind.Forgejo => "forgejo",
        PlatformKind.GitLab => "gitlab",
        PlatformKind.Bitbucket => "bitbucket",
        PlatformKind.AzureDevOps => "azure_devops",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unknown PlatformKind"),
    };

    /// <summary>
    /// Reverse mapping. Returns false on an unknown wire value
    /// (operational defence — a row written by a future migration
    /// shouldn't crash a current resolver).
    /// </summary>
    public static bool TryParseKind(string wire, out PlatformKind kind)
    {
        switch (wire)
        {
            case "github": kind = PlatformKind.GitHub; return true;
            case "gitea": kind = PlatformKind.Gitea; return true;
            case "forgejo": kind = PlatformKind.Forgejo; return true;
            case "gitlab": kind = PlatformKind.GitLab; return true;
            case "bitbucket": kind = PlatformKind.Bitbucket; return true;
            case "azure_devops": kind = PlatformKind.AzureDevOps; return true;
            default: kind = default; return false;
        }
    }

    private static PlatformInstallation ToInstallationRecord(
        TenantPlatformInstallation row, PlatformKind kind) =>
        new(
            Id: row.Id,
            TenantId: row.TenantId,
            Kind: kind,
            BaseUrl: row.BaseUrl,
            InstallationExternalId: row.InstallationExternalId);
}
