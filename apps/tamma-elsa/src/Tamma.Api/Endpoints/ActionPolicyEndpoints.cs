using System.Security.Claims;
using System.Text.Json.Serialization;
using Tamma.Api.Auth;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

// ── Single-field write DTOs (Story 43-6 AC2 / the 43-0 bug class) ───────────
// Every write endpoint takes ONE nullable-required field: a body missing the
// field is a 400, NEVER a defaulted write — a safety catalog that silently
// reset a gate would be materially worse than a prompt store that does.

/// <summary>PUT …/threshold body.</summary>
public sealed record SetActionThresholdRequest(
    [property: JsonPropertyName("minAutonomy")] int? MinAutonomy);

/// <summary>PUT …/enforce body.</summary>
public sealed record SetActionEnforceRequest(
    [property: JsonPropertyName("enforce")] bool? Enforce);

/// <summary>PUT …/enabled body.</summary>
public sealed record SetActionEnabledRequest(
    [property: JsonPropertyName("enabled")] bool? Enabled);

/// <summary>PUT …/roles body (empty array clears the restriction).</summary>
public sealed record SetActionRolesRequest(
    [property: JsonPropertyName("allowedRoles")] string[]? AllowedRoles);

/// <summary>
/// Minimal-API handlers for <c>/api/actions</c> (tenant/user policy surface)
/// and <c>/api/admin/actions/ceiling</c> (the platform ceiling — the
/// load-bearing protection: the only thing standing between a tenant admin and
/// full automation of a destructive action; epic README OQ4).
///
/// <para>RBAC per the epic's scoping table: reads ride <c>AuthenticatedAny</c>
/// (every role-holder and the orchestrator need the effective policy);
/// principal writes take <c>ActionsManage</c> (tenant_owner/tenant_admin —
/// member → 403); ceiling writes take <c>PlatformOwnerAccess</c>. Handlers
/// branch on <see cref="ITammaModeProvider.Mode"/> + <see cref="ITenantContext"/>
/// INLINE, exactly like <c>AcceptanceRulesEndpoints</c> (no shared helper —
/// six stores repeat it; introducing the first helper inside a safety story
/// would be a half-migration).</para>
///
/// <para>Every write refreshes the governance snapshot store
/// (invalidate-on-write) and emits an <c>ACTION.GATE.ASSIGNMENT_CHANGED</c>
/// audit event best-effort — the assignment row is the durable fact.</para>
/// </summary>
public static class ActionPolicyEndpoints
{
    // =======================================================================
    // GET /api/actions/dial — publish AutonomyDial so no client hardcodes it
    // =======================================================================

    public static IResult GetDial() => Results.Ok(new
    {
        min = AutonomyDial.Min,
        max = AutonomyDial.Max,
        alwaysHuman = AutonomyDial.AlwaysHuman,
        @default = AcceptanceDefaults.DefaultAutonomyLevel,
    });

    // =======================================================================
    // GET /api/actions/catalog — the full code-resident vocabulary
    // =======================================================================

    public static IResult GetCatalog() => Results.Ok(ActionCatalog.All.Select(d => new
    {
        key = d.Key.ToWire(),
        ns = d.Key.Ns.ToWire(),
        group = d.Group.ToWire(),
        risk = d.Risk.ToWire(),
        title = d.Title,
        summary = d.Summary,
        reversible = d.Reversible,
        defaultMinAutonomy = d.DefaultMinAutonomy,
        escalatableToHuman = d.EscalatableToHuman,
        enforceable = d.Enforceable,
        siteKey = d.SiteKey,
    }));

    // =======================================================================
    // GET /api/actions/policy?level=NN — the resolved, level-parameterized view
    // =======================================================================

