using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Convention resolution service (Story 27-9). Implements the locked
/// fail-loud chain — <c>tenant override → system default → TammaError</c> —
/// against the seeded <c>conventions</c> table, mirroring
/// <see cref="Tamma.Api.Services.PromptStore.PromptStoreService"/>'s structure
/// (a thin service over a tenant-scoped repository).
///
/// <para><b>Resolution (AC4).</b> A single exact-key lookup per tier on
/// <c>(tenant_id, role, action)</c> — no tokenisation, no keyword query, no
/// merge/concat, no <c>match_mode</c> / <c>always_apply</c> (AC5/AC6).</para>
///
/// <para><b>enabled=false fallthrough (AC9).</b> The tenant-override step
/// requires <see cref="Convention.Enabled"/> == true; a disabled override is
/// treated as ABSENT so resolution drops through to the system default rather
/// than blanking the convention.</para>
/// </summary>
public sealed class ConventionStore : IConventionStore
{
    private readonly IConventionRepository _repository;

    public ConventionStore(IConventionRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<Convention?> GetAsync(
        Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct)
    {
        var roleWire = role.ToWire();
        var actionWire = action.ToWire();

        // Prefer an enabled tenant override; otherwise the system default.
        // MAY return null (this is the raw fetch — distinct from ResolveAsync).
        if (tenantId is { } tid && tid != Guid.Empty)
        {
            var tenantOverride = await _repository
                .GetTenantOverrideAsync(tid, roleWire, actionWire, ct)
                .ConfigureAwait(false);
            if (tenantOverride is { Enabled: true })
            {
                return tenantOverride;
            }
        }

        return await _repository
            .GetSystemDefaultAsync(roleWire, actionWire, ct)
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(
        Guid tenantId, AgentRole role, AgentAction action, string body, bool enabled, Guid userId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        await _repository
            .UpsertTenantOverrideAsync(tenantId, role.ToWire(), action.ToWire(), body, enabled, userId, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        Guid tenantId, AgentRole role, AgentAction action, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        await _repository
            .DeleteTenantOverrideAsync(tenantId, role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);
    }

    public async Task<ConventionResolution> ResolveAsync(
        Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct)
    {
        var roleWire = role.ToWire();
        var actionWire = action.ToWire();

        // 4a. tenant-override row, enabled=true → use it (Source=Tenant).
        if (tenantId is { } tid && tid != Guid.Empty)
        {
            var tenantOverride = await _repository
                .GetTenantOverrideAsync(tid, roleWire, actionWire, ct)
                .ConfigureAwait(false);
            // enabled=false override falls through to the system default (AC9):
            // a tenant DISABLING its override reverts to system, never blanks.
            if (tenantOverride is { Enabled: true })
            {
                return new ConventionResolution(
                    roleWire, actionWire, tenantOverride.Body, ConventionSource.Tenant);
            }
        }

        // 4b. system-default row, enabled=true → use it (Source=System).
        var systemDefault = await _repository
            .GetSystemDefaultAsync(roleWire, actionWire, ct)
            .ConfigureAwait(false);
        if (systemDefault is { Enabled: true })
        {
            return new ConventionResolution(
                roleWire, actionWire, systemDefault.Body, ConventionSource.System);
        }

        // 4c. else fail loud. A taxonomy-valid pair MUST have a seeded system
        // default (Story 27-16) — absence is a bug, never a silent empty
        // (locked user mandate; SPEC §7).
        throw NoConventionError(roleWire, actionWire, tenantId);
    }

    public async Task<IReadOnlyList<ConventionSummary>> ListAsync(
        Guid? tenantId, CancellationToken ct)
    {
        // Build the resolved view across every taxonomy cell (AC3). Load each
        // tier ONCE and overlay overrides on defaults in memory — avoids one
        // query per cell.
        var systemDefaults = await _repository
            .ListSystemDefaultsAsync(ct)
            .ConfigureAwait(false);
        var systemByCell = systemDefaults
            .ToDictionary(c => (c.Role, c.Action));

        IReadOnlyDictionary<(string Role, string Action), Convention> overridesByCell;
        if (tenantId is { } tid && tid != Guid.Empty)
        {
            var overrides = await _repository
                .ListTenantOverridesAsync(tid, ct)
                .ConfigureAwait(false);
            overridesByCell = overrides
                .Where(c => c.Enabled)
                .ToDictionary(c => (c.Role, c.Action));
        }
        else
        {
            overridesByCell =
                new Dictionary<(string Role, string Action), Convention>();
        }

        var result = new List<ConventionSummary>(
            RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count));

        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            foreach (var act in actions)
            {
                var actionWire = act.ToWire();
                var cell = (roleWire, actionWire);

                if (overridesByCell.TryGetValue(cell, out var tenantRow))
                {
                    result.Add(new ConventionSummary(
                        roleWire, actionWire, tenantRow.Body, ConventionSource.Tenant,
                        tenantRow.Id, tenantRow.Enabled, tenantRow.Version, tenantRow.UpdatedAt));
                }
                else if (systemByCell.TryGetValue(cell, out var systemRow)
                    && systemRow.Enabled)
                {
                    result.Add(new ConventionSummary(
                        roleWire, actionWire, systemRow.Body, ConventionSource.System,
                        systemRow.Id, systemRow.Enabled, systemRow.Version, systemRow.UpdatedAt));
                }
                // A cell with neither an enabled override nor an enabled system
                // default is a seed bug (Story 27-16); ListAsync omits it rather
                // than throwing — ResolveAsync is the fail-loud surface.
            }
        }

        return result
            .OrderBy(s => s.Role, StringComparer.Ordinal)
            .ThenBy(s => s.Action, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------
    // System-default admin CRUD + reset (Story 27-10 enablement). Operate ONLY
    // on system-default rows (tenant_id IS NULL); the repository's
    // tenant_id IS NULL discriminator keeps these off tenant overrides — the
    // mutation-safe mirror-image of UpsertAsync/DeleteAsync above.
    // ------------------------------------------------------------------

    public async Task UpsertSystemDefaultAsync(
        AgentRole role, AgentAction action, string body, bool enabled, Guid adminUserId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        await _repository
            .UpsertSystemDefaultAsync(role.ToWire(), action.ToWire(), body, enabled, adminUserId, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteSystemDefaultAsync(
        AgentRole role, AgentAction action, CancellationToken ct)
    {
        await _repository
            .DeleteSystemDefaultAsync(role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);
    }

    public async Task ResetSystemDefaultAsync(
        AgentRole role, AgentAction action, Guid adminUserId, CancellationToken ct)
    {
        // Source the canonical baseline from the SAME code spec the seeder uses
        // (ConventionSeedSpecs) — a reset restores the system default to exactly
        // what a fresh seed would have written. DefaultBodyFor validates the
        // pair is a taxonomy cell and throws ArgumentException if not; rethrow
        // as a structured TammaError so the API boundary (Story 27-10) returns a
        // clean error rather than a 500.
        string baseline;
        try
        {
            baseline = ConventionSeedSpecs.DefaultBodyFor(role, action);
        }
        catch (ArgumentException)
        {
            throw new TammaError(
                "CONVENTION_NOT_A_TAXONOMY_CELL",
                $"Cannot reset (role='{role.ToWire()}', action='{action.ToWire()}'): "
                + "it is not a valid taxonomy cell, so it has no code-baseline "
                + "system default to reset to.",
                new Dictionary<string, object?>
                {
                    ["role"] = role.ToWire(),
                    ["action"] = action.ToWire(),
                },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        // A reset is a CANONICAL restore — force enabled:true so a previously
        // disabled system default comes back live (Story 27-10).
        await _repository
            .UpsertSystemDefaultAsync(role.ToWire(), action.ToWire(), baseline, enabled: true, adminUserId, ct)
            .ConfigureAwait(false);
    }

    private static TammaError NoConventionError(string role, string action, Guid? tenantId)
        => new(
            "CONVENTION_NOT_FOUND",
            $"No convention available for (role='{role}', action='{action}'): no enabled "
            + "tenant override and no enabled system default. Resolution is "
            + "tenant override → system default → error; a taxonomy-valid pair "
            + "always ships a seeded system default (Story 27-16 / SPEC §7), so "
            + "this is a seed bug, never a silent empty fallback.",
            new Dictionary<string, object?>
            {
                ["role"] = role,
                ["action"] = action,
                ["tenantId"] = tenantId,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
}
