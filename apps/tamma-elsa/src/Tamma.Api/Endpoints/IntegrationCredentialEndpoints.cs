using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Auth;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.Integrations;
using Tamma.Core.Audit;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Integration BYOK — tenant-admin management API for per-tenant JIRA + email
/// credentials, the integration sibling of Story 32-3's
/// <c>ProviderCredentialEndpoints</c>.
///
/// <list type="bullet">
///   <item><c>POST   /api/v1/integrations/jira/credential</c> — set the tenant's
///     JIRA credential bundle (baseUrl + email + apiToken).</item>
///   <item><c>DELETE /api/v1/integrations/jira/credential</c> — remove it (next
///     resolve falls back to the single-user system tier / fails loud).</item>
///   <item><c>POST   /api/v1/integrations/email/credential</c> — set the tenant's
///     email transport credential bundle (transport + from + SMTP/Resend secret).</item>
///   <item><c>DELETE /api/v1/integrations/email/credential</c> — remove it.</item>
/// </list>
///
/// <para>RBAC: writes are gated to <c>tenant_owner</c> / <c>tenant_admin</c> by the
/// <c>PlatformsManage</c> route policy (member → 403). Set is write-only: the
/// response carries metadata (version) only, NEVER the secret (reveal-safe). Every
/// mutation calls the resolver's <c>Invalidate</c> and emits the curated
/// <see cref="SensitiveActionCatalog.IntegrationCredentialChanged"/> audit event.
/// Integration credentials are tenant-scoped only (no per-user layer, mirroring the
/// provider BYOK + prompt store).</para>
/// </summary>
public static class IntegrationCredentialEndpoints
{
    private const string JiraIntegration = "jira";
    private const string EmailIntegration = "email";
    private const int MinSecretLength = 4;
    private const int MaxSecretLength = 8192;

    // ── JIRA ────────────────────────────────────────────────────────────────

