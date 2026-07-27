using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Core.Audit;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 32-3 (AC7) — tenant-admin BYOK provider-credential management API.
///
/// <list type="bullet">
///   <item><c>GET    /api/v1/agents/providers</c> — list configured BYOK
///     providers (metadata only, NEVER the key).</item>
///   <item><c>POST   /api/v1/agents/providers/{provider}/credential</c> —
///     register a BYOK key (reveal-once token via Story 29-3).</item>
///   <item><c>POST   /api/v1/agents/providers/{provider}/credential/rotate</c>
///     — rotate the BYOK key (reveal-once).</item>
///   <item><c>DELETE /api/v1/agents/providers/{provider}/credential</c> —
///     retire the active BYOK version (falls back to platform on next resolve).</item>
/// </list>
///
/// <para>RBAC: writes are gated to <c>tenant_owner</c> / <c>tenant_admin</c> by
/// the <c>AgentManage</c> route policy (member → 403). Cross-tenant / absent
/// targets return 404 (never leak existence). Every mutation calls
/// <c>resolver.Invalidate</c> so the next call re-resolves. Response bodies
/// NEVER contain the raw key — create/rotate return the reveal-once token
/// metadata only.</para>
///
/// <para>BYOK is tenant-scoped only (no per-user layer, mirroring the Prompt
/// Store). In single-user mode the sole user owns their personal tenant, so
/// the tenant-context guard still resolves to "their" scope.</para>
/// </summary>
public static class ProviderCredentialEndpoints
{
    private const int MinKeyLength = 8;
    private const int MaxKeyLength = 8192;

    /// <summary>
    /// <c>GET /api/v1/agents/providers</c> — list the tenant's configured BYOK
    /// providers. Metadata only (provider, version, last-rotated) — NO key.
    /// </summary>
    public static async Task<IResult> ListProviders(
        ITenantContext tenantContext,
        [FromServices] ISecretQueryService query,
        HttpContext http)
    {
        var tenantId = tenantContext.TenantId;
        if (tenantId is null)
        {
            return Results.Ok(new { providers = Array.Empty<object>() });
        }

        var rows = await query.ListAsync(SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);

        var providers = rows
            .Select(r => new { name = ProviderCabinetNames.TryParse(r.Name), metadata = r })
            .Where(x => x.name is not null)
            .Select(x => new ProviderCredentialMetadata(
                x.name!,
                x.metadata.ActiveVersionNumber,
                x.metadata.LastRotatedAt,
                x.metadata.UpdatedAt))
            .ToList();

        return Results.Ok(new { providers });
    }

