using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Auth;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Reveal;
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
        return Results.NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────

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
        var norm = provider.Trim().ToLowerInvariant();
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
