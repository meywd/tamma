using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.AcceptanceRules;
using Tamma.Api.Services.AcceptanceRules;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Minimal-API handlers for <c>/api/acceptance-rules</c> (Story 39-5 step 9). The
/// heavy lifting lives in <see cref="AcceptanceRulesService"/>; DCB audit events
/// are emitted from within its mutation methods (D12). Handlers branch on
/// <see cref="ITammaModeProvider.Mode"/> + <see cref="ITenantContext"/> exactly
/// like <c>PromptEndpoints</c>. The literal <c>base</c> segment addresses the
/// principal base row (the dial).
///
/// <para><b>The repair path for a corrupt stored row</b> (review MODERATE-2,
/// 2026-07-29): rows are validated defensively on READ (39-5 D3), so a row whose
/// JSON is malformed, or whose body no longer passes
/// <c>AcceptanceRules.Validate()</c>, makes <see cref="GetResolved"/> and
/// <see cref="Upsert"/> answer <c>400</c> naming the problem rather than
/// <c>500</c>. Because per-type resolution FALLS THROUGH to the base row, ONE
/// corrupt base row affects every document type. The repair is
/// <c>DELETE /api/acceptance-rules/{key}</c> — which never reads the body — to
/// drop to the next tier, then <c>PUT</c> the wanted rules. Upsert cannot be the
/// repair by itself: since 43-0 it must READ the in-force requirement in order
/// to preserve it.</para>
/// </summary>
public static class AcceptanceRulesEndpoints
{
    // =======================================================================
    // List — resolved rules for every document type + provenance
    // =======================================================================

    public static async Task<IResult> ListEffective(
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        ILoggerFactory loggerFactory)
    {
        try
        {
            IReadOnlyList<ResolvedAcceptanceRules> resolved;
            if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
                resolved = await store.ListEffectiveForTenantAsync(tenantId);
            else if (tenantContext.TenantId is null)
                // No tenant yet (bootstrap) — every type resolves to the shipped
                // static default; no DB read needed. Mirror PromptEndpoints'
                // "no overrides yet is a valid state" degradation.
                resolved = DefaultsForAllTypes();
            else
                resolved = await store.ListEffectiveAsync(principal.GetUserId());
            return Results.Ok(resolved);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            || ex is Npgsql.NpgsqlException
            || ex is Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            loggerFactory.CreateLogger("AcceptanceRulesEndpoints.ListEffective").LogError(ex,
                "ListEffective: returning shipped defaults because the per-tenant acceptance-rules store " +
                "could not be queried (tenant={TenantId})", tenantContext.TenantId);
            return Results.Ok(DefaultsForAllTypes());
        }
    }

    // =======================================================================
    // Defaults (read-only) — the shipped principal-base row
    // =======================================================================

    public static IResult GetDefaults() => Results.Ok(AcceptanceDefaults.Rules);

    // =======================================================================
    // Get resolved (one document type, or the literal `base` dial row)
    // =======================================================================

    public static async Task<IResult> GetResolved(
        string documentTypeKey,
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var saas = modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid;

        try
        {
            if (string.Equals(documentTypeKey, AcceptanceRulesService.BaseRowKeyLiteral, StringComparison.Ordinal))
            {
                var baseResolved = saas
                    ? await store.ResolveBaseForTenantAsync((Guid)tenantContext.TenantId!)
                    : await store.ResolveBaseAsync(principal.GetUserId());
                return Results.Ok(baseResolved);
            }

            if (!Tamma.Core.Documents.DocumentTypeKeyExtensions.TryParse(documentTypeKey, out var type))
                return Results.BadRequest(new { error = "Unknown document type", code = "DOCUMENT.TYPE.UNKNOWN", input = documentTypeKey });

            var resolved = saas
                ? await store.ResolveForTenantAsync((Guid)tenantContext.TenantId!, type)
                : await store.ResolveAsync(principal.GetUserId(), type);
            return Results.Ok(resolved);
        }
        catch (TammaError te)
        {
            // A stored row that no longer validates (ACCEPTANCE_RULES.INVALID).
            return Results.BadRequest(new { error = te.Message, code = te.Code });
        }
        catch (System.Text.Json.JsonException jx)
        {
            // Same class as Upsert's (review MODERATE-2): the read-side
            // validation is deliberate (39-5 D3 — a corrupt row throws, never
            // degrades), but the caller must be told what to do about it rather
            // than handed a 500.
            return Results.BadRequest(StoredRowUnreadable(documentTypeKey, jx));
        }
    }

