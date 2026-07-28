using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Core.Audit;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Epic 46 — the platform-owner provider admin surface
/// (<c>PlatformOwnerAccess</c>-gated group in <c>Program.cs</c>):
///
/// <list type="bullet">
///   <item><c>GET    /api/admin/providers/status</c> — one row per catalogue
///     provider: configuration status, key status, models-listing support,
///     current effective model + provenance (Story 46-0 AC4 + 46-1 AC5).
///     NOTE: the story specified <c>GET /api/admin/providers</c>, but that
///     route is already owned by the Story 34-11 provider COST price-book
///     roster (<c>AdminProviderPricingEndpoints.ListProviders</c>) — mapping
///     both would be an ambiguous match, so the status roster lives under
///     <c>/status</c>.</item>
///   <item><c>GET    /api/admin/providers/{key}/models</c> — the provider's
///     LIVE model list, fetched server-side with the PLATFORM key, fail-soft
///     (Story 46-0 AC5, epic D6).</item>
///   <item><c>PUT    /api/admin/providers/{key}/settings</c> — set the
///     platform default model and/or the enabled flag (Story 46-1 AC5).</item>
///   <item><c>DELETE /api/admin/providers/{key}/settings</c> — remove the
///     platform row → resolution falls back to config/descriptor.</item>
/// </list>
///
/// <para><b>Credential safety (46-0 AC7):</b> no response DTO in this file
/// carries key material — <c>keyStatus</c> is a three-way classification
/// answered by the credential resolver (the resolved credential is discarded
/// immediately). Note the status roster therefore emits the resolver's own
/// <c>AGENT.CREDENTIAL_RESOLVED.SUCCESS</c> / <c>AGENT.CREDENTIAL.DENIED</c>
/// events once per provider row per render — accepted; that is the audit
/// trail working as designed (46-0 plan, Events).</para>
/// </summary>
public static class ProviderAdminEndpoints
{
    private const int MaxModelLength = 256;

    /// <summary>
    /// <c>GET /api/admin/providers/status</c> — the provider status roster.
    /// Static descriptor data + key status + resolved model only — this
    /// endpoint deliberately does NOT fetch model lists (15 sequential
    /// fetches would make the page crawl; the UI fetches models per-provider
    /// when a picker opens).
    /// </summary>
    public static async Task<IResult> ListProviderStatus(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IProviderCredentialResolver credentials,
        [FromServices] IInlineToolLoopRunner runner,
        [FromServices] IProviderSettingsStore settings,
        HttpContext http)
    {
        var rows = new List<ProviderStatusRow>();

        foreach (var d in ProviderCatalog.HttpProviders)
        {
            var baseUrl = httpClientFactory.CreateClient(d.HttpClientName).BaseAddress?.ToString()
                ?? (string.IsNullOrWhiteSpace(d.DefaultBaseUrl) ? null : d.DefaultBaseUrl);
            var resolution = runner.ResolveDefaultModelWithSource(d.Key, tenantId: null);

            rows.Add(new ProviderStatusRow(
                Key: d.Key,
                DisplayName: d.DisplayName,
                Transport: "http",
                Dialect: d.Dialect.ToString(),
                EffectiveBaseUrl: baseUrl?.TrimEnd('/'),
                KeyStatus: await ResolveKeyStatusAsync(credentials, d.Key, http.RequestAborted)
                    .ConfigureAwait(false),
                ModelsSupported: d.ModelsEndpointPath is not null,
                CurrentModel: string.IsNullOrEmpty(resolution.Model) ? null : resolution.Model,
                Source: resolution.Source,
                Enabled: settings.IsEnabled(d.Key),
                Aliases: d.Aliases));
        }

        foreach (var n in ProviderCatalog.NonHttpProviders.Where(n => n.Allowlisted))
        {
            var resolution = runner.ResolveDefaultModelWithSource(n.Key, tenantId: null);
            rows.Add(new ProviderStatusRow(
                Key: n.Key,
                DisplayName: n.DisplayName,
                Transport: n.Transport == NonHttpProviderTransport.Cli ? "cli" : "mcp",
                Dialect: null,
                EffectiveBaseUrl: null,
                KeyStatus: await ResolveKeyStatusAsync(credentials, n.Key, http.RequestAborted)
                    .ConfigureAwait(false),
                ModelsSupported: false,
                CurrentModel: string.IsNullOrEmpty(resolution.Model) ? null : resolution.Model,
                Source: resolution.Source,
                Enabled: settings.IsEnabled(n.Key),
                Aliases: n.Aliases));
        }

        return Results.Ok(new { providers = rows });
    }

