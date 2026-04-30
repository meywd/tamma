using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-2 — production
/// <see cref="IPlatformCredentialReader"/>. Reads installation
/// credential plaintext through the same Story 29-2 seam every other
/// credentialled subsystem uses
/// (compare <c>DefaultAlertChannelSecretReader</c>):
///
/// <list type="number">
///   <item>Look up the secret row by
///         <c>(scope, tenantId?, name)</c> in
///         <see cref="SecretsDbContext"/>.</item>
///   <item>Read the active version's plaintext via
///         <see cref="ISecretStoreBackend.GetVersionPlaintextAsync"/>.</item>
///   <item>Emit a <see cref="SecretAuditEventTypes.Read"/> audit event
///         on every successful and failed read (Story 29-1 AC5 — every
///         secret read MUST be auditable; webhook traffic is the
///         highest-volume reader). System-triggered reads use
///         <c>Guid.Empty</c> as the actor sentinel.</item>
/// </list>
///
/// <para>This adapter ships in <c>Tamma.Api</c> (not
/// <c>Tamma.Platforms</c>) because <c>SecretsDbContext</c> +
/// <c>ISecretStoreBackend</c> live in <c>Tamma.Api</c>; the resolver
/// consumes the slim
/// <see cref="IPlatformCredentialReader"/> port so the platform layer
/// has no compile-time dependency on the API project.</para>
/// </summary>
public sealed class SecretStorePlatformCredentialReader
    : IPlatformCredentialReader
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ISecretAccessAuditor _auditor;
    private readonly TimeProvider _timeProvider;

    public SecretStorePlatformCredentialReader(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ISecretAccessAuditor auditor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _secretsFactory = secretsFactory;
        _backend = backend;
        _auditor = auditor;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<string?> ReadActivePlaintextAsync(
        string scope,
        Guid? tenantId,
        string name,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Validate scope/tenantId invariant up front so a bad
        // installation row surfaces a clean argument error rather
        // than a missing-row null at the bottom.
        if (scope == "tenant" && tenantId is null)
        {
            throw new ArgumentException(
                "Tenant-scoped secret reads require a non-null tenantId.",
                nameof(tenantId));
        }
        if (scope == "platform" && tenantId is not null)
        {
            throw new ArgumentException(
                "Platform-scoped secrets must not carry a tenantId.",
                nameof(tenantId));
        }
        if (scope is not ("platform" or "tenant"))
        {
            throw new ArgumentException(
                $"Unknown secret scope '{scope}'. Expected 'platform' or 'tenant'.",
                nameof(scope));
        }

        var secretScope = scope == "platform" ? SecretScope.Platform : SecretScope.Tenant;
        var auditRef = new SecretRef(secretScope, tenantId, name);

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct)
            .ConfigureAwait(false);

        var row = await ctx.Secrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.Scope == scope
                && s.TenantId == tenantId
                && s.Name == name,
                ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            await EmitReadAsync(auditRef, versionNumber: null,
                SecretAuditOutcome.Failure, detail: "row_not_found", ct)
                .ConfigureAwait(false);
            return null;
        }
        if (row.ActiveVersionNumber <= 0)
        {
            await EmitReadAsync(auditRef, versionNumber: null,
                SecretAuditOutcome.Failure, detail: "no_active_version", ct)
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            var plaintext = await _backend
                .GetVersionPlaintextAsync(row.Id, row.ActiveVersionNumber, ct)
                .ConfigureAwait(false);
            if (plaintext is null)
            {
                await EmitReadAsync(auditRef, row.ActiveVersionNumber,
                    SecretAuditOutcome.Failure, detail: "version_plaintext_missing", ct)
                    .ConfigureAwait(false);
                return null;
            }
            await EmitReadAsync(auditRef, row.ActiveVersionNumber,
                SecretAuditOutcome.Success, detail: null, ct)
                .ConfigureAwait(false);
            return plaintext;
        }
        catch (KeyNotFoundException)
        {
            await EmitReadAsync(auditRef, row.ActiveVersionNumber,
                SecretAuditOutcome.Failure, detail: "version_scrubbed", ct)
                .ConfigureAwait(false);
            return null;
        }
    }

    private Task EmitReadAsync(
        SecretRef reference,
        int? versionNumber,
        SecretAuditOutcome outcome,
        string? detail,
        CancellationToken ct)
    {
        // System-triggered read (webhook/dispatcher). No HTTP user.
        // Guid.Empty is the sentinel used elsewhere for non-user actors.
        return _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: SecretAuditEventTypes.Read,
                Reference: reference,
                ActorUserId: Guid.Empty,
                VersionNumber: versionNumber,
                Outcome: outcome,
                Detail: detail,
                OccurredAt: _timeProvider.GetUtcNow()),
            ct);
    }
}
