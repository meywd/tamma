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
/// POST …/policy/reset body (Story 43-15 D4). ABSENT → today's delete-all,
/// byte-identical. PRESENT with <see cref="Targets"/> → the BULK REVOKE: delete
/// exactly those <c>action</c>-scope rows (the surviving-toggles revoke), each
/// audited individually. Reusing the reset route rather than minting a new
/// mutating one keeps the endpoint-coverage ratchet unmoved (D4/D5).
/// </summary>
public sealed record ResetPolicyRequest(
    [property: JsonPropertyName("targets")] string[]? Targets);

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

    public static IResult GetCatalog(IActionEnforcementSites enforcementSites) =>
        Results.Ok(ActionCatalog.All.Select(d => new
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
            // Story 43-8 AC9. `siteKey` is the descriptor's DECLARED site — a string
            // an author wrote. `enforcementSites` is what the RUNNING host actually
            // has bound. They are different facts and the difference is the point:
            // an EMPTY array means this row governs nothing, and the UI must say so
            // rather than render it as governed.
            enforcementSites = enforcementSites.For(d.Key),
        }));

    // =======================================================================
    // GET /api/actions/policy?level=NN — the resolved, level-parameterized view
    // =======================================================================

    public static async Task<IResult> GetPolicy(
        int? level,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        IGovernancePrincipalResolver principals,
        IActionEnforcementSites enforcementSites,
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
            // Story 43-13 — deliberately on the defaulted Caller (Llm): the
            // policy view is the LLM-path view by definition — it renders what
            // the dial would do to the MODEL, which is the only caller the dial
            // governs (43-11 Amendment 4). Machinery rows short-circuit inside
            // the evaluator; surfacing that flag on this payload is 43-15's job.
            var decision = AutonomyGateEvaluator.Evaluate(
                new AutonomyQuery(d.Key, gp), snapshot, baseRules);

            // Story 43-15 (AC8, closes 43-11 AC12) — the toggle/greying fields.
            // levelOwned keys on the ladder WITHOUT the principal action row
            // (Amendment 2-E): the shipped level, group rows and the ceiling are
            // all included, so the group-row bypass is closed and the badge is
            // NEVER keyed on row presence alone.
            var withoutRow = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
                d, snapshot, baseRules);
            var hasActionToggle =
                snapshot.PrincipalActionRows.TryGetValue(d.Key.ToWire(), out var prow)
                && prow.MinAutonomy == AutonomyDial.Min;
            bool levelOwned;
            bool editable;
            string reason;
            if (d.IsMachinery)
            {
                // Machinery takes no threshold (43-13); the dial does not govern it.
                (levelOwned, editable, reason) = (false, false, "machinery-not-dial-governed");
            }
            else if (!d.Enforceable)
            {
                (levelOwned, editable, reason) = (false, false, "not-enforceable");
            }
            else
            {
                levelOwned = viewLevel >= withoutRow.EffectiveMinAutonomy;
                editable = !levelOwned;
                reason = levelOwned ? "level-owned" : "editable";
            }

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
                isMachinery = d.IsMachinery,
                // The shipped zone level (43-11) — a LEVEL, distinct from the
                // resolved effective threshold above.
                shippedLevel = d.DefaultMinAutonomy,
                // The ladder-without-the-action-row resolution the 409/greying
                // rule keys on (Amendment 2-E).
                ladderWithoutRow = withoutRow.EffectiveMinAutonomy,
                // Computed with THE SAME comparison the gate applies, so the
                // UI's greying rule cannot drift from the enforcement rule.
                automatedAtLevel = viewLevel >= decision.EffectiveMinAutonomy,
                // Story 43-15 AC8 — levelOwned / editable = !levelOwned / reason,
                // replacing the old unconditional editable = true (S3 deleted).
                levelOwned,
                editable,
                reason,
                // A per-action toggle standing ABOVE the current dial: an explicit
                // choice the dial has not caught up to. Keyed on the row being AT
                // Min AND the ladder-without-row exceeding the dial — never on row
                // presence alone (Amendment 2-E's named failure).
                toggleAboveDial =
                    !d.IsMachinery && hasActionToggle
                    && withoutRow.EffectiveMinAutonomy > dial,
                // Story 43-8 AC9 — the concrete sites this action is bound at, read
                // off the RUNNING host. An EMPTY array means the row is enforced
                // NOWHERE; rendering such a row as "governed" claims coverage that
                // does not exist, which is the lie the epic exists to prevent. See
                // ActionEnforcementSites for what a site does and does not prove.
                enforcementSites = enforcementSites.For(d.Key),
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
    // GET /api/actions/policy/diff?from=L1&to=L2 — the detent diff preview
    // =======================================================================

    /// <summary>
    /// Story 43-15 (AC5, D3) — the automated-set DELTA between two dial positions,
    /// the informed-consent payload behind a detent move. Symmetric: <c>from &lt; to</c>
    /// returns the newly-automated set (a RAISE); <c>from &gt; to</c> returns the
    /// de-automated set PLUS the surviving toggles (a LOWER). Each changed action
    /// carries its shipped level and last-30-day fire count / approve rate WHERE A
    /// SOURCE EXISTS — null (rendered "no data") where none does (Amendment 2-H).
    ///
    /// <para>The delta is computed over the principal's EFFECTIVE ladder (rows
    /// included), so a toggle at <see cref="AutonomyDial.Min"/> is automated at
    /// both ends and correctly never appears. The detents are the distinct SHIPPED
    /// levels (a catalog fact) so the control's positions are stable under the
    /// admin's own edits. Machinery is excluded from both (43-13).</para>
    /// </summary>
    public static async Task<IResult> GetPolicyDiff(
        int? from, int? to,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        IGovernancePrincipalResolver principals,
        ActionTelemetryReader telemetry,
        ClaimsPrincipal principal)
    {
        if (from is not int fromLevel || to is not int toLevel)
        {
            return Results.BadRequest(new
            {
                error = "both 'from' and 'to' query parameters are required",
                code = "ACTION_POLICY.INVALID",
            });
        }
        if (!AutonomyDial.IsValidLevel(fromLevel) || !AutonomyDial.IsValidLevel(toLevel))
        {
            return Results.BadRequest(new
            {
                error = $"from/to must be within [{AutonomyDial.Min}, {AutonomyDial.Max}]",
                code = "ACTION_POLICY.INVALID",
            });
        }

        var gp = await principals.ResolveAsync(principal);
        var snapshot = snapshots.GetSnapshot(gp);
        var baseRules = await ResolveBaseAsync(acceptanceRules, gp);

        var lower = Math.Min(fromLevel, toLevel);
        var higher = Math.Max(fromLevel, toLevel);
        var raising = toLevel > fromLevel;

        // Dial-governed (non-machinery) rows only; their effective threshold folds
        // in the principal ladder, ceiling and legacy floor (same as the gate).
        var dialGoverned = ActionCatalog.All.Where(d => !d.IsMachinery).ToList();

        var changes = new List<(ActionDescriptor Descriptor, int EffectiveMin, string Direction)>();
        foreach (var d in dialGoverned)
        {
            var decision = AutonomyGateEvaluator.Evaluate(
                new AutonomyQuery(d.Key, gp), snapshot, baseRules);
            var effectiveMin = decision.EffectiveMinAutonomy;
            var automatedAtFrom = fromLevel >= effectiveMin;
            var automatedAtTo = toLevel >= effectiveMin;
            if (automatedAtFrom == automatedAtTo)
            {
                continue; // no state change across the move
            }
            // Its threshold sits in (lower, higher]; the direction is the move's.
            changes.Add((d, effectiveMin, automatedAtTo ? "automates" : "de-automates"));
        }

        // Surviving toggles (a LOWER only): principal action rows at Min whose
        // ladder-WITHOUT-the-row exceeds the new dial — an explicit choice the
        // lowered dial does not reach, offered for bulk revoke.
        var survivingToggles = new List<object>();
        var survivingWires = new List<string>();
        if (!raising)
        {
            foreach (var d in dialGoverned)
            {
                if (!snapshot.PrincipalActionRows.TryGetValue(d.Key.ToWire(), out var row)
                    || row.MinAutonomy != AutonomyDial.Min)
                {
                    continue;
                }
                var withoutRow = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
                    d, snapshot, baseRules);
                if (withoutRow.EffectiveMinAutonomy > toLevel)
                {
                    survivingWires.Add(d.Key.ToWire());
                    survivingToggles.Add(new
                    {
                        key = d.Key.ToWire(),
                        group = d.Group.ToWire(),
                        title = d.Title,
                        shippedLevel = d.DefaultMinAutonomy,
                        ladderWithoutRow = withoutRow.EffectiveMinAutonomy,
                    });
                }
            }
        }

        // Telemetry for exactly the changed + surviving wires (one read).
        var wires = changes.Select(c => c.Descriptor.Key.ToWire())
            .Concat(survivingWires)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var tel = await telemetry.ReadAsync(gp.TenantId, gp.UserId, wires);

        var changeViews = changes.Select(c =>
        {
            var wire = c.Descriptor.Key.ToWire();
            var t = tel.TryGetValue(wire, out var v) ? v : ActionTelemetry.None;
            return new
            {
                key = wire,
                group = c.Descriptor.Group.ToWire(),
                title = c.Descriptor.Title,
                shippedLevel = c.Descriptor.DefaultMinAutonomy,
                effectiveMinAutonomy = c.EffectiveMin,
                direction = c.Direction,
                fireCount30d = t.FireCount30d,
                approveRate30d = t.ApproveRate30d,
            };
        }).ToList();

        var detents = dialGoverned
            .Select(d => d.DefaultMinAutonomy)
            .Append(baseRules.Rules.AutonomyLevel)
            .Where(AutonomyDial.IsValidLevel)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        return Results.Ok(new
        {
            from = fromLevel,
            to = toLevel,
            direction = fromLevel == toLevel ? "none" : (raising ? "raise" : "lower"),
            windowFrom = lower,
            windowTo = higher,
            detents,
            currentDial = baseRules.Rules.AutonomyLevel,
            changes = changeViews,
            survivingToggles,
        });
    }

    // =======================================================================
    // Principal writes (ActionsManage)
    // =======================================================================

    /// <summary>
    /// Story 43-15 (Amendment 2-E, AC1/AC3) — a per-action threshold write IS a
    /// TOGGLE now. The only legal body value is <see cref="AutonomyDial.Min"/>
    /// ("automated, period") — a constant function of the dial, so a later dial
    /// move can never silently kill or resurrect an explicit choice. The
    /// mint-time dial is audit provenance, never the stored arithmetic.
    ///
    /// <para>Precedence: (1) machinery / out-of-range / non-enforceable →
    /// <c>400</c> via <see cref="ValidateThresholdForAction"/> (43-13 rules,
    /// unchanged); (2) any value other than <see cref="AutonomyDial.Min"/> →
    /// <c>400 ACTION_POLICY.INVALID</c> naming the toggle encoding; (3) the
    /// ladder WITHOUT this action row already automates the target at the
    /// principal's dial → <c>409 ACTION_POLICY.LEVEL_OWNED</c> naming both
    /// numbers and the owning source (AC3, closing the group-row bypass — the
    /// predicate keys on group rows and the ceiling, not the shipped level
    /// alone).</para>
    /// </summary>
    public static async Task<IResult> PutActionThreshold(
        string ns, string key, SetActionThresholdRequest body,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        ActionGateEventsService events,
        ISoleUserProvider soleUser,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (!TryResolveAction(ns, key, out var descriptor, out var error)) return error!;
        if (body?.MinAutonomy is not int minAutonomy) return MissingField("minAutonomy");

        // (1) Machinery / non-enforceable / out-of-valid-range — 43-13's rules,
        //     ahead of the toggle-encoding check (Min on a machinery target is a
        //     400-machinery, not a 200: the row would do nothing).
        var invalid = ValidateThresholdForAction(minAutonomy)?.Invoke(descriptor!);
        if (invalid is not null) return invalid;

        // (2) The toggle encoding: the ONLY legal value is AutonomyDial.Min. This
        //     supersedes 43-11 AC8's "only legal value is the caller's dial" — a
        //     row at dial-at-mint is an inequality against a moving value; a row
        //     at Min is a constant "automated, period" (Amendment 2-E).
        if (minAutonomy != AutonomyDial.Min)
        {
            return Results.BadRequest(new
            {
                error = $"a per-action toggle stores minAutonomy = {AutonomyDial.Min} "
                    + "(\"automated, period\") and nothing else (Story 43-15, 43-11 "
                    + $"Amendment 2-E); got {minAutonomy}. The dial governs the level; "
                    + "the toggle only forces an above-dial action on.",
                code = "ACTION_POLICY.INVALID",
            });
        }

        var (tid, uid, resolveError) = await ResolvePrincipalKeysAsync(
            soleUser, principal, tenantContext, modeProvider);
        if (resolveError is not null) return resolveError;

        // (3) Level-ownership 409 — key on the ladder WITHOUT this action row,
        //     from FRESH rows (the F4 fresh-read pattern), so the group-row bypass
        //     is closed and the greying rule cannot drift from the gate.
        var baseRules = await ResolveBaseAsync(acceptanceRules, new GovernancePrincipal(tid, uid));
        var dial = baseRules.Rules.AutonomyLevel;
        var freshSnapshot = await BuildFreshSnapshotAsync(repository, tid, uid);
        var withoutRow = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            descriptor!, freshSnapshot, baseRules);
        if (dial >= withoutRow.EffectiveMinAutonomy)
        {
            return Results.Conflict(new
            {
                error = $"'{descriptor!.Key.ToWire()}' is already automated at dial {dial} "
                    + $"(the ladder resolves min {withoutRow.EffectiveMinAutonomy} via "
                    + $"{SourceWire(withoutRow.Source)} even without a per-action row), so a "
                    + "toggle is redundant. Level-owned actions are automated by RAISING the "
                    + "dial, not switched on individually.",
                code = "ACTION_POLICY.LEVEL_OWNED",
                dial,
                effectiveMinAutonomy = withoutRow.EffectiveMinAutonomy,
                source = SourceWire(withoutRow.Source),
            });
        }

        var wire = descriptor!.Key.ToWire();
        await repository.UpsertAsync(
            tid, uid, "action", wire, AutonomyDial.Min, null, null, null, null,
            principal.GetUserId());
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "action", wire,
            "minAutonomy", oldValue: null, newValue: AutonomyDial.Min, dialAtMint: dial);
        return Results.Ok(new { key = wire, minAutonomy = AutonomyDial.Min, dialAtMint = dial });
    }

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
            (repo, tid, uid, wire, actor, ct) => UpsertNonThresholdFieldAsync(
                repo, tid, uid, wire, body!.Enforce, enabled: null, allowedRoles: null, actor, ct),
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
            (repo, tid, uid, wire, actor, ct) => UpsertNonThresholdFieldAsync(
                repo, tid, uid, wire, enforce: null, body!.Enabled, allowedRoles: null, actor, ct),
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
            (repo, tid, uid, wire, actor, ct) => UpsertNonThresholdFieldAsync(
                repo, tid, uid, wire, enforce: null, enabled: null, body!.AllowedRoles, actor, ct),
            validate: null,
            repository, snapshots, events, soleUser, principal, tenantContext, modeProvider);

    public static async Task<IResult> DeleteAction(
        string ns, string key,
        IActionAssignmentRepository repository,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
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

            // Story 43-15 AC4 — name what now applies. The row is gone, so the
            // ladder-without-the-action-row IS the resolution; compute it from
            // FRESH rows against the same predicate the 409 uses.
            var baseRules = await ResolveBaseAsync(
                acceptanceRules, new GovernancePrincipal(tid, uid));
            var freshSnapshot = await BuildFreshSnapshotAsync(repository, tid, uid);
            var nowApplies = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
                descriptor, freshSnapshot, baseRules);
            var reason = nowApplies.Source == ActionAssignmentSource.AlwaysEscalateLegacy
                ? "the legacy always-escalate floor now applies"
                : "the next tier applies";
            return Results.Ok(new
            {
                message = "Assignment deleted; the next tier applies.",
                nowResolvesTo = nowApplies.EffectiveMinAutonomy,
                source = FallbackSourceWire(nowApplies.Source),
                reason,
            });
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
        if (InvalidGroupThreshold(g, minAutonomy) is IResult invalidForMembers)
            return invalidForMembers;

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
        ResetPolicyRequest? body,
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

        // Story 43-15 (AC7, D4) — BULK REVOKE of named action-scope rows. Present
        // targets delete only those rows (each audited individually); an absent
        // body keeps the delete-all behaviour byte-identical.
        if (body?.Targets is { Length: > 0 } targets)
        {
            var deleted = new List<string>();
            var missing = new List<string>();
            var unknown = new List<string>();
            foreach (var wire in targets)
            {
                if (!ActionKey.TryParse(wire, out var k)
                    || !ActionCatalog.TryGet(k, out _))
                {
                    unknown.Add(wire);
                    continue;
                }
                var wasDeleted = await repository.DeleteAsync(tid, uid, "action", wire);
                if (wasDeleted)
                {
                    deleted.Add(wire);
                    await events.EmitAssignmentChangedAsync(
                        tid, uid, principal.GetUserId(), "principal", "action", wire,
                        "deleted", oldValue: null, newValue: null);
                }
                else
                {
                    missing.Add(wire);
                }
            }
            if (deleted.Count > 0)
            {
                await snapshots.RefreshAsync();
            }
            return Results.Ok(new
            {
                removed = deleted.Count,
                deleted,
                missing,
                unknown,
            });
        }

        var removedAll = await repository.DeleteAllForPrincipalAsync(tid, uid);
        await snapshots.RefreshAsync();
        await events.EmitAssignmentChangedAsync(
            tid, uid, principal.GetUserId(), "principal", "policy", "*",
            "reset", oldValue: null, newValue: removedAll);
        return Results.Ok(new { removed = removedAll });
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
        if (InvalidGroupThreshold(g, minAutonomy) is IResult invalidForMembers)
            return invalidForMembers;

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
    /// Write enforce/enabled/roles without disturbing the threshold. The
    /// mode-row CHECK requires action rows to carry a threshold, so a FIRST
    /// write of these fields must materialize one — but an EXISTING row's
    /// stored threshold must never be re-derived (adversarial review F4,
    /// 2026-07-29: the old path re-supplied MinAutonomy from the ≤60s-stale
    /// snapshot, so an enforce write on pod B within the TTL of a threshold
    /// tightening on pod A silently reverted the tightening). Resolution:
    /// <list type="bullet">
    /// <item>existing row (decided by a FRESH repository read, never the
    /// snapshot) → pass a null threshold; <c>UpsertAsync</c>'s per-field
    /// independence leaves the stored value untouched.</item>
    /// <item>genuinely-new row → MATERIALIZE-AND-PIN: the threshold is pinned
    /// at the current effective value computed from FRESH repository reads
    /// through the evaluator's own ladder. Documented design consequence
    /// (story amendment 2026-07-29): the pinned action row thereafter beats
    /// group-scope rows (<c>??</c> inside the principal ladder), so a LATER
    /// group tightening no longer reaches this member, and the pin survives
    /// group-row deletion. 43-6's UI surfaces provenance
    /// (<c>action-override</c>) so an admin can see the pin.</item>
    /// </list>
    /// </summary>
    private static async Task<(Tamma.Data.Entities.ActionAssignment Entity, bool WasCreated)>
        UpsertNonThresholdFieldAsync(
            IActionAssignmentRepository repository,
            Guid? tenantId, Guid? userId, string actionWire,
            bool? enforce, bool? enabled, string[]? allowedRoles,
            Guid? actingUserId, CancellationToken ct)
    {
        var principalRows = await repository.ListForPrincipalAsync(tenantId, userId, ct);
        int? threshold = null;
        if (!principalRows.Any(r =>
                r.TargetKind == "action"
                && string.Equals(r.TargetKey, actionWire, StringComparison.Ordinal)))
        {
            var platformRows = await repository.ListPlatformAsync(ct);
            threshold = PinnedEffectiveThreshold(actionWire, platformRows, principalRows);
        }

        return await repository.UpsertAsync(
            tenantId, userId, "action", actionWire, threshold,
            enforce, enabled, allowedRoles, null, actingUserId, ct);
    }

    /// <summary>The materialize-and-pin value for a genuinely-new row: the
    /// current effective threshold from FRESH rows (not the snapshot), via the
    /// evaluator's own ladder so the pin cannot drift from enforcement.</summary>
    private static int PinnedEffectiveThreshold(
        string actionWire,
        IReadOnlyList<Tamma.Data.Entities.ActionAssignment> platformRows,
        IReadOnlyList<Tamma.Data.Entities.ActionAssignment> principalRows)
    {
        if (!ActionKey.TryParse(actionWire, out var k)
            || !ActionCatalog.TryGet(k, out var d) || d is null)
        {
            return AutonomyDial.Min; // unreachable: the route already resolved the descriptor
        }

        // FromSuccessfulRead is the honest factory here (review 2.2): both row
        // sets were just read straight out of the repository on this request and
        // both reads returned — this snapshot is NOT the degraded one.
        var snapshot = GovernancePolicySnapshot.FromSuccessfulRead(
            RowsByKind(platformRows, "action"),
            RowsByKind(platformRows, "group"),
            RowsByKind(principalRows, "action"),
            RowsByKind(principalRows, "group"));
        return AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(d, snapshot).EffectiveMinAutonomy;
    }

    /// <summary>
    /// Story 43-15 — a FRESH governance snapshot for one principal, built from
    /// repository reads on THIS request (never the ≤60s-stale provider snapshot),
    /// so the level-ownership 409 and the DELETE fallback see the true current
    /// rows. The F4 fresh-read pattern (<see cref="PinnedEffectiveThreshold"/>),
    /// lifted to a whole snapshot.
    /// </summary>
    private static async Task<GovernancePolicySnapshot> BuildFreshSnapshotAsync(
        IActionAssignmentRepository repository, Guid? tenantId, Guid? userId)
    {
        var principalRows = await repository.ListForPrincipalAsync(tenantId, userId);
        var platformRows = await repository.ListPlatformAsync();
        return GovernancePolicySnapshot.FromSuccessfulRead(
            RowsByKind(platformRows, "action"),
            RowsByKind(platformRows, "group"),
            RowsByKind(principalRows, "action"),
            RowsByKind(principalRows, "group"));
    }

    /// <summary>Map the surviving ladder source to the DELETE-fallback wire
    /// vocabulary (<c>group</c> | <c>shipped</c> | <c>ceiling</c>). The legacy
    /// floor reports <c>shipped</c> with the floor noted in <c>reason</c>.</summary>
    private static string FallbackSourceWire(ActionAssignmentSource source) => source switch
    {
        ActionAssignmentSource.GroupOverride => "group",
        ActionAssignmentSource.PlatformCeiling => "ceiling",
        ActionAssignmentSource.AlwaysEscalateLegacy => "shipped",
        _ => "shipped",
    };

    private static IReadOnlyDictionary<string, ActionAssignmentValue> RowsByKind(
        IReadOnlyList<Tamma.Data.Entities.ActionAssignment> rows, string kind)
        => rows
            .Where(r => string.Equals(r.TargetKind, kind, StringComparison.Ordinal))
            .ToDictionary(
                r => r.TargetKey,
                r => new ActionAssignmentValue(r.MinAutonomy, r.Enforce, r.Enabled, r.AllowedRoles),
                StringComparer.Ordinal);

    /// <summary>
    /// Story 43-13 D7 — the wire code for a threshold write on a MACHINERY
    /// target (or a group with nothing but machinery to govern). Distinct from
    /// <c>ACTION_POLICY.INVALID</c>: the value may be perfectly legal — it is
    /// the TARGET that the dial does not govern (43-11 Amendment 4).
    /// </summary>
    internal const string MachineryNotDialGovernedCode =
        "ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED";

    /// <summary>
    /// Adversarial review F5 (2026-07-29), REWRITTEN by Story 43-13 (D7): a
    /// machinery member is provably INERT to a group threshold — the evaluator
    /// short-circuits it before the dial comparison — so the old rejection of
    /// mid-range values on groups containing non-escalatable
    /// (<c>automation:*</c>) members policed rows no threshold can reach any
    /// more, and is removed: mid-range group writes are legal whenever the
    /// group has at least one enforceable DIAL member. A group with NO such
    /// member takes no threshold at all — a 200 that governs nothing is the
    /// false affordance this epic keeps hunting down. (No shipped group is in
    /// that state today — even platform-automation carries
    /// <c>effect:engine.channel-outbox.enqueue</c> and the three
    /// <c>schedule.*</c> dial rows — so the branch is a guard for a future
    /// re-partition, pinned by the code rather than reachable data.)
    /// Returns the 400, or null when the write is legal for the whole group.
    /// </summary>
    private static IResult? InvalidGroupThreshold(ActionGroup group, int minAutonomy)
    {
        var governable = ActionCatalog.ByGroup[group]
            .Select(k => ActionCatalog.ByKey[k])
            .Any(d => d.Enforceable && !d.IsMachinery);
        if (governable)
        {
            return null;
        }

        return Results.BadRequest(new
        {
            error = $"every enforceable member of group '{group.ToWire()}' is machinery "
                + "(Story 43-13: deterministic services are never dial-governed), so a "
                + $"minAutonomy of {minAutonomy} here could govern nothing. Switch an actor "
                + "off with its per-action enabled=false instead.",
            code = MachineryNotDialGovernedCode,
        });
    }

    private static Func<ActionDescriptor, IResult?>? ValidateThresholdForAction(int? minAutonomy)
    {
        if (minAutonomy is not int value) return null; // missing-field handled separately
        return descriptor =>
        {
            // Story 43-13 (D7) — MACHINERY first, for ANY value, and ahead of
            // the enforceability check (for effect:secret.reveal the machinery
            // classification is the stronger, newer fact). The evaluator never
            // resolves a machinery row through the dial, so accepting a
            // threshold here — even Min, which the old two-state rule allowed —
            // would store a row that does nothing.
            if (descriptor.IsMachinery)
                return Results.BadRequest(new
                {
                    error = $"'{descriptor.Key.ToWire()}' is machinery (Story 43-13, 43-11 "
                        + "Amendment 4): deterministic services and plumbing are never "
                        + "dial-governed, so no threshold may be assigned. Use enabled=false "
                        + "to switch the actor off.",
                    code = MachineryNotDialGovernedCode,
                });
            if (!AutonomyDial.IsValidThreshold(value))
                return InvalidThreshold(value);
            if (!descriptor.Enforceable)
                return Results.BadRequest(new
                {
                    error = $"'{descriptor.Key.ToWire()}' is informational only — its threshold "
                        + "can never be enforced and may not be assigned (epic OQ2).",
                    code = "ACTION_POLICY.NOT_ENFORCEABLE",
                });
            // The old two-state (Min/AlwaysHuman) rule for non-escalatable
            // targets is GONE (43-13 AC5): every EscalatableToHuman=false row
            // is an automation:* row and every one is machinery, so the branch
            // above subsumes it entirely.
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