    // =======================================================================
    // Upsert (AcceptanceRulesManage)
    // =======================================================================

    public static async Task<IResult> Upsert(
        string documentTypeKey,
        AcceptanceRulesUpsertRequest req,
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        // Story 43-15 (D7) — optional so existing direct-call unit tests compile;
        // DI injects both for the HTTP path. The dial-lower survival record is
        // skipped when either is absent (best-effort).
        Tamma.Data.Repositories.IActionAssignmentRepository? actionRepo = null,
        Tamma.Api.Services.Actions.ActionGateEventsService? actionEvents = null)
    {
        var userId = principal.GetUserId();

        // Story 43-15 (D7) — when the BASE row's dial DECREASES, per-action
        // toggles standing above the new dial survive VISIBLY. Capture the old
        // dial before the write so the drop can be detected; best-effort, the
        // toggle rows are the durable fact.
        var isBaseRow = string.Equals(
            documentTypeKey, AcceptanceRulesService.BaseRowKeyLiteral, StringComparison.Ordinal);
        int? oldDial = null;
        if (isBaseRow)
        {
            try
            {
                var current = modeProvider.Mode == TammaMode.SaaS
                        && tenantContext.TenantId is Guid tid0
                    ? await store.ResolveBaseForTenantAsync(tid0)
                    : await store.ResolveBaseAsync(userId);
                oldDial = current.Rules.AutonomyLevel;
            }
            catch
            {
                // No readable old dial → skip the survival record (best-effort).
            }
        }

        // Story 43-0 — a field the caller did not send is PRESERVED, never invented.
        // `acceptorRequirement` is the only optional member of the body; resolve
        // what is in force right now (override row, else the shipped per-type
        // default) and hand it to ToRules as the fallback. Before this, an omitting
        // body bound `any` and every admin save wiped `design`/`sprint-plan`/
        // `threat-model`'s human acceptor floor.
        AcceptorRequirement currentAcceptorRequirement;
        try
        {
            currentAcceptorRequirement = await ResolveCurrentAcceptorRequirementAsync(
                documentTypeKey, store, principal, tenantContext, modeProvider);
        }
        catch (TammaError te)
        {
            // Unknown document type key, or a stored row whose body fails
            // AcceptanceRules.Validate() (ACCEPTANCE_RULES.INVALID) — the same
            // 400 the write path raises.
            return Results.BadRequest(new { error = te.Message, code = te.Code });
        }
        catch (System.Text.Json.JsonException jx)
        {
            // Review MODERATE-2 (2026-07-29). Before 43-0, Upsert never READ, so
            // overwriting a corrupt row was the repair. Now it reads first, and
            // Materialize → AcceptanceRulesJson.Deserialize throws JsonException
            // on malformed stored JSON — which the TammaError catch above does
            // NOT cover, so this commit turned one corrupt row into a 500 on a
            // shipped admin surface. Worse: because per-type resolution FALLS
            // THROUGH to the base row, a single corrupt BASE row broke PUT for
            // EVERY document type.
            //
            // A stored row we cannot parse is not a server fault the caller can
            // do nothing about — it is a repairable state, and the response says
            // exactly how to repair it.
            return Results.BadRequest(StoredRowUnreadable(documentTypeKey, jx));
        }

        AcceptanceRules rules;
        try
        {
            rules = req.ToRules(currentAcceptorRequirement);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message, code = "ACCEPTANCE_RULES.INVALID" });
        }

        try
        {
            if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
                await store.UpsertForTenantAsync(tenantId, userId, documentTypeKey, rules);
            else
                await store.UpsertAsync(userId, documentTypeKey, rules);
        }
        catch (TammaError te)
        {
            // Out-of-range knob (ACCEPTANCE_RULES.INVALID) or unknown type key
            // (DOCUMENT.TYPE.UNKNOWN) — both are caller errors → 400.
            return Results.BadRequest(new { error = te.Message, code = te.Code });
        }

        // Story 43-15 (D7) — enumerate surviving toggles on a base-row dial DROP.
        if (isBaseRow && oldDial is int from && rules.AutonomyLevel < from
            && actionRepo is not null && actionEvents is not null)
        {
            await EmitTogglesSurvivedDialLowerAsync(
                from, rules.AutonomyLevel, userId, tenantContext, modeProvider,
                rules, actionRepo!, actionEvents!);
        }

        // Return the freshly resolved rules (with provenance) for the type.
        return await GetResolved(documentTypeKey, store, principal, tenantContext, modeProvider);
    }

    /// <summary>
    /// Story 43-15 (D7) — SERVER-authored survival record. Enumerates the
    /// principal's per-action toggles (action rows at <see cref="AutonomyDial.Min"/>)
    /// whose ladder-WITHOUT-the-row resolution exceeds the new dial, and emits ONE
    /// <c>ACTION.GATE.TOGGLES_SURVIVED_DIAL_LOWER</c> naming them. Best-effort — it
    /// never fails the dial write; the toggle rows are the durable fact, and
    /// "declining the bulk revoke" is structural (this event, then no deletions).
    /// </summary>
    private static async Task EmitTogglesSurvivedDialLowerAsync(
        int fromDial, int toDial, Guid? callerUserId,
        ITenantContext tenantContext, ITammaModeProvider modeProvider,
        AcceptanceRules newRules,
        Tamma.Data.Repositories.IActionAssignmentRepository actionRepo,
        Tamma.Api.Services.Actions.ActionGateEventsService actionEvents)
    {
        try
        {
            var (tid, uid) = modeProvider.Mode == TammaMode.SaaS
                    && tenantContext.TenantId is Guid t
                ? ((Guid?)t, (Guid?)null)
                : (null, callerUserId);

            var principalRows = await actionRepo.ListForPrincipalAsync(tid, uid);
            var platformRows = await actionRepo.ListPlatformAsync();
            var snapshot = Tamma.Core.Actions.GovernancePolicySnapshot.FromSuccessfulRead(
                RowsByKind(platformRows, "action"), RowsByKind(platformRows, "group"),
                RowsByKind(principalRows, "action"), RowsByKind(principalRows, "group"));
            var baseRules = new ResolvedAcceptanceRules(
                newRules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);

            var surviving = new List<string>();
            foreach (var row in principalRows)
            {
                if (!string.Equals(row.TargetKind, "action", StringComparison.Ordinal)
                    || row.MinAutonomy != Tamma.Core.Documents.Policy.AutonomyDial.Min)
                {
                    continue;
                }
                if (!Tamma.Core.Actions.ActionKey.TryParse(row.TargetKey, out var k)
                    || !Tamma.Core.Actions.ActionCatalog.TryGet(k, out var d) || d is null
                    || d.IsMachinery)
                {
                    continue;
                }
                var withoutRow = Tamma.Core.Actions.AutonomyGateEvaluator
                    .ResolveLadderWithoutActionRow(d, snapshot, baseRules);
                if (withoutRow.EffectiveMinAutonomy > toDial)
                {
                    surviving.Add(row.TargetKey);
                }
            }

            if (surviving.Count > 0)
            {
                await actionEvents.EmitTogglesSurvivedDialLowerAsync(
                    tid, uid, callerUserId, fromDial, toDial, surviving);
            }
        }
        catch
        {
            // Best-effort audit — never break the dial write over it.
        }
    }

    private static IReadOnlyDictionary<string, Tamma.Core.Actions.ActionAssignmentValue>
        RowsByKind(
            IReadOnlyList<Tamma.Data.Entities.ActionAssignment> rows, string kind)
        => rows
            .Where(r => string.Equals(r.TargetKind, kind, StringComparison.Ordinal))
            .ToDictionary(
                r => r.TargetKey,
                r => new Tamma.Core.Actions.ActionAssignmentValue(
                    r.MinAutonomy, r.Enforce, r.Enabled, r.AllowedRoles),
                StringComparer.Ordinal);

    // =======================================================================
    // Delete → reset to next tier (AcceptanceRulesManage)
    // =======================================================================

    public static async Task<IResult> Delete(
        string documentTypeKey,
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = principal.GetUserId();
        bool deleted;
        try
        {
            if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)
                deleted = await store.DeleteForTenantAsync(tenantId, documentTypeKey);
            else
                deleted = await store.DeleteAsync(userId, documentTypeKey);
        }
        catch (TammaError te)
        {
            return Results.BadRequest(new { error = te.Message, code = te.Code });
        }

        if (!deleted)
            return Results.NotFound(new { error = "Acceptance-rules override not found" });

        return Results.Ok(new { message = "Acceptance-rules override deleted" });
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    /// <summary>
    /// The <see cref="AcceptorRequirement"/> in force for <paramref name="documentTypeKey"/>
    /// right now — the resolved override row if one exists, else the shipped per-type
    /// default (<c>design</c>, <c>sprint-plan</c> and <c>threat-model</c> ship
    /// <see cref="AcceptorRequirement.Human"/>). Story 43-0: this is the value an
    /// upsert body that OMITS <c>acceptorRequirement</c> carries forward.
    /// </summary>
    /// <exception cref="TammaError">
    /// <c>DOCUMENT.TYPE.UNKNOWN</c> when the key is neither <c>base</c> nor a known
    /// document type — surfaced by the caller as the same 400 the write path raises.
    /// </exception>
    private static async Task<AcceptorRequirement> ResolveCurrentAcceptorRequirementAsync(
        string documentTypeKey,
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var saas = modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid;

        if (string.Equals(documentTypeKey, AcceptanceRulesService.BaseRowKeyLiteral, StringComparison.Ordinal))
        {
            var baseResolved = saas
                ? await store.ResolveBaseForTenantAsync((Guid)tenantContext.TenantId!)
                : await store.ResolveBaseAsync(principal.GetUserId());
            return baseResolved.Rules.AcceptorRequirement;
        }

        // Throws TammaError DOCUMENT.TYPE.UNKNOWN on a typo'd key (the fail-loud
        // parse the service would apply on write anyway).
        var type = Tamma.Core.Documents.DocumentTypeKeyExtensions.Parse(documentTypeKey);
        var resolved = saas
            ? await store.ResolveForTenantAsync((Guid)tenantContext.TenantId!, type)
            : await store.ResolveAsync(principal.GetUserId(), type);
        return resolved.Rules.AcceptorRequirement;
    }

    /// <summary>
    /// The typed 400 for a stored override row whose JSON cannot be parsed
    /// (review MODERATE-2, 2026-07-29). It names the DELETE-then-PUT repair, and
    /// it names the fall-through explicitly: because per-type resolution walks
    /// type row → base row → shipped default, the unreadable row may be the BASE
    /// row even though the caller addressed a document type — which is why one
    /// corrupt base row could break the write path for all of them.
    /// </summary>
    private static object StoredRowUnreadable(string documentTypeKey, System.Text.Json.JsonException jx) => new
    {
        error = $"The acceptance-rules row currently in force for '{documentTypeKey}' is stored as "
            + "malformed JSON and cannot be read. Per-type resolution falls through to the base row, "
            + $"so the unreadable row may be '{AcceptanceRulesService.BaseRowKeyLiteral}' rather than "
            + $"'{documentTypeKey}'. REPAIR: DELETE the offending override "
            + $"(DELETE /api/acceptance-rules/{documentTypeKey}, or "
            + $"DELETE /api/acceptance-rules/{AcceptanceRulesService.BaseRowKeyLiteral}) — DELETE never "
            + "reads the body — to fall back to the next tier, then PUT the rules you want. A PUT alone "
            + "cannot repair it: since Story 43-0 the write READS the in-force value in order to "
            + "preserve it.",
        code = "ACCEPTANCE_RULES.STORED_ROW_UNREADABLE",
        detail = jx.Message,
    };

    private static IReadOnlyList<ResolvedAcceptanceRules> DefaultsForAllTypes()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<ResolvedAcceptanceRules>();
        foreach (var type in Enum.GetValues<Tamma.Core.Documents.DocumentTypeKey>())
            list.Add(new ResolvedAcceptanceRules(
                AcceptanceDefaults.For(type),
                AcceptanceRulesSource.SystemDefault,
                1,
                type.ToWire(),
                now));
        return list;
    }
}
