using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Actions;
using Tamma.Data;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-5 (AC7, D9) — MANDATORY principal resolution with one documented
/// rule per plane. The principal is NEVER taken from caller-supplied payload
/// (<c>EnginePlane_NeverReadsPrincipalFromTheWireBody</c>): tenant identity
/// comes only from the ambient <see cref="ITenantContext"/> (populated by
/// middleware from the authenticated request), user identity only from the
/// authenticated <see cref="ClaimsPrincipal"/> or the
/// <see cref="ISoleUserProvider"/>.
///
/// <list type="bullet">
/// <item><b>SaaS</b>: tenant id from <see cref="ITenantContext"/>. Absent →
/// resolve against the PLATFORM scope only and emit
/// <c>ACTION.GATE.PRINCIPAL_UNRESOLVED</c>; NEVER falls through to a user row
/// (in SaaS a user row is not a legal principal at all).</item>
/// <item><b>single-user, human plane</b>: user id from the authenticated
/// <see cref="ClaimsPrincipal"/>.</item>
/// <item><b>single-user, engine / service / background plane</b> (no claims):
/// the <see cref="ISoleUserProvider"/> — fail-loud when no sole user exists.</item>
/// </list>
/// </summary>
public interface IGovernancePrincipalResolver
{
    /// <summary>Resolve the governance principal for the current plane.
    /// <paramref name="caller"/> is the authenticated request principal when
    /// one exists; null on engine/background planes.</summary>
    Task<GovernancePrincipal> ResolveAsync(
        ClaimsPrincipal? caller = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class GovernancePrincipalResolver : IGovernancePrincipalResolver
{
    private readonly ITammaModeProvider _mode;
    private readonly ITenantContext _tenantContext;
    private readonly ISoleUserProvider _soleUser;
    private readonly ActionGateEventsService? _events;
    private readonly ILogger<GovernancePrincipalResolver>? _logger;

    public GovernancePrincipalResolver(
        ITammaModeProvider mode,
        ITenantContext tenantContext,
        ISoleUserProvider soleUser,
        ActionGateEventsService? events = null,
        ILogger<GovernancePrincipalResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(soleUser);
        _mode = mode;
        _tenantContext = tenantContext;
        _soleUser = soleUser;
        _events = events;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GovernancePrincipal> ResolveAsync(
        ClaimsPrincipal? caller = null, CancellationToken ct = default)
    {
        if (_mode.Mode == TammaMode.SaaS)
        {
            if (_tenantContext.TenantId is Guid tenantId)
            {
                return GovernancePrincipal.ForTenant(tenantId);
            }

            // Platform scope ONLY — never a user row in SaaS (AC7/D9).
            _logger?.LogWarning(
                "Governance principal unresolved: SaaS request with no ambient tenant; "
                + "resolving against the platform scope only.");
            if (_events is not null)
            {
                await _events.EmitPrincipalUnresolvedAsync(
                    "SaaS request with no ambient tenant context").ConfigureAwait(false);
            }
            return GovernancePrincipal.Platform;
        }

        // Single-user, human plane: the authenticated ClaimsPrincipal.
        if (caller?.GetUserId() is Guid userId)
        {
            return GovernancePrincipal.ForUser(userId);
        }

        // Single-user, engine / service / background plane: the sole user —
        // fail-loud (GOVERNANCE.PRINCIPAL.NO_SOLE_USER) rather than guessing.
        return GovernancePrincipal.ForUser(
            await _soleUser.GetSoleUserIdAsync(ct).ConfigureAwait(false));
    }
}
