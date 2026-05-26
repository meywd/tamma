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
///
/// <para><b>Event emission (Story 27-14 Delta A).</b> DCB audit events are
/// emitted from within this service, not from the endpoint handlers, so any
/// future caller of <see cref="IConventionStore"/> (CLI, Elsa activities,
/// internal scripts) gets an automatic audit trail.</para>
/// </summary>
public sealed class ConventionStore : IConventionStore
{
    private readonly IConventionRepository _repository;
    private readonly ConventionEventsService _events;

    public ConventionStore(IConventionRepository repository, ConventionEventsService events)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(events);
        _repository = repository;
        _events = events;
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

    public async Task<(Convention Row, bool WasCreated)> UpsertAsync(
        Guid tenantId, AgentRole role, AgentAction action, string body, bool enabled, Guid userId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var (row, wasCreated, previous) = await _repository
            .UpsertTenantOverrideAsync(tenantId, role.ToWire(), action.ToWire(), body, enabled, userId, ct)
            .ConfigureAwait(false);

        // Story 27-14 Delta A: emit DCB event from the service layer.
        await _events.EmitUpsertedAsync(tenantId, role, action, userId, wasCreated, previous, row, ct)
            .ConfigureAwait(false);

        return (row, wasCreated);
    }

    public async Task<bool> DeleteAsync(
        Guid tenantId, AgentRole role, AgentAction action, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var (wasDeleted, deletedVersion) = await _repository
            .DeleteTenantOverrideAsync(tenantId, role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);

        // Story 27-14 Delta A: emit DCB event from the service layer. The
        // userId is not available at the store-mutation level (it was the
        // endpoint's actor); pass Guid.Empty as a sentinel — the endpoint
        // now delegates fully to the store. In practice callers that need a
        // real actor id should use the overload that accepts userId.
        // NOTE: DeleteAsync is called by the endpoint which also has the
        // principal — see DeleteWithActorAsync. The base DeleteAsync falls
        // back to Guid.Empty for backwards compatibility with test callers.
        await _events.EmitDeletedAsync(tenantId, role, action, Guid.Empty, wasDeleted, deletedVersion, ct)
            .ConfigureAwait(false);

        return wasDeleted;
    }

    /// <summary>
    /// Delete a tenant override, emitting the DCB event with the correct actor.
    /// This is the preferred overload; <see cref="DeleteAsync"/> exists for
    /// backwards-compatibility with existing callers that don't have an actor.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid tenantId, AgentRole role, AgentAction action, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var (wasDeleted, deletedVersion) = await _repository
            .DeleteTenantOverrideAsync(tenantId, role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);

        await _events.EmitDeletedAsync(tenantId, role, action, actorUserId, wasDeleted, deletedVersion, ct)
            .ConfigureAwait(false);

        return wasDeleted;
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
                    roleWire, actionWire, tenantOverride.Body, ConventionSource.Tenant,
                    tenantOverride.Id, tenantOverride.Version, tenantOverride.UpdatedAt);
            }
        }

        // 4b. system-default row, enabled=true → use it (Source=System).
        var systemDefault = await _repository
            .GetSystemDefaultAsync(roleWire, actionWire, ct)
            .ConfigureAwait(false);
        if (systemDefault is { Enabled: true })
        {
            return new ConventionResolution(
                roleWire, actionWire, systemDefault.Body, ConventionSource.System,
                systemDefault.Id, systemDefault.Version, systemDefault.UpdatedAt);
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

    public async Task<(Convention Row, bool WasCreated)> UpsertSystemDefaultAsync(
        AgentRole role, AgentAction action, string body, bool enabled, Guid adminUserId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var (row, wasCreated, previous) = await _repository
            .UpsertSystemDefaultAsync(role.ToWire(), action.ToWire(), body, enabled, adminUserId, ct)
            .ConfigureAwait(false);

        // Story 27-14 Delta A: emit DCB event from the service layer.
        await _events.EmitUpsertedAsync(null, role, action, adminUserId, wasCreated, previous, row, ct)
            .ConfigureAwait(false);

        return (row, wasCreated);
    }

    public async Task<bool> DeleteSystemDefaultAsync(
        AgentRole role, AgentAction action, CancellationToken ct)
    {
        var (wasDeleted, deletedVersion) = await _repository
            .DeleteSystemDefaultAsync(role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);

        // Story 27-14 Delta A: emit DCB event — actor is Guid.Empty because
        // DeleteSystemDefaultAsync has no actor parameter (it's only called
        // from the admin endpoint which has already authenticated the request).
        // See also: DeleteSystemDefaultAsync(role, action, adminUserId, ct).
        await _events.EmitDeletedAsync(null, role, action, Guid.Empty, wasDeleted, deletedVersion, ct)
            .ConfigureAwait(false);

        return wasDeleted;
    }

    /// <summary>
    /// Delete the SYSTEM-DEFAULT convention, emitting the DCB event with the
    /// correct admin actor. This is the preferred overload.
    /// </summary>
    public async Task<bool> DeleteSystemDefaultAsync(
        AgentRole role, AgentAction action, Guid adminUserId, CancellationToken ct)
    {
        var (wasDeleted, deletedVersion) = await _repository
            .DeleteSystemDefaultAsync(role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);

        await _events.EmitDeletedAsync(null, role, action, adminUserId, wasDeleted, deletedVersion, ct)
            .ConfigureAwait(false);

        return wasDeleted;
    }

    public async Task<(Convention Row, bool WasCreated)> ResetSystemDefaultAsync(
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
                    ["role"]   = role.ToWire(),
                    ["action"] = action.ToWire(),
                },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        // Read the current system default BEFORE upserting so we can compute the
        // previousVersion for the RESET event.
        var currentDefault = await _repository
            .GetSystemDefaultAsync(role.ToWire(), action.ToWire(), ct)
            .ConfigureAwait(false);
        var previousVersion = currentDefault?.Version ?? 0;

        // A reset is a CANONICAL restore — force enabled:true so a previously
        // disabled system default comes back live (Story 27-10).
        // Forward the (row, wasCreated) tuple so the caller can build a complete
        // HTTP response without a second DB round-trip.
        var (row, wasCreated, _) = await _repository
            .UpsertSystemDefaultAsync(role.ToWire(), action.ToWire(), baseline, enabled: true, adminUserId, ct)
            .ConfigureAwait(false);

        // Story 27-14 Delta A + C: emit RESET event with version diff.
        await _events.EmitResetAsync(role, action, adminUserId, previousVersion, row.Version, ct)
            .ConfigureAwait(false);

        return (row, wasCreated);
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
                ["role"]     = role,
                ["action"]   = action,
                ["tenantId"] = tenantId,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
}