    /// <summary>
    /// <c>GET /api/admin/providers/{key}/models</c> — the live model list for
    /// a provider, platform-key scope (<c>tenantId: null</c>). Always HTTP 200
    /// for a known provider (fail-soft, epic D6): fresh list, stale-flagged
    /// cached list, or empty list + error code — and the currently-effective
    /// model is ALWAYS present, flagged <c>current</c> (synthesized if the
    /// provider delisted it). Unknown key → 404, never enumerating.
    /// </summary>
    public static async Task<IResult> GetProviderModels(
        string key,
        [FromServices] IProviderModelCatalog modelCatalog,
        [FromServices] IInlineToolLoopRunner runner,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(key);
        if (err is not null) return err;

        var list = await modelCatalog.ListModelsAsync(norm!, tenantId: null, http.RequestAborted)
            .ConfigureAwait(false);
        var currentModel = runner.GetDefaultModel(norm!, tenantId: null);

        return Results.Ok(BuildModelsResponse(norm!, list, currentModel));
    }

    /// <summary>
    /// <c>PUT /api/admin/providers/{key}/settings</c> — upsert the platform
    /// default model and/or the enabled flag. Response carries the
    /// pricing-known warning (epic D3b: warn, never block — open question 1).
    /// </summary>
    public static async Task<IResult> PutProviderSettings(
        string key,
        PutProviderSettingsRequest body,
        ClaimsPrincipal principal,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner,
        [FromServices] IProviderPricingService pricing,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(key);
        if (err is not null) return err;

        if (body is null || (body.DefaultModel is null && body.Enabled is null))
        {
            return Results.BadRequest(new
            {
                error = "invalid_request",
                detail = "provide defaultModel and/or enabled.",
            });
        }

        if (body.DefaultModel is not null)
        {
            var modelError = ValidateModel(body.DefaultModel);
            if (modelError is not null) return modelError;
        }

        var previous = runner.ResolveDefaultModelWithSource(norm!, tenantId: null);
        var actor = principal.GetUserId();

        if (body.DefaultModel is not null)
        {
            await settings.SetPlatformModelAsync(
                norm!, body.DefaultModel.Trim(), actor, http.RequestAborted).ConfigureAwait(false);
        }
        if (body.Enabled is bool enabled)
        {
            await settings.SetEnabledAsync(norm!, enabled, actor, http.RequestAborted)
                .ConfigureAwait(false);
        }

        await EmitSettingsChangeAsync(
            http, tenantId: null, actor, norm!, scope: "platform",
            operation: body.Enabled is bool e2 && body.DefaultModel is null
                ? (e2 ? "enabled" : "disabled")
                : "set",
            previousModel: previous.Model, model: body.DefaultModel?.Trim(),
            enabled: body.Enabled).ConfigureAwait(false);

        var (pricingKnown, warning) = PricingWarning(pricing, norm!, body.DefaultModel?.Trim());
        return Results.Ok(new PutProviderSettingsResponse(
            norm!, body.DefaultModel?.Trim(), settings.IsEnabled(norm!), pricingKnown, warning));
    }