    /// <summary>
    /// <c>POST /api/v1/agents/providers/{provider}/credential</c> — register a
    /// BYOK key for the tenant. Reveal-once: the response carries the token +
    /// metadata, NEVER the key.
    /// </summary>
    public static async Task<IResult> RegisterCredential(
        string provider,
        SetProviderCredentialRequest body,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] ISecretRevealService reveal,
        [FromServices] IProviderCredentialResolver resolver,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;
        var keyError = ValidateKey(body?.ApiKey);
        if (keyError is not null) return keyError;

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "registering a BYOK key requires tenant context.",
            });
        }

        try
        {
            var result = await reveal.IssueCreateAsync(
                name: ProviderCabinetNames.Byok(norm!),
                scope: SecretScope.Tenant,
                tenantId: tid,
                purpose: SecretPurpose.ApiKey,
                initialPlaintext: body!.ApiKey!,
                consumerRefs: new[] { new ConsumerRef(norm!, "api-key") },
                ownerUserId: principal.GetUserId() ?? Guid.Empty,
                rotationSchedule: null,
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            resolver.Invalidate(tid, norm!);

            // Story 37-10 — curated BYOK audit event (the SECRET.WRITE cabinet
            // event stays the secret source of truth; this is derived alongside it,
            // NOT a second write). Metadata only — the key never travels here.
            await EmitByokChangeAsync(
                http, tid, principal.GetUserId(), norm!, "set",
                result.Metadata.ActiveVersionNumber).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/agents/providers/{norm}/credential",
                new SetProviderCredentialResponse(
                    norm!, result.Metadata.ActiveVersionNumber,
                    result.RevealToken, result.ExpiresAt));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (IsDuplicate(ex))
        {
            return Results.Conflict(new
            {
                error = "credential_exists",
                detail = "a BYOK key for this provider already exists; use rotate.",
            });
        }
    }

    /// <summary>
    /// <c>POST /api/v1/agents/providers/{provider}/credential/rotate</c> —
    /// rotate the BYOK key to a new version. Reveal-once response; never the key.
    /// </summary>
    public static async Task<IResult> RotateCredential(
        string provider,
        SetProviderCredentialRequest body,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] ISecretQueryService query,
        [FromServices] ISecretRevealService reveal,
        [FromServices] IProviderCredentialResolver resolver,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;
        var keyError = ValidateKey(body?.ApiKey);
        if (keyError is not null) return keyError;

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.NotFound();
        }

        var secretId = await FindSecretIdAsync(query, tid, norm!, http.RequestAborted)
            .ConfigureAwait(false);
        if (secretId is null)
        {
            // No existing key for this tenant/provider — nothing to rotate.
            // 404 (never leak another tenant's existence).
            return Results.NotFound(new
            {
                error = "credential_not_found",
                detail = "no BYOK key for this provider; register one first.",
            });
        }

        try
        {
            var result = await reveal.IssueRotateAsync(
                secretId.Value, body!.ApiKey!, principal.GetUserId() ?? Guid.Empty,
                http.RequestAborted).ConfigureAwait(false);

            resolver.Invalidate(tid, norm!);

            // Story 37-10 — curated BYOK rotate audit event (see RegisterCredential).
            await EmitByokChangeAsync(
                http, tid, principal.GetUserId(), norm!, "rotated",
                result.Metadata.ActiveVersionNumber).ConfigureAwait(false);

            return Results.Ok(new SetProviderCredentialResponse(
                norm!, result.Metadata.ActiveVersionNumber,
                result.RevealToken, result.ExpiresAt));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// <c>DELETE /api/v1/agents/providers/{provider}/credential</c> — retire the
    /// active BYOK version so the next resolve falls back to the platform key.
    /// </summary>
    public static async Task<IResult> DeleteCredential(
        string provider,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] ISecretQueryService query,
        [FromServices] IProviderCredentialResolver resolver,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.NotFound();
        }

        var meta = await FindMetadataAsync(query, tid, norm!, http.RequestAborted)
            .ConfigureAwait(false);
        if (meta is null || meta.ActiveVersionNumber <= 0)
        {
            return Results.NotFound(new
            {
                error = "credential_not_found",
                detail = "no active BYOK key for this provider.",
            });
        }

        var retiredVersion = meta.ActiveVersionNumber;
        try
        {
            await query.RetireVersionAsync(
                meta.Id, meta.ActiveVersionNumber, SecretScope.Tenant, tid,
                principal.GetUserId() ?? Guid.Empty, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // Refused to retire the active version without a successor — but for
            // BYOK "remove" we WANT to drop the active key. Surface a clear 409.
            return Results.Conflict(new { error = "retire_blocked", detail = ex.Message });
        }

        resolver.Invalidate(tid, norm!);

        // Story 37-10 — curated BYOK remove audit event (see RegisterCredential).
        await EmitByokChangeAsync(
            http, tid, principal.GetUserId(), norm!, "removed",
            retiredVersion).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Epic 46 — tenant-facing model listing + model settings, registered
    // beside the BYOK routes on the /api/v1/agents/providers surface.
    // Reads serve any tenant member (single-user: the sole user); writes are
    // AgentManage-gated at the route map (member → 403) — the same trust
    // level as BYOK key custody (epic D3).
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Story 46-0 (AC6) — <c>GET /api/v1/agents/providers/{provider}/models</c>:
    /// the provider's LIVE model list for THIS tenant. The fetch runs
    /// server-side with the tenant's BYOK key when one exists, else the
    /// platform key (epic D5 — the credential resolver's existing order); the
    /// key never reaches the browser. Fail-soft per epic D6: always HTTP 200
    /// for a known provider, and the currently-effective model is always an
    /// entry flagged <c>current</c>. SaaS callers without tenant context get
    /// the empty-envelope behaviour (consistent with <see cref="ListProviders"/>).
    /// </summary>
    public static async Task<IResult> GetTenantProviderModels(
        string provider,
        ITenantContext tenantContext,
        [FromServices] ITammaModeProvider mode,
        [FromServices] IProviderModelCatalog modelCatalog,
        [FromServices] IInlineToolLoopRunner runner,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;

        var tenantId = tenantContext.TenantId;
        if (mode.Mode == TammaMode.SaaS && tenantId is null)
        {
            // Consistent with ListProviders' no-tenant-context behaviour —
            // no provider data is fetched for an unresolved SaaS caller.
            return Results.Ok(new ProviderModelsResponse(
                norm!, Array.Empty<ProviderModelEntry>(),
                FetchedAt: null, Stale: false, ErrorCode: "no_tenant_context"));
        }

        var list = await modelCatalog.ListModelsAsync(norm!, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        var currentModel = runner.GetDefaultModel(norm!, tenantId);

        return Results.Ok(ProviderAdminEndpoints.BuildModelsResponse(norm!, list, currentModel));
    }

    /// <summary>
    /// Story 46-1 (AC5) — <c>GET /api/v1/agents/providers/models</c>: the
    /// tenant-facing provider roster 46-3 renders. One row per ENABLED HTTP
    /// provider (disabled providers are simply absent — tenants never see the
    /// platform's off switch): key, display name, models-listing support, the
    /// resolved model + provenance for THIS tenant, whether an override row
    /// exists, and BYOK key PRESENCE (metadata only — reuses the
    /// <see cref="ListProviders"/> cabinet query; never the key).
    /// </summary>
    public static async Task<IResult> GetTenantProviderRoster(
        ITenantContext tenantContext,
        [FromServices] ITammaModeProvider mode,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner,
        [FromServices] ISecretQueryService query,
        HttpContext http)
    {
        var tenantId = tenantContext.TenantId;

        // BYOK presence via the same cabinet listing ListProviders uses.
        var byokProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tenantId is Guid tid)
        {
            var rows = await query.ListAsync(SecretScope.Tenant, tid, http.RequestAborted)
                .ConfigureAwait(false);
            foreach (var r in rows)
            {
                var name = ProviderCabinetNames.TryParse(r.Name);
                if (name is not null) byokProviders.Add(name);
            }
        }

        var roster = new List<TenantProviderRosterRow>();
        foreach (var d in ProviderCatalog.HttpProviders)
        {
            if (!settings.IsEnabled(d.Key))
            {
                continue; // platform-disabled → absent from the tenant view
            }
            var resolution = runner.ResolveDefaultModelWithSource(d.Key, tenantId);
            roster.Add(new TenantProviderRosterRow(
                Provider: d.Key,
                DisplayName: d.DisplayName,
                ModelsSupported: d.ModelsEndpointPath is not null,
                Model: string.IsNullOrEmpty(resolution.Model) ? null : resolution.Model,
                Source: resolution.Source,
                HasOverride: settings.HasOverride(d.Key, tenantId),
                ByokKeyPresent: byokProviders.Contains(d.Key)));
        }

        return Results.Ok(new { providers = roster });
    }

    /// <summary>
    /// Story 46-1 (AC5) — <c>GET /api/v1/agents/providers/{provider}/model</c>:
    /// the resolved model for this tenant + provenance
    /// (<c>tenant-override | platform-db | config | descriptor</c>) + the raw
    /// override value when one exists. Member-readable.
    /// </summary>
    public static IResult GetTenantProviderModel(
        string provider,
        ITenantContext tenantContext,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;

        var tenantId = tenantContext.TenantId;
        var resolution = runner.ResolveDefaultModelWithSource(norm!, tenantId);
        return Results.Ok(new TenantProviderModelResponse(
            norm!,
            string.IsNullOrEmpty(resolution.Model) ? null : resolution.Model,
            resolution.Source,
            settings.TryGetModel(norm!, tenantId)));
    }

    /// <summary>
    /// Story 46-1 (AC5) — <c>PUT /api/v1/agents/providers/{provider}/model</c>:
    /// upsert this tenant's model override (SaaS: tenant-keyed, written by
    /// tenant_owner/tenant_admin via the AgentManage route policy; single-user:
    /// the sole user's user-keyed row). Response carries the epic-D3b
    /// pricing-known warning. Writes against a platform-disabled provider are
    /// rejected (409) — the off switch wins.
    /// </summary>
    public static async Task<IResult> PutTenantProviderModel(
        string provider,
        PutTenantProviderModelRequest body,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] ITammaModeProvider mode,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner,
        [FromServices] IProviderPricingService pricing,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;

        var modelError = ProviderAdminEndpoints.ValidateModel(body?.Model);
        if (modelError is not null) return modelError;
        var model = body!.Model!.Trim();

        var (tenantId, userId, principalError) = ResolveSettingsPrincipal(
            mode, tenantContext, principal);
        if (principalError is not null) return principalError;

        if (!settings.IsEnabled(norm!))
        {
            return Results.Conflict(new
            {
                error = "provider_disabled",
                detail = "this provider is disabled by the platform.",
            });
        }

        var previous = runner.ResolveDefaultModelWithSource(norm!, tenantId);
        await settings.SetPrincipalModelAsync(
            norm!, tenantId, userId, model, principal.GetUserId(), http.RequestAborted)
            .ConfigureAwait(false);

        await ProviderAdminEndpoints.EmitSettingsChangeAsync(
            http, tenantId, principal.GetUserId(), norm!,
            scope: tenantId is null ? "user" : "tenant",
            operation: "set", previousModel: previous.Model, model: model, enabled: null)
            .ConfigureAwait(false);

        var (pricingKnown, warning) =
            ProviderAdminEndpoints.PricingWarning(pricing, norm!, model);
        return Results.Ok(new PutTenantProviderModelResponse(
            norm!, model, "tenant-override", pricingKnown, warning));
    }

    /// <summary>
    /// Story 46-1 (AC5) — <c>DELETE /api/v1/agents/providers/{provider}/model</c>:
    /// remove this tenant's override → resolution falls back to
    /// platform-db/config/descriptor. 404 when no override row exists.
    /// </summary>
    public static async Task<IResult> DeleteTenantProviderModel(
        string provider,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] ITammaModeProvider mode,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(provider);
        if (err is not null) return err;

        var (tenantId, userId, principalError) = ResolveSettingsPrincipal(
            mode, tenantContext, principal);
        if (principalError is not null) return principalError;

        var previous = runner.ResolveDefaultModelWithSource(norm!, tenantId);
        var removed = await settings.RemovePrincipalModelAsync(
            norm!, tenantId, userId, http.RequestAborted).ConfigureAwait(false);
        if (!removed)
        {
            return Results.NotFound(new
            {
                error = "override_not_found",
                detail = "no model override for this provider.",
            });
        }

        await ProviderAdminEndpoints.EmitSettingsChangeAsync(
            http, tenantId, principal.GetUserId(), norm!,
            scope: tenantId is null ? "user" : "tenant",
            operation: "removed", previousModel: previous.Model, model: null, enabled: null)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// Story 46-1 scoping (CLAUDE.md universal rule, answered per mode): SaaS
    /// rows are TENANT-keyed (no tenant context → 404, mirroring the BYOK
    /// mutations); single-user rows are USER-keyed (the sole user — resolved
    /// from the JWT; no user id → 400). Exactly one of the two ids is non-null
    /// on success.
    /// </summary>
    private static (Guid? TenantId, Guid? UserId, IResult? Error) ResolveSettingsPrincipal(
        ITammaModeProvider mode, ITenantContext tenantContext, ClaimsPrincipal principal)
    {
        if (mode.Mode == TammaMode.SingleUser)
        {
            var userId = principal.GetUserId();
            if (userId is null)
            {
                return (null, null, Results.BadRequest(new
                {
                    error = "no_user_context",
                    detail = "a model override requires an authenticated user.",
                }));
            }
            return (null, userId, null);
        }

        if (tenantContext.TenantId is not Guid tid)
        {
            return (null, null, Results.NotFound());
        }
        return (tid, null, null);
    }

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Story 37-10 — emit the curated <c>PROVIDER_KEY.CHANGED.SUCCESS</c> BYOK
    /// audit event (tenant-scoped) after a successful cabinet write. Metadata
    /// only (provider, operation, mode, version) — the emitter additionally
    /// scrubs any secret-shaped value defensively. The emitter is resolved
    /// best-effort off the request scope (keeps the handler signature stable for
    /// the direct-call tests); a missing registration simply skips the emission.
    /// Never throws.
    /// </summary>
    private static async Task EmitByokChangeAsync(
        HttpContext http,
        Guid tenantId,
        Guid? actorUserId,
        string provider,
        string operation,
        int? version)
    {
        ISensitiveActionEmitter? emitter;
        try { emitter = http.RequestServices?.GetService<ISensitiveActionEmitter>(); }
        catch { emitter = null; }
        if (emitter is null) return;

        var tags = new Dictionary<string, string?>
        {
            ["provider"] = provider,
            ["operation"] = operation,
            ["mode"] = "byok",
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["operation"] = operation,
            ["mode"] = "byok",
        };
        if (version is int v) data["version"] = v;

        await emitter.EmitAsync(
            SensitiveAction.ForTenant(
                SensitiveActionCatalog.ProviderKeyChanged, tenantId, actorUserId, tags, data),
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<Guid?> FindSecretIdAsync(
        ISecretQueryService query, Guid tenantId, string provider, CancellationToken ct)
    {
        var meta = await FindMetadataAsync(query, tenantId, provider, ct).ConfigureAwait(false);
        return meta?.Id;
    }

    private static async Task<SecretMetadata?> FindMetadataAsync(
        ISecretQueryService query, Guid tenantId, string provider, CancellationToken ct)
    {
        var name = ProviderCabinetNames.Byok(provider);
        var rows = await query.ListAsync(SecretScope.Tenant, tenantId, ct).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.Name == name);
    }

    private static (string? Provider, IResult? Error) NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return (null, Results.BadRequest(new { error = "invalid_provider" }));
        }
        // F5 — normalize catalogue aliases ("kimi" → moonshot, "z.ai"/"zai" →
        // z-ai) to the canonical key BEFORE the allowlist check; aliases are
        // lookup spellings, deliberately not allowlist entries.
        var spelled = provider.Trim().ToLowerInvariant();
        var norm = ProviderCatalog.Resolve(spelled)?.Key
            ?? ProviderCatalog.ResolveNonHttp(spelled)?.Key
            ?? spelled;
        if (!ProviderAllowlist.IsAllowedDefault(norm))
        {
            // Unknown provider — 404 (do not enumerate the allowlist).
            return (null, Results.NotFound(new { error = "unknown_provider" }));
        }
        return (norm, null);
    }

    private static IResult? ValidateKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Results.BadRequest(new { error = "invalid_key", detail = "apiKey is required." });
        }
        if (apiKey.Length is < MinKeyLength or > MaxKeyLength)
        {
            return Results.BadRequest(new
            {
                error = "invalid_key",
                detail = $"apiKey must be between {MinKeyLength} and {MaxKeyLength} chars.",
            });
        }
        return null;
    }

    private static bool IsDuplicate(Exception ex) =>
        ex is InvalidOperationException
        || (ex.InnerException is not null && ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == "23505")
        || ex is Microsoft.EntityFrameworkCore.DbUpdateException due
            && due.InnerException is Npgsql.PostgresException ipg && ipg.SqlState == "23505";
}