    /// <summary><c>POST /api/v1/integrations/jira/credential</c>.</summary>
    public static async Task<IResult> SetJiraCredential(
        SetJiraCredentialRequest body,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] IIntegrationCredentialCabinet cabinet,
        [FromServices] IJiraCredentialResolver resolver,
        HttpContext http)
    {
        if (body is null)
        {
            return Results.BadRequest(new { error = "invalid_body" });
        }
        var validation = ValidateJira(body);
        if (validation is not null) return validation;

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "registering an integration credential requires tenant context.",
            });
        }

        var bundle = new JiraCredential(body.BaseUrl!.Trim(), body.Email!.Trim(), body.ApiToken!);
        var json = JiraCredentialCodec.Serialize(bundle);

        try
        {
            var meta = await cabinet.SetAsync(
                tid, IntegrationCabinetNames.JiraConfig, JiraIntegration, json,
                principal.GetUserId() ?? Guid.Empty, http.RequestAborted)
                .ConfigureAwait(false);

            resolver.Invalidate(tid);
            await EmitChangeAsync(http, tid, principal.GetUserId(), JiraIntegration, "set",
                meta.ActiveVersionNumber).ConfigureAwait(false);

            return Results.Created(
                "/api/v1/integrations/jira/credential",
                new SetIntegrationCredentialResponse(JiraIntegration, meta.ActiveVersionNumber));
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
                detail = "a JIRA credential already exists for this tenant; delete it then set again to change it.",
            });
        }
    }

    /// <summary><c>DELETE /api/v1/integrations/jira/credential</c>.</summary>
    public static Task<IResult> DeleteJiraCredential(
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] IIntegrationCredentialCabinet cabinet,
        [FromServices] IJiraCredentialResolver resolver,
        HttpContext http) =>
        DeleteAsync(
            JiraIntegration, IntegrationCabinetNames.JiraConfig,
            principal, tenantContext, cabinet, () => resolver.Invalidate(tenantContext.TenantId), http);

    // ── EMAIL ───────────────────────────────────────────────────────────────

    /// <summary><c>POST /api/v1/integrations/email/credential</c>.</summary>
    public static async Task<IResult> SetEmailCredential(
        SetEmailCredentialRequest body,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] IIntegrationCredentialCabinet cabinet,
        [FromServices] IEmailCredentialResolver resolver,
        HttpContext http)
    {
        if (body is null)
        {
            return Results.BadRequest(new { error = "invalid_body" });
        }
        var (bundle, validation) = ValidateEmail(body);
        if (validation is not null) return validation;

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "registering an integration credential requires tenant context.",
            });
        }

        var json = EmailCredentialCodec.Serialize(bundle!);

        try
        {
            var meta = await cabinet.SetAsync(
                tid, IntegrationCabinetNames.EmailConfig, EmailIntegration, json,
                principal.GetUserId() ?? Guid.Empty, http.RequestAborted)
                .ConfigureAwait(false);

            resolver.Invalidate(tid);
            await EmitChangeAsync(http, tid, principal.GetUserId(), EmailIntegration, "set",
                meta.ActiveVersionNumber).ConfigureAwait(false);

            return Results.Created(
                "/api/v1/integrations/email/credential",
                new SetIntegrationCredentialResponse(EmailIntegration, meta.ActiveVersionNumber));
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
                detail = "an email credential already exists for this tenant; delete it then set again to change it.",
            });
        }
    }

    /// <summary><c>DELETE /api/v1/integrations/email/credential</c>.</summary>
    public static Task<IResult> DeleteEmailCredential(
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        [FromServices] IIntegrationCredentialCabinet cabinet,
        [FromServices] IEmailCredentialResolver resolver,
        HttpContext http) =>
        DeleteAsync(
            EmailIntegration, IntegrationCabinetNames.EmailConfig,
            principal, tenantContext, cabinet, () => resolver.Invalidate(tenantContext.TenantId), http);

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteAsync(
        string integration,
        string cabinetName,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        IIntegrationCredentialCabinet cabinet,
        Action invalidate,
        HttpContext http)
    {
        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.NotFound();
        }

        var removed = await cabinet.RemoveAsync(tid, cabinetName, http.RequestAborted)
            .ConfigureAwait(false);
        if (!removed)
        {
            return Results.NotFound(new
            {
                error = "credential_not_found",
                detail = $"no {integration} credential is configured for this tenant.",
            });
        }

        invalidate();
        await EmitChangeAsync(http, tid, principal.GetUserId(), integration, "removed", version: null)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Emit the curated <see cref="SensitiveActionCatalog.IntegrationCredentialChanged"/>
    /// BYOK audit event (tenant-scoped) after a successful cabinet mutation. Metadata
    /// only (integration, operation, version) — the emitter additionally scrubs any
    /// secret-shaped value. Best-effort off the request scope; a missing registration
    /// simply skips the emission. Never throws.
    /// </summary>
    private static async Task EmitChangeAsync(
        HttpContext http, Guid tenantId, Guid? actorUserId,
        string integration, string operation, int? version)
    {
        ISensitiveActionEmitter? emitter;
        try { emitter = http.RequestServices?.GetService<ISensitiveActionEmitter>(); }
        catch { emitter = null; }
        if (emitter is null) return;

        var tags = new Dictionary<string, string?>
        {
            ["integration"] = integration,
            ["operation"] = operation,
            ["mode"] = "byok",
        };
        var data = new Dictionary<string, object?>
        {
            ["integration"] = integration,
            ["operation"] = operation,
            ["mode"] = "byok",
        };
        if (version is int v) data["version"] = v;

        await emitter.EmitAsync(
            SensitiveAction.ForTenant(
                SensitiveActionCatalog.IntegrationCredentialChanged, tenantId, actorUserId, tags, data),
            http.RequestAborted).ConfigureAwait(false);
    }

    private static IResult? ValidateJira(SetJiraCredentialRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.BaseUrl)
            || !Uri.TryCreate(body.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new
            {
                error = "invalid_base_url",
                detail = "baseUrl must be an absolute http(s) URL.",
            });
        }
        if (string.IsNullOrWhiteSpace(body.Email))
        {
            return Results.BadRequest(new { error = "invalid_email", detail = "email is required." });
        }
        return ValidateSecret(body.ApiToken, "apiToken");
    }

    private static (EmailCredential? Bundle, IResult? Error) ValidateEmail(SetEmailCredentialRequest body)
    {
        var transport = (body.Transport ?? string.Empty).Trim().ToLowerInvariant();
        if (transport != EmailCredential.TransportSmtp && transport != EmailCredential.TransportResend)
        {
            return (null, Results.BadRequest(new
            {
                error = "invalid_transport",
                detail = "transport must be 'smtp' or 'resend'.",
            }));
        }
        if (string.IsNullOrWhiteSpace(body.From))
        {
            return (null, Results.BadRequest(new { error = "invalid_from", detail = "from is required." }));
        }

        if (transport == EmailCredential.TransportResend)
        {
            var secretError = ValidateSecret(body.ResendApiKey, "resendApiKey");
            if (secretError is not null) return (null, secretError);
        }
        else if (string.IsNullOrWhiteSpace(body.SmtpHost))
        {
            return (null, Results.BadRequest(new
            {
                error = "invalid_smtp_host",
                detail = "smtpHost is required for the smtp transport.",
            }));
        }

        var bundle = new EmailCredential(
            transport,
            body.From!.Trim(),
            ResendApiKey: body.ResendApiKey,
            SmtpHost: body.SmtpHost,
            SmtpPort: body.SmtpPort,
            SmtpUsername: body.SmtpUsername,
            SmtpPassword: body.SmtpPassword,
            SmtpUseStartTls: body.SmtpUseStartTls);
        return (bundle, null);
    }

    private static IResult? ValidateSecret(string? secret, string field)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return Results.BadRequest(new { error = "invalid_secret", detail = $"{field} is required." });
        }
        if (secret.Length is < MinSecretLength or > MaxSecretLength)
        {
            return Results.BadRequest(new
            {
                error = "invalid_secret",
                detail = $"{field} must be between {MinSecretLength} and {MaxSecretLength} chars.",
            });
        }
        return null;
    }

    private static bool IsDuplicate(Exception ex) =>
        ex is InvalidOperationException
        || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        || (ex is Microsoft.EntityFrameworkCore.DbUpdateException due
            && due.InnerException is Npgsql.PostgresException ipg && ipg.SqlState == "23505");
}

/// <summary>Request body to set a JIRA credential bundle. Fields are write-only; never echoed.</summary>
public sealed record SetJiraCredentialRequest(string? BaseUrl, string? Email, string? ApiToken);

/// <summary>Request body to set an email transport credential bundle. Write-only.</summary>
public sealed record SetEmailCredentialRequest(
    string? Transport,
    string? From,
    string? ResendApiKey = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    bool? SmtpUseStartTls = null);

/// <summary>Reveal-safe response — metadata only (integration + active version). NEVER the secret.</summary>
public sealed record SetIntegrationCredentialResponse(string Integration, int Version);