    /// <summary>
    /// <c>DELETE /api/admin/providers/{key}/settings</c> — remove the platform
    /// row entirely (model AND enabled flag) → resolution falls back to
    /// config/descriptor. 404 when no row existed.
    /// </summary>
    public static async Task<IResult> DeleteProviderSettings(
        string key,
        ClaimsPrincipal principal,
        [FromServices] IProviderSettingsStore settings,
        [FromServices] IInlineToolLoopRunner runner,
        HttpContext http)
    {
        var (norm, err) = NormalizeProvider(key);
        if (err is not null) return err;

        var previous = runner.ResolveDefaultModelWithSource(norm!, tenantId: null);
        var removed = await settings.RemovePlatformAsync(norm!, http.RequestAborted)
            .ConfigureAwait(false);
        if (!removed)
        {
            return Results.NotFound(new
            {
                error = "settings_not_found",
                detail = "no platform settings row for this provider.",
            });
        }

        await EmitSettingsChangeAsync(
            http, tenantId: null, principal.GetUserId(), norm!, scope: "platform",
            operation: "removed", previousModel: previous.Model, model: null, enabled: null)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    // ── shared helpers (also used by the tenant model routes) ──────────────

    /// <summary>Three-way key status (46-0 plan D6): <c>not_required</c> for
    /// the key-optional listing family; else the resolver answers —
    /// <c>configured</c> on success (credential discarded immediately),
    /// <c>missing</c> on PROVIDER_CREDENTIAL_UNAVAILABLE. Answered by the
    /// RESOLVER, never by reading config, so cabinet-stored keys count.</summary>
    internal static async Task<string> ResolveKeyStatusAsync(
        IProviderCredentialResolver credentials, string canonicalKey, CancellationToken ct)
    {
        if (ProviderModelCatalogService.KeyOptionalProviders.Contains(canonicalKey))
        {
            return "not_required";
        }

        try
        {
            _ = await credentials.ResolveAsync(null, canonicalKey, ct).ConfigureAwait(false);
            return "configured"; // resolved credential discarded — never surfaced
        }
        catch (OperationCanceledException) { throw; }
        catch (TammaError ex) when (ex.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE")
        {
            return "missing";
        }
        catch
        {
            return "missing";
        }
    }

    /// <summary>Current-model injection (46-0 AC5, epic D6): the effective
    /// model is ALWAYS an entry — flagged in place when the live list carries
    /// it, synthesized (DisplayName null, <c>Delisted: true</c>) and prepended
    /// when the provider delisted it (or the list is empty/stale). The
    /// <c>Delisted</c> flag states the fact so neither UI has to infer
    /// synthesis positionally (bug 2026-07-27-models-envelope-lacks-delisted-flag).</summary>
    internal static ProviderModelsResponse BuildModelsResponse(
        string provider, ProviderModelList list, string? currentModel)
    {
        var entries = new List<ProviderModelEntry>(list.Models.Count + 1);
        var hasCurrent = false;
        foreach (var m in list.Models)
        {
            var isCurrent = !string.IsNullOrEmpty(currentModel)
                && string.Equals(m.Id, currentModel, StringComparison.Ordinal);
            hasCurrent |= isCurrent;
            entries.Add(new ProviderModelEntry(m.Id, m.DisplayName, m.Deprecated, isCurrent));
        }

        if (!hasCurrent && !string.IsNullOrEmpty(currentModel))
        {
            entries.Insert(0, new ProviderModelEntry(
                currentModel!, DisplayName: null, Deprecated: false, Current: true,
                Delisted: true));
        }

        return new ProviderModelsResponse(
            provider, entries, list.FetchedAt, list.Stale, list.ErrorCode);
    }

    /// <summary>Alias → canonical → allowlist → 404 (never enumerate) — the
    /// <c>ProviderCredentialEndpoints.NormalizeProvider</c> shape.</summary>
    internal static (string? Provider, IResult? Error) NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return (null, Results.BadRequest(new { error = "invalid_provider" }));
        }
        var spelled = provider.Trim().ToLowerInvariant();
        var norm = ProviderCatalog.Resolve(spelled)?.Key
            ?? ProviderCatalog.ResolveNonHttp(spelled)?.Key
            ?? spelled;
        if (!ProviderAllowlist.IsAllowedDefault(norm))
        {
            return (null, Results.NotFound(new { error = "unknown_provider" }));
        }
        return (norm, null);
    }

