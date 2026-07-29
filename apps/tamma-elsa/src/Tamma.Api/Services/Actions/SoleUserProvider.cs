using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Data;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-5 (AC7, D9) — answers "who is the sole user?" on single-user
/// planes that carry no <c>ClaimsPrincipal</c> at all (the engine's service
/// principal has no user id; background actors have no request). Order:
/// <c>Tamma:SingleUser:OwnerUserId</c> config when set, else the
/// earliest-created row in <c>users</c>; FAIL-LOUD
/// (<c>GOVERNANCE.PRINCIPAL.NO_SOLE_USER</c>) when <c>users</c> is empty —
/// guessing here would silently apply the wrong principal's policy.
///
/// <para>Only success is cached (a sole-user id never changes once minted), so
/// "invalidation on user create" needs no hook: an empty-<c>users</c> failure
/// is never cached, and the first call after the first signup resolves.</para>
/// </summary>
public interface ISoleUserProvider
{
    /// <summary>The sole user's id.</summary>
    /// <exception cref="TammaError">Code <c>GOVERNANCE.PRINCIPAL.NO_SOLE_USER</c>.</exception>
    Task<Guid> GetSoleUserIdAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SoleUserProvider : ISoleUserProvider
{
    /// <summary>Config key naming the owner explicitly.</summary>
    public const string OwnerUserIdConfigKey = "Tamma:SingleUser:OwnerUserId";

    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<ControlPlaneDbContext>? _factory;

    private Guid? _cached; // success-only cache

    public SoleUserProvider(
        IConfiguration configuration,
        IDbContextFactory<ControlPlaneDbContext>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<Guid> GetSoleUserIdAsync(CancellationToken ct = default)
    {
        if (_cached is Guid cached)
        {
            return cached;
        }

        var configured = _configuration[OwnerUserIdConfigKey];
        if (!string.IsNullOrWhiteSpace(configured) && Guid.TryParse(configured, out var fromConfig))
        {
            _cached = fromConfig;
            return fromConfig;
        }

        if (_factory is not null)
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var earliest = await db.Users.AsNoTracking()
                .OrderBy(u => u.CreatedAt)
                .ThenBy(u => u.Id)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (earliest is Guid uid)
            {
                _cached = uid;
                return uid;
            }
        }

        throw new TammaError(
            "GOVERNANCE.PRINCIPAL.NO_SOLE_USER",
            "Single-user principal resolution failed: no Tamma:SingleUser:OwnerUserId is "
            + "configured and the users table is empty (or no control-plane database is "
            + "wired). Guessing a principal would silently apply the wrong policy.",
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}