/// <summary>Request body for register / rotate. Key is write-only; never echoed.</summary>
public sealed record SetProviderCredentialRequest(string? ApiKey);

/// <summary>
/// Reveal-once response for register / rotate. Carries the token + metadata —
/// NEVER the raw key (Story 32-3 AC5/AC7).
/// </summary>
public sealed record SetProviderCredentialResponse(
    string Provider, int Version, string RevealToken, DateTimeOffset ExpiresAt);

/// <summary>Metadata-only list item (NO key).</summary>
public sealed record ProviderCredentialMetadata(
    string Provider, int Version, DateTimeOffset? LastRotatedAt, DateTimeOffset UpdatedAt);

/// <summary>Story 46-1 — one row of the tenant-facing provider roster
/// (<c>GET /api/v1/agents/providers/models</c>). Metadata only:
/// <see cref="ByokKeyPresent"/> is presence, NEVER the key.</summary>
public sealed record TenantProviderRosterRow(
    string Provider,
    string DisplayName,
    bool ModelsSupported,
    string? Model,
    string Source,
    bool HasOverride,
    bool ByokKeyPresent);

/// <summary>Story 46-1 — resolved model + provenance + the raw override
/// (<c>GET /api/v1/agents/providers/{provider}/model</c>).</summary>
public sealed record TenantProviderModelResponse(
    string Provider, string? Model, string Source, string? Override);

/// <summary>Story 46-1 — PUT body for the tenant model override.</summary>
public sealed record PutTenantProviderModelRequest(string? Model);

/// <summary>Story 46-1 — PUT response, carrying the epic-D3b pricing warning.</summary>
public sealed record PutTenantProviderModelResponse(
    string Provider, string Model, string Source, bool PricingKnown, string? Warning);