    /// <summary>46-1 AC5 validation: model non-empty, ≤ 256 chars, no
    /// whitespace-only, no control characters.</summary>
    internal static IResult? ValidateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new
            {
                error = "invalid_model",
                detail = "model must be a non-empty string.",
            });
        }
        if (model.Length > MaxModelLength)
        {
            return Results.BadRequest(new
            {
                error = "invalid_model",
                detail = $"model must be at most {MaxModelLength} characters.",
            });
        }
        if (model.Any(char.IsControl))
        {
            return Results.BadRequest(new
            {
                error = "invalid_model",
                detail = "model must not contain control characters.",
            });
        }
        return null;
    }

    /// <summary>Epic D3b — allow-with-warning: settings writes warn (never
    /// block) when the chosen model has no pricing row for cost attribution.</summary>
    internal static (bool PricingKnown, string? Warning) PricingWarning(
        IProviderPricingService pricing, string provider, string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return (true, null);
        }
        if (pricing.IsKnown(provider, model))
        {
            return (true, null);
        }
        return (false,
            $"No cost pricing row exists for {provider}/{model} — calls will record " +
            "cost 0 on the runner path and fail on the SaaS billing path until a " +
            "pricing row is added (admin → pricing).");
    }

    /// <summary>
    /// Story 46-1 (AC8) — emit the curated
    /// <c>PROVIDER.SETTINGS_CHANGED.SUCCESS</c> DCB event for a settings
    /// mutation. Tags: provider, scope (platform|tenant|user), operation
    /// (set|removed|enabled|disabled), mode; data: previous→new model +
    /// enabled. Never any key material. Resolved best-effort off the request
    /// scope (mirrors <c>EmitByokChangeAsync</c>); never throws.
    /// </summary>
    internal static async Task EmitSettingsChangeAsync(
        HttpContext http,
        Guid? tenantId,
        Guid? actorUserId,
        string provider,
        string scope,
        string operation,
        string? previousModel,
        string? model,
        bool? enabled)
    {
        ISensitiveActionEmitter? emitter;
        try { emitter = http.RequestServices?.GetService<ISensitiveActionEmitter>(); }
        catch { emitter = null; }
        if (emitter is null) return;

        var tags = new Dictionary<string, string?>
        {
            ["provider"] = provider,
            ["scope"] = scope,
            ["operation"] = operation,
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["scope"] = scope,
            ["operation"] = operation,
        };
        if (!string.IsNullOrEmpty(previousModel)) data["previousModel"] = previousModel;
        if (!string.IsNullOrEmpty(model)) data["model"] = model;
        if (enabled is bool e) data["enabled"] = e;

        var action = tenantId is Guid tid
            ? SensitiveAction.ForTenant(
                SensitiveActionCatalog.ProviderSettingsChanged, tid, actorUserId, tags, data)
            : SensitiveAction.ForPlatform(
                SensitiveActionCatalog.ProviderSettingsChanged, null, actorUserId, tags, data);

        try
        {
            await emitter.EmitAsync(action, http.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — an audit-append failure never fails the mutation
            // (the emitter itself is contracted never to throw; this is belt
            // and suspenders around resolution edge cases).
        }
    }
}

/// <summary>One provider row of the admin status roster. NEVER carries key
/// material — <see cref="KeyStatus"/> is a classification only.</summary>
public sealed record ProviderStatusRow(
    string Key,
    string DisplayName,
    string Transport,
    string? Dialect,
    string? EffectiveBaseUrl,
    string KeyStatus,
    bool ModelsSupported,
    string? CurrentModel,
    string? Source,
    bool Enabled,
    IReadOnlyList<string> Aliases);

/// <summary>One entry of a live model list (46-0 AC5 response shape).
/// <para><c>Delisted</c> is <c>true</c> ONLY on the entry
/// <see cref="ProviderAdminEndpoints.BuildModelsResponse"/> synthesized because
/// the provider's list no longer carries the currently-effective model —
/// genuinely-listed entries serialize without the field (default <c>false</c>
/// is omitted), so consumers read absent/false as "listed". Additive; replaces
/// the 46-2/46-3 client-side heuristics
/// (bug 2026-07-27-models-envelope-lacks-delisted-flag).</para></summary>
public sealed record ProviderModelEntry(
    string Id,
    string? DisplayName,
    bool Deprecated,
    bool Current,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Delisted = false);

/// <summary>The fail-soft models envelope both the admin and tenant models
/// routes return (always HTTP 200 for a known provider — epic D6).</summary>
public sealed record ProviderModelsResponse(
    string Provider,
    IReadOnlyList<ProviderModelEntry> Models,
    DateTimeOffset? FetchedAt,
    bool Stale,
    string? ErrorCode);

/// <summary>PUT body for the platform settings route — at least one field.</summary>
public sealed record PutProviderSettingsRequest(string? DefaultModel, bool? Enabled);

/// <summary>PUT response — carries the epic-D3b pricing warning.</summary>
public sealed record PutProviderSettingsResponse(
    string Provider,
    string? DefaultModel,
    bool Enabled,
    bool PricingKnown,
    string? Warning);