    public static async Task<IResult> GetPolicy(
        int? level,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        IGovernancePrincipalResolver principals,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (level is int l && !AutonomyDial.IsValidLevel(l))
        {
            return Results.BadRequest(new
            {
                error = $"level must be within [{AutonomyDial.Min}, {AutonomyDial.Max}]",
                code = "ACTION_POLICY.INVALID",
            });
        }

        var gp = await principals.ResolveAsync(principal);
        var snapshot = snapshots.GetSnapshot(gp);
        var baseRules = await ResolveBaseAsync(acceptanceRules, gp);
        var dial = baseRules.Rules.AutonomyLevel;
        var viewLevel = level ?? dial;

        var actions = ActionCatalog.All.Select(d =>
        {
            var decision = AutonomyGateEvaluator.Evaluate(
                new AutonomyQuery(d.Key, gp), snapshot, baseRules);
            return new
            {
                key = d.Key.ToWire(),
                group = d.Group.ToWire(),
                risk = d.Risk.ToWire(),
                title = d.Title,
                summary = d.Summary,
                siteKey = d.SiteKey,
                minAutonomy = decision.EffectiveMinAutonomy,
                source = SourceWire(decision.Source),
                enforce = decision.Enforced,
                enabled = decision.Enabled,
                allowedRoles = decision.AllowedRoles,
                escalatableToHuman = d.EscalatableToHuman,
                enforceable = d.Enforceable,
                // Computed with THE SAME comparison the gate applies, so the
                // UI's greying rule cannot drift from the enforcement rule.
                automatedAtLevel = viewLevel >= decision.EffectiveMinAutonomy,
                // A row automated at the previewed level is still editable —
                // setting a threshold that only matters at a future lower
                // floor is the entire point (S3).
                editable = true,
            };
        }).ToList();

        var groups = Enum.GetValues<ActionGroup>().Select(g => new
        {
            group = g.ToWire(),
            description = ActionGroupExtensions.Descriptions[g],
            members = ActionCatalog.ByGroup[g].Count,
            principalRow = RowView(snapshot.PrincipalGroupRows, g.ToWire()),
            platformRow = RowView(snapshot.PlatformGroupRows, g.ToWire()),
        }).ToList();

        return Results.Ok(new
        {
            dial = new
            {
                min = AutonomyDial.Min,
                max = AutonomyDial.Max,
                alwaysHuman = AutonomyDial.AlwaysHuman,
                @default = AcceptanceDefaults.DefaultAutonomyLevel,
                current = dial,
                viewLevel,
            },
            groups,
            actions,
        });
    }

    // =======================================================================
    // Principal writes (ActionsManage)
    // =======================================================================

