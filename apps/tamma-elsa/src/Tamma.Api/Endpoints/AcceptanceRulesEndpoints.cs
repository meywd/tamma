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

    // =======================================================================
    // Upsert (AcceptanceRulesManage)
    // =======================================================================

    public static async Task<IResult> Upsert(
        string documentTypeKey,
        AcceptanceRulesUpsertRequest req,
        AcceptanceRulesService store,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        var userId = principal.GetUserId();

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
            // Unknown document type key — the same 400 the write path raises.
            return Results.BadRequest(new { error = te.Message, code = te.Code });
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

        // Return the freshly resolved rules (with provenance) for the type.
        return await GetResolved(documentTypeKey, store, principal, tenantContext, modeProvider);
    }

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
