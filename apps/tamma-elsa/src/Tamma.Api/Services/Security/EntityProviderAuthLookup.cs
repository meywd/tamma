using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 / 34-11 — the entity-backed <see cref="IProviderAuthLookup"/>.
/// Reads <c>Provider.AuthModel</c> (<c>api-key</c> | <c>cli-token</c>) from the
/// control-plane <c>providers</c> table (the canonical source once 34-11 has
/// landed) for the requested provider key. Returns <c>null</c> for an unknown
/// key — which drives the SaaS fail-closed DENY (never a permissive allow, per
/// <c>feedback_resolution_no_empty_fallback</c>).
///
/// <para>This is the production default for <see cref="IProviderAuthLookup"/>:
/// swapping it for / from <see cref="StaticProviderAuthLookup"/> is a single DI
/// registration line, and the <see cref="ISaaSProviderGate"/> contract is
/// identical for both (the 34-11 swap test pins contract-neutrality).</para>
///
/// <para>Reads through a short-lived scope (mirrors
/// <c>DbProviderPricingService</c>) so it is safe behind any service lifetime.
/// Matching is case-insensitive and trimmed.</para>
/// </summary>
public sealed class EntityProviderAuthLookup : IProviderAuthLookup
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EntityProviderAuthLookup> _logger;

    public EntityProviderAuthLookup(
        IServiceScopeFactory scopeFactory,
        ILogger<EntityProviderAuthLookup> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProviderAuthModel?> AuthModelAsync(
        string? providerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return null;

        var name = providerName.Trim();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // Read only the AuthModel string for the provider key (case-insensitive).
        // No tenant scope — provider cost/auth identity is platform-global (34-11).
        var authModel = await db.Providers
            .AsNoTracking()
            .Where(p => EF.Functions.ILike(p.Key, name))
            .Select(p => p.AuthModel)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(authModel))
            return null; // unknown provider ⇒ fail-closed deny in SaaS

        return authModel.Trim().ToLowerInvariant() switch
        {
            "api-key" => ProviderAuthModel.ApiKey,
            "cli-token" => ProviderAuthModel.CliToken,
            _ => UnknownAuthModel(name, authModel),
        };
    }

    private ProviderAuthModel? UnknownAuthModel(string providerName, string authModel)
    {
        // An unrecognised AuthModel string is a data defect — fail-closed (deny)
        // rather than guess. The 34-11 CHECK pins the enum, so this should never
        // fire in practice; we never silently allow.
        _logger.LogWarning(
            "Provider '{Provider}' has an unrecognised AuthModel '{AuthModel}'; "
            + "treating as unknown (fail-closed deny in SaaS).",
            providerName, authModel);
        return null;
    }
}