    public static Task<IResult> PutActionThreshold(
        string ns, string key, SetActionThresholdRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
        => WriteActionField(ns, key, "minAutonomy", body?.MinAutonomy,
            (repo, tid, uid, wire, actor, ct) => repo.UpsertAsync(
                tid, uid, "action", wire, body!.MinAutonomy, null, null, null, null, actor, ct),
            ValidateThresholdForAction(body?.MinAutonomy),
            repository, snapshots, events, soleUser, principal, tenantContext, modeProvider);

    public static Task<IResult> PutActionEnforce(
        string ns, string key, SetActionEnforceRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
        => WriteActionField(ns, key, "enforce", body?.Enforce,
            (repo, tid, uid, wire, actor, ct) => repo.UpsertAsync(
                tid, uid, "action", wire, RequiredThresholdFor(tid, uid, wire, snapshots),
                body!.Enforce, null, null, null, actor, ct),
            validate: null,
            repository, snapshots, events, soleUser, principal, tenantContext, modeProvider);

    public static Task<IResult> PutActionEnabled(
        string ns, string key, SetActionEnabledRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
        => WriteActionField(ns, key, "enabled", body?.Enabled,
            (repo, tid, uid, wire, actor, ct) => repo.UpsertAsync(
                tid, uid, "action", wire, RequiredThresholdFor(tid, uid, wire, snapshots),
                null, body!.Enabled, null, null, actor, ct),
            validate: null,
            repository, snapshots, events, soleUser, principal, tenantContext, modeProvider);

    public static Task<IResult> PutActionRoles(
        string ns, string key, SetActionRolesRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
        => WriteActionField(ns, key, "allowedRoles", (object?)body?.AllowedRoles,
            (repo, tid, uid, wire, actor, ct) => repo.UpsertAsync(
                tid, uid, "action", wire, RequiredThresholdFor(tid, uid, wire, snapshots),
                null, null, body!.AllowedRoles, null, actor, ct),
            validate: null,
            repository, snapshots, events, soleUser, principal, tenantContext, modeProvider);

    public static async Task<IResult> DeleteAction(
        string ns, string key,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (TryResolveAction(ns, key, out var descriptor, out var error))
        {
            var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
                soleUser, principal, tenantContext, modeProvider);
            if (resolveError is not null) return resolveError;

            var wire = descriptor!.Key.ToWire();
            var deleted = await repository.DeleteAsync(tid, uid, "action", wire);
            if (!deleted)
                return Results.NotFound(new { error = "No assignment row for this action" });

            await snapshots.RefreshAsync();
            await events.EmitAssignmentChangedAsync(
                tid, uid, principal.GetUserId(), "principal", "action", wire,
                "deleted", oldValue: null, newValue: null);
            return Results.Ok(new { message = "Assignment deleted; the next tier applies." });
        }
        return error!;
    }

    public static async Task<IResult> PutGroupThreshold(
        string group, SetActionThresholdRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (!TryResolveGroup(group, out var g, out var error)) return error!;
        if (body?.MinAutonomy is not int minAutonomy)
            return MissingField("minAutonomy");
        if (!AutonomyDial.IsValidThreshold(minAutonomy))
            return InvalidThreshold(minAutonomy);

        var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
            soleUser, principal, tenantContext, modeProvider);
        if (resolveError is not null) return resolveError;

        await repository.UpsertAsync(
            tid, uid, "group", g.ToWire(), minAutonomy, null, null, null, null,
            principal.GetUserId());
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "group", g.ToWire(),
            "minAutonomy", oldValue: null, newValue: minAutonomy);
        return Results.Ok(new { group = g.ToWire(), minAutonomy });
    }

    public static async Task<IResult> DeleteGroup(
        string group,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (!TryResolveGroup(group, out var g, out var error)) return error!;

        var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
            soleUser, principal, tenantContext, modeProvider);
        if (resolveError is not null) return resolveError;

        var deleted = await repository.DeleteAsync(tid, uid, "group", g.ToWire());
        if (!deleted)
            return Results.NotFound(new { error = "No assignment row for this group" });

        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "group", g.ToWire(),
            "deleted", oldValue: null, newValue: null);
        return Results.Ok(new { message = "Assignment deleted; the next tier applies." });
    }

    public static async Task<IResult> ResetPolicy(
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
            soleUser, principal, tenantContext, modeProvider);
        if (resolveError is not null) return resolveError;

        var removed = await repository.DeleteAllForPrincipalAsync(tid, uid);
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "policy", "*",
            "reset", oldValue: null, newValue: removed);
        return Results.Ok(new { removed });
    }

    // =======================================================================
    // Platform ceiling writes (PlatformOwnerAccess) — the load-bearing
    // protection: only the platform owner can author ceiling rows, and a
    // tenant admin can never lower them (the evaluator's max()).
    // =======================================================================

    public static async Task<IResult> PutCeilingActionThreshold(
        string ns, string key, SetActionThresholdRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ClaimsPrincipal principal)
    {
        if (!TryResolveAction(ns, key, out var descriptor, out var error)) return error!;
        if (body?.MinAutonomy is not int minAutonomy) return MissingField("minAutonomy");
        var invalid = ValidateThresholdForAction(minAutonomy)?.Invoke(descriptor!);
        if (invalid is not null) return invalid;

        var wire = descriptor!.Key.ToWire();
        await repository.UpsertAsync(
            null, null, "action", wire, minAutonomy, null, null, null, null,
            principal.GetUserId());
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            null, null, principal.GetUserId(), "platform-ceiling", "action", wire,
            "minAutonomy", oldValue: null, newValue: minAutonomy);
        return Results.Ok(new { key = wire, minAutonomy, scope = "platform-ceiling" });
    }

    public static async Task<IResult> PutCeilingGroupThreshold(
        string group, SetActionThresholdRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ClaimsPrincipal principal)
    {
        if (!TryResolveGroup(group, out var g, out var error)) return error!;
        if (body?.MinAutonomy is not int minAutonomy) return MissingField("minAutonomy");
        if (!AutonomyDial.IsValidThreshold(minAutonomy)) return InvalidThreshold(minAutonomy);

        await repository.UpsertAsync(
            null, null, "group", g.ToWire(), minAutonomy, null, null, null, null,
            principal.GetUserId());
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            null, null, principal.GetUserId(), "platform-ceiling", "group", g.ToWire(),
            "minAutonomy", oldValue: null, newValue: minAutonomy);
        return Results.Ok(new { group = g.ToWire(), minAutonomy, scope = "platform-ceiling" });
    }

    public static async Task<IResult> DeleteCeilingAction(
        string ns, string key,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ClaimsPrincipal principal)
    {
        if (!TryResolveAction(ns, key, out var descriptor, out var error)) return error!;
        var wire = descriptor!.Key.ToWire();
        var deleted = await repository.DeleteAsync(null, null, "action", wire);
        if (!deleted) return Results.NotFound(new { error = "No ceiling row for this action" });
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            null, null, principal.GetUserId(), "platform-ceiling", "action", wire,
            "deleted", oldValue: null, newValue: null);
        return Results.Ok(new { message = "Ceiling removed." });
    }

    public static async Task<IResult> DeleteCeilingGroup(
        string group,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ClaimsPrincipal principal)
    {
        if (!TryResolveGroup(group, out var g, out var error)) return error!;
        var deleted = await repository.DeleteAsync(null, null, "group", g.ToWire());
        if (!deleted) return Results.NotFound(new { error = "No ceiling row for this group" });
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            null, null, principal.GetUserId(), "platform-ceiling", "group", g.ToWire(),
            "deleted", oldValue: null, newValue: null);
        return Results.Ok(new { message = "Ceiling removed." });
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private static async Task<IResult> WriteActionField(
        string ns, string key, string field, object? fieldValue,
        Func<IActionAssignmentRepository, Guid?, Guid?, string, Guid?, CancellationToken,
            Task<(Tamma.Data.Entities.ActionAssignment Entity, bool WasCreated)>> write,
        Func<ActionDescriptor, IResult?>? validate,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (!TryResolveAction(ns, key, out var descriptor, out var error)) return error!;
        if (fieldValue is null) return MissingField(field);
        var invalid = validate?.Invoke(descriptor!);
        if (invalid is not null) return invalid;

        var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
            soleUser, principal, tenantContext, modeProvider);
        if (resolveError is not null) return resolveError;

        var wire = descriptor!.Key.ToWire();
        await write(repository, tid, uid, wire, principal.GetUserId(), default);
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "action", wire,
            field, oldValue: null, newValue: fieldValue);
        return Results.Ok(new { key = wire, field, value = fieldValue });
    }

    /// <summary>
    /// The mode-row CHECK requires action/group rows to carry a threshold, so
    /// a first write of enforce/enabled/roles needs one: reuse the row's
    /// stored threshold when present, else the CURRENT effective threshold
    /// (which, with no rows, is the shipped default — behaviour-preserving).
    /// On an existing row the repository leaves the stored value unchanged.
    /// </summary>
    private static int RequiredThresholdFor(
        Guid? tenantId, Guid? userId, string actionWire,
        IGovernancePolicySnapshotProvider snapshots)
    {
        var gp = tenantId is Guid t
            ? GovernancePrincipal.ForTenant(t)
            : userId is Guid u ? GovernancePrincipal.ForUser(u) : GovernancePrincipal.Platform;
        var snapshot = snapshots.GetSnapshot(gp);
        if (snapshot.PrincipalActionRows.TryGetValue(actionWire, out var row)
            && row.MinAutonomy is int stored)
        {
            return stored;
        }
        return ActionKey.TryParse(actionWire, out var k)
            && ActionCatalog.TryGet(k, out var d) && d is not null
            ? AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(d, snapshot).EffectiveMinAutonomy
            : AutonomyDial.Min;
    }

    private static Func<ActionDescriptor, IResult?>? ValidateThresholdForAction(int? minAutonomy)
    {
        if (minAutonomy is not int value) return null; // missing-field handled separately
        return descriptor =>
        {
            if (!AutonomyDial.IsValidThreshold(value))
                return InvalidThreshold(value);
            if (!descriptor.Enforceable)
                return Results.BadRequest(new
                {
                    error = $"'{descriptor.Key.ToWire()}' is informational only — its threshold "
                        + "can never be enforced and may not be assigned (epic OQ2).",
                    code = "ACTION_POLICY.NOT_ENFORCEABLE",
                });
            // automation:* targets are two-state (a sweeper cannot wait for a
            // person — Seam D is deny-only): only Min or AlwaysHuman.
            if (!descriptor.EscalatableToHuman
                && value != AutonomyDial.Min && value != AutonomyDial.AlwaysHuman)
                return Results.BadRequest(new
                {
                    error = $"'{descriptor.Key.ToWire()}' is not escalatable to a human; a "
                        + $"mid-range threshold would silently behave as Deny. Use "
                        + $"{AutonomyDial.Min} (automated) or {AutonomyDial.AlwaysHuman} (off).",
                    code = "ACTION_POLICY.INVALID",
                });
            return null;
        };
    }

    private static bool TryResolveAction(
        string ns, string key, out ActionDescriptor? descriptor, out IResult? error)
    {
        descriptor = null;
        // Case-sensitive ordinal (the EnumWire posture): bad casing is a 400,
        // not a coercion.
        if (!ActionKey.TryParse($"{ns}:{key}", out var actionKey)
            || !ActionCatalog.TryGet(actionKey, out descriptor)
            || descriptor is null)
        {
            error = Results.BadRequest(new
            {
                error = $"'{ns}:{key}' is not a catalogued action",
                code = "ACTION_POLICY.UNKNOWN_ACTION",
            });
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryResolveGroup(string group, out ActionGroup g, out IResult? error)
    {
        if (!Tamma.Api.Services.Agents.EnumWire<ActionGroup>.TryParse(group, out g))
        {
            error = Results.BadRequest(new
            {
                error = $"'{group}' is not an action group",
                code = "ACTION_POLICY.UNKNOWN_GROUP",
            });
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// The inline mode split (the <c>AcceptanceRulesEndpoints</c> shape):
    /// SaaS-with-tenant keys the TENANT row; otherwise the sole/authenticated
    /// USER row. SaaS without a tenant context is a 409 — writing a policy row
    /// for an unresolvable principal would either be lost or, worse, land on
    /// the platform scope.
    /// </summary>
    private static async Task<(Guid? TenantId, Guid? UserId, IResult? Error)>
        ResolvePrincipalKeysAsync(
            ISoleUserProvider soleUser,
            ClaimsPrincipal principal,
            ITenantContext tenantContext,
            ITammaModeProvider modeProvider)
    {
        if (modeProvider.Mode == TammaMode.SaaS)
        {
            if (tenantContext.TenantId is Guid tenantId)
                return (tenantId, null, null);
            return (null, null, Results.Conflict(new
            {
                error = "No tenant context — a SaaS policy write requires a resolvable tenant",
                code = "ACTION_POLICY.PRINCIPAL_UNRESOLVED",
            }));
        }

        if (principal.GetUserId() is Guid userId)
            return (null, userId, null);

        try
        {
            return (null, await soleUser.GetSoleUserIdAsync(), null);
        }
        catch (TammaError te)
        {
            return (null, null, Results.Conflict(new { error = te.Message, code = te.Code }));
        }
    }

    private static async Task<ResolvedAcceptanceRules> ResolveBaseAsync(
        IAcceptanceRulesResolver acceptanceRules, GovernancePrincipal gp)
    {
        try
        {
            if (gp.TenantId is Guid tid) return await acceptanceRules.ResolveBaseForTenantAsync(tid);
            if (gp.UserId is Guid uid) return await acceptanceRules.ResolveBaseAsync(uid);
        }
        catch (Exception)
        {
            // Degrade to shipped defaults — the AcceptanceRulesEndpoints posture.
        }
        return new ResolvedAcceptanceRules(
            AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "base",
            DateTimeOffset.UtcNow);
    }

    private static IResult MissingField(string field) => Results.BadRequest(new
    {
        error = $"Body field '{field}' is required (single-field write; a missing "
            + "field is never defaulted — Story 43-6 AC2)",
        code = "ACTION_POLICY.MISSING_FIELD",
    });

    private static IResult InvalidThreshold(int value) => Results.BadRequest(new
    {
        error = $"minAutonomy {value} is outside [{AutonomyDial.Min}, {AutonomyDial.Max}] "
            + $"∪ {{{AutonomyDial.AlwaysHuman}}}",
        code = "ACTION_POLICY.INVALID",
    });

    private static object? RowView(
        IReadOnlyDictionary<string, ActionAssignmentValue> rows, string key)
        => rows.TryGetValue(key, out var row)
            ? new
            {
                minAutonomy = row.MinAutonomy,
                enforce = row.Enforce,
                enabled = row.Enabled,
                allowedRoles = row.AllowedRoles,
            }
            : null;

    private static string SourceWire(ActionAssignmentSource source) => source switch
    {
        ActionAssignmentSource.PlatformCeiling => "platform-ceiling",
        ActionAssignmentSource.AlwaysEscalateLegacy => "always-escalate-legacy",
        ActionAssignmentSource.ActionOverride => "action-override",
        ActionAssignmentSource.GroupOverride => "group-override",
        _ => "system-default",
    };
}
