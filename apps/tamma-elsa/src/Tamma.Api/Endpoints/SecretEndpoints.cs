using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Api.Authorization;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Reveal;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 29-3 endpoints for the reveal-once-on-create UX. Three
/// routes — create, rotate, reveal — plus two aliases (platform vs
/// tenant create) so admin flows can land on the right auth policy
/// at the route mapping site (Program.cs).
///
/// <para><b>Plaintext rule</b>: the response body for the create /
/// rotate endpoints carries the <c>revealToken</c> + metadata ONLY —
/// never the plaintext. The caller must follow up with
/// <c>GET /api/v1/secrets/reveal/{token}</c> within 60 seconds to
/// exchange the token for the plaintext. Subsequent calls with the
/// same token return 410 Gone.</para>
///
/// <para>Rate-limit policy <c>SecretReveal</c> (10/min/user) is
/// attached in Program.cs so a token-guessing attacker trips 429
/// well before exhausting the 256-bit token search space.</para>
/// </summary>
public static class SecretEndpoints
{
    private const int MinPlaintextLength = 8;
    private const int MaxPlaintextLength = 8192;

    /// <summary>
    /// Platform-scope create: <c>POST /api/admin/secrets</c>. Gated by
    /// <c>OwnerAccess</c> at the mapping site. The request body is
    /// shape-checked here; the service owns the business invariants
    /// (name slug, purpose × scope).
    /// </summary>
    public static async Task<IResult> CreatePlatformSecret(
        CreateSecretRequestBody body,
        ClaimsPrincipal principal,
        [FromServices] ISecretRevealService revealService,
        HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validationError = ValidateCreateBody(body, requireTenantId: false);
        if (validationError is not null) return validationError;

        var ownerUserId = ResolveUserId(principal);

        try
        {
            var result = await revealService.IssueCreateAsync(
                name: body.Name,
                scope: SecretScope.Platform,
                tenantId: null,
                purpose: ParsePurpose(body.Purpose),
                initialPlaintext: body.Plaintext!,
                consumerRefs: body.ConsumerRefs,
                ownerUserId: ownerUserId,
                rotationSchedule: BuildSchedule(body.RotationDays),
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/secrets/{result.Metadata.Id}",
                ToIssueResponse(result));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Tenant-scope create: <c>POST /api/v1/orgs/{tenantId}/secrets</c>.
    /// Route carries the tenant id so the handler does not have to
    /// look it up from the caller's claims — the route authorization
    /// checks membership separately.
    /// </summary>
    public static async Task<IResult> CreateTenantSecret(
        Guid tenantId,
        CreateSecretRequestBody body,
        ClaimsPrincipal principal,
        [FromServices] ISecretRevealService revealService,
        HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(body);
        var validationError = ValidateCreateBody(body, requireTenantId: true);
        if (validationError is not null) return validationError;

        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });

        var ownerUserId = ResolveUserId(principal);

        try
        {
            var result = await revealService.IssueCreateAsync(
                name: body.Name,
                scope: SecretScope.Tenant,
                tenantId: tenantId,
                purpose: ParsePurpose(body.Purpose),
                initialPlaintext: body.Plaintext!,
                consumerRefs: body.ConsumerRefs,
                ownerUserId: ownerUserId,
                rotationSchedule: BuildSchedule(body.RotationDays),
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/orgs/{tenantId}/secrets/{result.Metadata.Id}",
                ToIssueResponse(result));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Rotation: <c>POST /api/admin/secrets/{id}/rotate</c>. Mints a
    /// new version + reveal token for it. The old version enters the
    /// RetiredGrace state per the Story 29-6 rotation saga and is not
    /// revealable through this endpoint (or any other).
    /// </summary>
    public static async Task<IResult> RotateSecret(
        Guid id,
        RotateSecretRequestBody body,
        ClaimsPrincipal principal,
        [FromServices] ISecretRevealService revealService,
        HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        if (string.IsNullOrEmpty(body.NewPlaintext))
            return Results.BadRequest(new { error = "newPlaintext is required" });
        if (body.NewPlaintext.Length is < MinPlaintextLength or > MaxPlaintextLength)
            return Results.BadRequest(new
            {
                error = $"newPlaintext length must be between {MinPlaintextLength} and {MaxPlaintextLength} characters"
            });

        var actorUserId = ResolveUserId(principal);

        try
        {
            var result = await revealService.IssueRotateAsync(
                secretId: id,
                newPlaintext: body.NewPlaintext,
                actorUserId: actorUserId,
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            return Results.Ok(ToIssueResponse(result));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Secret not found" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reveal exchange: <c>GET /api/v1/secrets/reveal/{token}</c>.
    /// Returns the plaintext exactly once. 410 Gone on any subsequent
    /// call, whether the second call lost a race or lands after the
    /// 60-second TTL has expired.
    /// </summary>
    public static async Task<IResult> RevealSecret(
        string token,
        [FromServices] ISecretRevealService revealService,
        HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Results.BadRequest(new { error = "Token is required" });

        var caller = new RevealCallerContext(
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            RemoteIp: http.Connection.RemoteIpAddress?.ToString());

        var result = await revealService.ConsumeAsync(
            token, caller, http.RequestAborted)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            RevealTokenConsumeOutcome.Success => Results.Ok(new
            {
                secretId = result.SecretId,
                name = result.SecretName,
                version = result.VersionNumber,
                plaintext = result.Plaintext,
                expiresAt = result.ExpiresAt,
            }),
            RevealTokenConsumeOutcome.AlreadyConsumed => Results.Json(
                new { error = "already_consumed" }, statusCode: 410),
            RevealTokenConsumeOutcome.Expired => Results.Json(
                new { error = "expired" }, statusCode: 410),
            RevealTokenConsumeOutcome.NotFound => Results.NotFound(
                new { error = "Token not found" }),
            _ => Results.Problem("Unknown reveal outcome"),
        };
    }

    /// <summary>
    /// Platform-scope list: <c>GET /api/v1/admin/secrets</c>. Returns
    /// every platform-scoped secret's metadata (no plaintext). Gated by
    /// <c>OwnerAccess</c> at the mapping site.
    /// </summary>
    public static async Task<IResult> ListPlatformSecrets(
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        var rows = await queryService.ListAsync(
            SecretScope.Platform, tenantId: null, http.RequestAborted)
            .ConfigureAwait(false);
        return Results.Ok(new { secrets = rows.Select(ToListItem).ToList() });
    }

    /// <summary>
    /// Tenant-scope list: <c>GET /api/v1/orgs/{tenantId}/secrets</c>.
    /// Returns only this tenant's secrets; the caller has already been
    /// proven a member by <c>RequireTenantMembershipFilter</c>. Member-
    /// level access (read-only) is sufficient — rotate / retire are
    /// admin+ via their own handlers.
    /// </summary>
    public static async Task<IResult> ListTenantSecrets(
        Guid tenantId,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });

        var rows = await queryService.ListAsync(
            SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        return Results.Ok(new { secrets = rows.Select(ToListItem).ToList() });
    }

    /// <summary>
    /// Platform-scope get: <c>GET /api/v1/admin/secrets/{id}</c>.
    /// </summary>
    public static async Task<IResult> GetPlatformSecret(
        Guid id,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var row = await queryService.GetAsync(
            id, SecretScope.Platform, tenantId: null, http.RequestAborted)
            .ConfigureAwait(false);
        return row is null
            ? Results.NotFound(new { error = "Secret not found" })
            : Results.Ok(ToDetail(row));
    }

    /// <summary>
    /// Tenant-scope get: <c>GET /api/v1/orgs/{tenantId}/secrets/{id}</c>.
    /// Cross-tenant read returns 404 (not leaked as forbidden).
    /// </summary>
    public static async Task<IResult> GetTenantSecret(
        Guid tenantId,
        Guid id,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var row = await queryService.GetAsync(
            id, SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        return row is null
            ? Results.NotFound(new { error = "Secret not found" })
            : Results.Ok(ToDetail(row));
    }

    /// <summary>
    /// Platform-scope versions: <c>GET /api/v1/admin/secrets/{id}/versions</c>.
    /// </summary>
    public static async Task<IResult> ListPlatformVersions(
        Guid id,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var versions = await queryService.ListVersionsAsync(
            id, SecretScope.Platform, tenantId: null, http.RequestAborted)
            .ConfigureAwait(false);
        return Results.Ok(new { versions = versions.Select(ToVersionItem).ToList() });
    }

    /// <summary>
    /// Tenant-scope versions: <c>GET /api/v1/orgs/{tenantId}/secrets/{id}/versions</c>.
    /// </summary>
    public static async Task<IResult> ListTenantVersions(
        Guid tenantId,
        Guid id,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var versions = await queryService.ListVersionsAsync(
            id, SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        return Results.Ok(new { versions = versions.Select(ToVersionItem).ToList() });
    }

    /// <summary>
    /// Platform-scope retire: <c>POST /api/v1/admin/secrets/{id}/retire-version/{versionNumber}</c>.
    /// Refuses the active version per AC5.
    /// </summary>
    public static async Task<IResult> RetirePlatformVersion(
        Guid id,
        int versionNumber,
        ClaimsPrincipal principal,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });
        if (versionNumber <= 0)
            return Results.BadRequest(new { error = "versionNumber must be >= 1" });

        var actorUserId = ResolveUserId(principal);

        try
        {
            var status = await queryService.RetireVersionAsync(
                id, versionNumber, SecretScope.Platform, tenantId: null,
                actorUserId, http.RequestAborted)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                secretId = id,
                versionNumber,
                status = status.ToString(),
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Secret or version not found" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 409);
        }
    }

    /// <summary>
    /// Tenant-scope retire: <c>POST /api/v1/orgs/{tenantId}/secrets/{id}/retire-version/{versionNumber}</c>.
    /// Requires tenant admin+ role (the handler enforces; membership
    /// filter wraps the route).
    /// </summary>
    public static async Task<IResult> RetireTenantVersion(
        Guid tenantId,
        Guid id,
        int versionNumber,
        ClaimsPrincipal principal,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });
        if (versionNumber <= 0)
            return Results.BadRequest(new { error = "versionNumber must be >= 1" });

        var actorUserId = ResolveUserId(principal);

        try
        {
            var status = await queryService.RetireVersionAsync(
                id, versionNumber, SecretScope.Tenant, tenantId,
                actorUserId, http.RequestAborted)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                secretId = id,
                versionNumber,
                status = status.ToString(),
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Secret or version not found" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 409);
        }
    }

    /// <summary>
    /// Tenant-scope rotate: <c>POST /api/v1/orgs/{tenantId}/secrets/{id}/rotate</c>.
    /// Same body as the platform rotate; requires admin+ role.
    /// </summary>
    public static async Task<IResult> RotateTenantSecret(
        Guid tenantId,
        Guid id,
        RotateSecretRequestBody body,
        ClaimsPrincipal principal,
        [FromServices] ISecretRevealService revealService,
        [FromServices] ISecretQueryService queryService,
        [FromServices] Tamma.Data.Repositories.ITenantPlatformInstallationRepository installations,
        [FromServices] Tamma.Platforms.IPlatformInstallationEventEmitter installationEvents,
        HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        if (string.IsNullOrEmpty(body.NewPlaintext))
            return Results.BadRequest(new { error = "newPlaintext is required" });
        if (body.NewPlaintext.Length is < MinPlaintextLength or > MaxPlaintextLength)
            return Results.BadRequest(new
            {
                error = $"newPlaintext length must be between {MinPlaintextLength} and {MaxPlaintextLength} characters"
            });

        // Defense-in-depth scope check BEFORE hitting the reveal
        // service, which would otherwise happily rotate a platform
        // row pushed into the tenant route by a forged path.
        var existing = await queryService.GetAsync(
            id, SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return Results.NotFound(new { error = "Secret not found" });
        }

        var actorUserId = ResolveUserId(principal);

        try
        {
            var result = await revealService.IssueRotateAsync(
                secretId: id,
                newPlaintext: body.NewPlaintext,
                actorUserId: actorUserId,
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            // Epic 31 review (F-medium) — if this tenant secret backs a
            // platform installation credential, the resolver's driver cache
            // is still serving a driver composed from the OLD plaintext.
            // Emit PLATFORM.INSTALLATION.CREDENTIAL_ROTATED (durable audit +
            // in-process bus fan-out) so PlatformDriverCacheInvalidator
            // evicts immediately; the cache's absolute TTL stays the safety
            // net for multi-pod deployments. Best-effort — the rotation
            // itself already succeeded.
            await EmitCredentialRotatedForBackedInstallationsAsync(
                tenantId, existing.Name, actorUserId,
                installations, installationEvents, http)
                .ConfigureAwait(false);

            return Results.Ok(ToIssueResponse(result));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Secret not found" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Epic 31 review (F-medium) — bridge the secrets plane to the platform
    /// plane: a rotated TENANT secret that a
    /// <c>tenant_platform_installations</c> row references as its credential
    /// (matched by slug) gets a CREDENTIAL_ROTATED installation event per
    /// backed installation, which the in-process bus fans out to
    /// <c>PlatformDriverCacheInvalidator</c>. Best-effort by design: a
    /// failure here must never fail the rotation that already happened —
    /// the driver cache's absolute TTL bounds the staleness either way.
    /// </summary>
    internal static async Task EmitCredentialRotatedForBackedInstallationsAsync(
        Guid tenantId,
        string secretName,
        Guid actorUserId,
        Tamma.Data.Repositories.ITenantPlatformInstallationRepository installations,
        Tamma.Platforms.IPlatformInstallationEventEmitter installationEvents,
        HttpContext http)
    {
        try
        {
            var rows = await installations
                .ListByTenantAsync(tenantId, http.RequestAborted)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!string.Equals(row.CredentialSecretScope, "tenant", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(row.CredentialSecretName, secretName, StringComparison.Ordinal))
                    continue;
                if (!Tamma.Platforms.PlatformResolver.TryParseKind(row.PlatformKind, out var kind))
                    continue;

                await installationEvents.EmitCredentialRotatedAsync(
                    tenantId, kind, row.Id, actorUserId, http.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Null-tolerant: unit-test HttpContexts carry no RequestServices,
            // and this is a diagnostics-only tail on a best-effort path.
            (http.RequestServices as IServiceProvider)?
                .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?
                .CreateLogger(nameof(SecretEndpoints))
                .LogWarning(
                    ex,
                    "Could not emit CREDENTIAL_ROTATED for installations backed by tenant "
                    + "secret {SecretName} (tenant {TenantId}); the driver cache's absolute "
                    + "TTL bounds the staleness",
                    secretName, tenantId);
        }
    }

    /// <summary>
    /// Story 29-6 (audit gap #2) — trigger the audited
    /// <c>rotate-secret</c> SAGA workflow for a platform-scoped secret:
    /// <c>POST /api/v1/secrets/{secretId}/rotate</c>. Gated by
    /// <c>PlatformOwnerAccess</c> at the mapping site (cross-tenant
    /// infrastructure — NOT <c>OwnerAccess</c>, which admits every
    /// personal-tenant owner).
    ///
    /// <para>Unlike the legacy reveal-based rotate, this mints a fresh
    /// rotation correlation id, takes the per-secret concurrency guard,
    /// and dispatches the mint → push → probe → activate → schedule-retire
    /// saga. Returns <c>202 Accepted</c> + the correlation id; emits
    /// <c>SECRET.ROTATION.REQUESTED</c>. A rotation already in flight is
    /// refused with <c>409</c> + <c>SECRET.ROTATION.REJECTED(rotation_in_progress)</c>.</para>
    /// </summary>
    public static async Task<IResult> TriggerRotateWorkflow(
        Guid secretId,
        TriggerRotateRequestBody? body,
        ClaimsPrincipal principal,
        [FromServices] Services.Secrets.Rotation.IRotationTriggerService triggerService,
        HttpContext http)
    {
        if (secretId == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var validation = ValidateTriggerBody(body);
        if (validation is not null) return validation;

        var operatorUserId = ResolveUserId(principal);

        try
        {
            var result = await triggerService.TriggerRotationAsync(
                secretId,
                operatorUserId,
                newPlaintext: body?.NewPlaintext,
                generateLength: body?.GenerateLength,
                graceWindowSeconds: body?.GraceWindowSeconds ?? 0L,
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            if (!result.Accepted)
            {
                return Results.Json(
                    new { error = result.Reason, rotationCorrelationId = result.RotationCorrelationId },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Accepted(
                value: new
                {
                    secretId,
                    rotationCorrelationId = result.RotationCorrelationId,
                    status = "dispatched",
                });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Story 29-6 (audit gap #2) — tenant-scoped trigger of the saga
    /// workflow: <c>POST /api/v1/orgs/{tenantId}/secrets/{id}/rotate-workflow</c>.
    /// Requires tenant admin+ role (membership filter wraps the route).
    /// Distinct path from the legacy reveal-based tenant rotate so both
    /// surfaces coexist.
    /// </summary>
    public static async Task<IResult> TriggerRotateTenantWorkflow(
        Guid tenantId,
        Guid id,
        TriggerRotateRequestBody? body,
        ClaimsPrincipal principal,
        [FromServices] Services.Secrets.Rotation.IRotationTriggerService triggerService,
        [FromServices] ISecretQueryService queryService,
        HttpContext http)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (id == Guid.Empty)
            return Results.BadRequest(new { error = "secretId must be a non-empty Guid" });

        var validation = ValidateTriggerBody(body);
        if (validation is not null) return validation;

        // Defense-in-depth scope check: the secret must belong to this
        // tenant before we dispatch a rotation for it.
        var existing = await queryService.GetAsync(
            id, SecretScope.Tenant, tenantId, http.RequestAborted)
            .ConfigureAwait(false);
        if (existing is null)
            return Results.NotFound(new { error = "Secret not found" });

        var operatorUserId = ResolveUserId(principal);

        try
        {
            var result = await triggerService.TriggerRotationAsync(
                id,
                operatorUserId,
                newPlaintext: body?.NewPlaintext,
                generateLength: body?.GenerateLength,
                graceWindowSeconds: body?.GraceWindowSeconds ?? 0L,
                ct: http.RequestAborted)
                .ConfigureAwait(false);

            if (!result.Accepted)
            {
                return Results.Json(
                    new { error = result.Reason, rotationCorrelationId = result.RotationCorrelationId },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Accepted(
                value: new
                {
                    secretId = id,
                    rotationCorrelationId = result.RotationCorrelationId,
                    status = "dispatched",
                });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IResult? ValidateTriggerBody(TriggerRotateRequestBody? body)
    {
        if (body is null) return null; // all-optional: saga generates a value
        if (body.NewPlaintext is not null
            && body.NewPlaintext.Length is < MinPlaintextLength or > MaxPlaintextLength)
        {
            return Results.BadRequest(new
            {
                error = $"newPlaintext length must be between {MinPlaintextLength} and {MaxPlaintextLength} characters"
            });
        }
        if (body.GenerateLength is not null && body.GenerateLength is < 16 or > 256)
            return Results.BadRequest(new { error = "generateLength must be between 16 and 256" });
        if (body.GraceWindowSeconds is < 0)
            return Results.BadRequest(new { error = "graceWindowSeconds must be >= 0" });
        return null;
    }

    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minting / retiring / rotating credentials within a tenant is
    /// admin+ per Story 29-5 AC6. Membership filter has already run
    /// and stashed the caller's tenant role.
    /// </summary>
    private static bool RequireTenantAdmin(HttpContext http, out IResult? forbid)
    {
        var role = http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (role is null)
        {
            forbid = Results.Json(
                new { error = "Tenant role not resolved" },
                statusCode: StatusCodes.Status500InternalServerError);
            return false;
        }
        if (!TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin))
        {
            forbid = Results.Json(
                new { error = "Requires admin role or higher" },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        forbid = null;
        return true;
    }

    private static object ToListItem(SecretMetadata row) => new
    {
        secretId = row.Id,
        name = row.Name,
        scope = row.Scope.ToString().ToLowerInvariant(),
        tenantId = row.TenantId,
        purpose = row.Purpose.ToString(),
        consumerRefs = row.ConsumerRefs,
        activeVersion = row.ActiveVersionNumber,
        lastRotatedAt = row.LastRotatedAt,
        nextRotationDueAt = row.NextRotationDueAt,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
    };

    private static object ToDetail(SecretMetadata row) => new
    {
        secretId = row.Id,
        name = row.Name,
        scope = row.Scope.ToString().ToLowerInvariant(),
        tenantId = row.TenantId,
        purpose = row.Purpose.ToString(),
        consumerRefs = row.ConsumerRefs,
        ownerUserId = row.OwnerUserId,
        rotationSchedule = new
        {
            kind = row.RotationSchedule.Kind.ToString(),
            days = row.RotationSchedule.Days,
            cronExpression = row.RotationSchedule.CronExpression,
        },
        activeVersion = row.ActiveVersionNumber,
        lastRotatedAt = row.LastRotatedAt,
        nextRotationDueAt = row.NextRotationDueAt,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
    };

    private static object ToVersionItem(SecretVersion v) => new
    {
        secretId = v.SecretId,
        versionNumber = v.VersionNumber,
        status = v.Status.ToString(),
        createdAt = v.CreatedAt,
        activatedAt = v.ActivatedAt,
        retiredAt = v.RetiredAt,
        createdByUserId = v.CreatedByUserId,
    };

    // ─────────────────────────────────────────────────────────────────

    private static object ToIssueResponse(RevealTokenIssueResult result) => new
    {
        secretId = result.Metadata.Id,
        name = result.Metadata.Name,
        scope = result.Metadata.Scope.ToString().ToLowerInvariant(),
        tenantId = result.Metadata.TenantId,
        purpose = result.Metadata.Purpose.ToString(),
        activeVersion = result.Metadata.ActiveVersionNumber,
        createdAt = result.Metadata.CreatedAt,
        updatedAt = result.Metadata.UpdatedAt,
        // Reveal-once envelope:
        revealToken = result.RevealToken,
        revealExpiresAt = result.ExpiresAt,
        revealUrl = $"/api/v1/secrets/reveal/{result.RevealToken}",
        message = "Copy the plaintext via GET revealUrl before the token expires. This reveal is logged.",
    };

    private static IResult? ValidateCreateBody(
        CreateSecretRequestBody body, bool requireTenantId)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "name is required" });
        if (string.IsNullOrEmpty(body.Plaintext))
            return Results.BadRequest(new { error = "plaintext is required" });
        if (body.Plaintext.Length is < MinPlaintextLength or > MaxPlaintextLength)
            return Results.BadRequest(new
            {
                error = $"plaintext length must be between {MinPlaintextLength} and {MaxPlaintextLength} characters"
            });
        if (string.IsNullOrWhiteSpace(body.Purpose))
            return Results.BadRequest(new { error = "purpose is required" });
        if (!Enum.TryParse<SecretPurpose>(body.Purpose, ignoreCase: true, out _))
            return Results.BadRequest(new
            {
                error = $"purpose must be one of: {string.Join(", ", Enum.GetNames<SecretPurpose>())}"
            });
        if (body.RotationDays is < 0)
            return Results.BadRequest(new { error = "rotationDays must be >= 0" });
        return null;
    }

    private static SecretPurpose ParsePurpose(string value) =>
        Enum.Parse<SecretPurpose>(value, ignoreCase: true);

    private static RotationSchedule? BuildSchedule(int? rotationDays)
    {
        if (rotationDays is null or 0) return RotationSchedule.None;
        return RotationSchedule.EveryDays(rotationDays.Value);
    }

    private static Guid ResolveUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(sub, out var id)) return id;
        // Tests / unauthenticated flows: a deterministic sentinel is
        // better than throwing because the endpoints are behind auth
        // policies in production; tests that bypass auth still need a
        // non-empty owner id for the factory invariants.
        return new Guid("00000000-0000-0000-0000-00000000DEAD");
    }

    /// <summary>
    /// Request body for <see cref="CreatePlatformSecret"/> /
    /// <see cref="CreateTenantSecret"/>. Uses plain primitive types
    /// so the JSON payload stays backend-agnostic (no enum ordering
    /// coupling).
    /// </summary>
    public sealed record CreateSecretRequestBody(
        string Name,
        string Purpose,
        string? Plaintext,
        IReadOnlyList<ConsumerRef>? ConsumerRefs,
        int? RotationDays);

    /// <summary>Request body for <see cref="RotateSecret"/>.</summary>
    public sealed record RotateSecretRequestBody(string NewPlaintext);

    /// <summary>
    /// Request body for <see cref="TriggerRotateWorkflow"/> /
    /// <see cref="TriggerRotateTenantWorkflow"/>. All fields optional:
    /// omit <c>newPlaintext</c> to have the saga CSPRNG-generate a value
    /// (optionally sized via <c>generateLength</c>); omit
    /// <c>graceWindowSeconds</c> to use the saga default (15 min).
    /// </summary>
    public sealed record TriggerRotateRequestBody(
        string? NewPlaintext = null,
        int? GenerateLength = null,
        long GraceWindowSeconds = 0L);
}
