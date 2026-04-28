using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Query;

/// <summary>
/// Story 29-4 / 29-5 implementation of <see cref="ISecretQueryService"/>.
/// Reads / mutates metadata via <see cref="SecretsDbContext"/>. Emits
/// a <see cref="SecretAuditEventTypes.VersionRevoked"/> event on
/// retire so the admin-UI audit feed surfaces the action.
/// </summary>
public sealed class SecretQueryService : ISecretQueryService
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretAccessAuditor _auditor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecretQueryService> _logger;

    public SecretQueryService(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretAccessAuditor auditor,
        TimeProvider timeProvider,
        ILogger<SecretQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _secretsFactory = secretsFactory;
        _auditor = auditor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ValidateScopeCombo(scope, tenantId);

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var scopeString = scope.ToString().ToLowerInvariant();
        var q = ctx.Secrets.AsNoTracking().Where(r => r.Scope == scopeString);
        if (scope == SecretScope.Tenant)
        {
            q = q.Where(r => r.TenantId == tenantId!.Value);
        }

        var rows = await q
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ProjectMetadata).ToList();
    }

    /// <inheritdoc />
    public async Task<SecretMetadata?> GetAsync(
        Guid secretId,
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ValidateScopeCombo(scope, tenantId);

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ctx.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == secretId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        if (!RowMatchesScope(row, scope, tenantId))
        {
            // Out-of-scope: treat as not-found so existence does not leak.
            _logger.LogWarning(
                "SecretQueryService.Get: scope mismatch; requested scope={Scope}, tenantId={TenantId}, row.scope={RowScope}, row.tenantId={RowTenantId}",
                scope, tenantId, row.Scope, row.TenantId);
            return null;
        }

        return ProjectMetadata(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
        Guid secretId,
        SecretScope scope,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ValidateScopeCombo(scope, tenantId);

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ctx.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == secretId, ct)
            .ConfigureAwait(false);
        if (row is null || !RowMatchesScope(row, scope, tenantId))
        {
            return Array.Empty<SecretVersion>();
        }

        var versions = await ctx.SecretVersions.AsNoTracking()
            .Where(v => v.SecretId == secretId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return versions.Select(ProjectVersion).ToList();
    }

    /// <inheritdoc />
    public async Task<SecretVersionStatus> RetireVersionAsync(
        Guid secretId,
        int versionNumber,
        SecretScope scope,
        Guid? tenantId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        ValidateScopeCombo(scope, tenantId);
        if (versionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber), versionNumber,
                "Version numbers are 1-based.");
        }

        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var secretRow = await ctx.Secrets
            .FirstOrDefaultAsync(r => r.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secretRow is null || !RowMatchesScope(secretRow, scope, tenantId))
        {
            throw new KeyNotFoundException(
                $"No secret matches id={secretId} in the requested scope.");
        }

        if (secretRow.ActiveVersionNumber == versionNumber)
        {
            throw new InvalidOperationException(
                "Cannot retire the active version. Rotate first so the " +
                "successor is in place before the current row is retired.");
        }

        var versionRow = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == versionNumber,
                ct)
            .ConfigureAwait(false);
        if (versionRow is null)
        {
            throw new KeyNotFoundException(
                $"No version row for secretId={secretId}, versionNumber={versionNumber}.");
        }

        var now = _timeProvider.GetUtcNow();
        var newStatus = versionRow.Status switch
        {
            "retired_grace" => "revoked",
            "pending" => "revoked",
            "active" => throw new InvalidOperationException(
                "Active version row cannot be retired in place."),
            _ => "revoked",
        };

        versionRow.Status = newStatus;
        if (newStatus == "revoked")
        {
            // Scrub ciphertext here too so a future accidental reveal
            // path has nothing to hand back. The backend's
            // DeleteVersionAsync does the same thing on its own call;
            // we null it here so the row is one-shot scrubbed even if
            // the backend is an in-memory fake.
            versionRow.Ciphertext = null;
        }
        if (versionRow.RetiredAt is null)
        {
            versionRow.RetiredAt = now.UtcDateTime;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        await _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: SecretAuditEventTypes.VersionRevoked,
                Reference: new SecretRef(scope, tenantId, secretRow.Name),
                ActorUserId: actorUserId,
                VersionNumber: versionNumber,
                Outcome: SecretAuditOutcome.Success,
                Detail: null,
                OccurredAt: now),
            ct)
            .ConfigureAwait(false);

        return newStatus switch
        {
            "revoked" => SecretVersionStatus.Revoked,
            _ => SecretVersionStatus.RetiredGrace,
        };
    }

    // ── helpers ────────────────────────────────────────────────────

    private static void ValidateScopeCombo(SecretScope scope, Guid? tenantId)
    {
        if (scope == SecretScope.Tenant && (tenantId is null || tenantId == Guid.Empty))
        {
            throw new ArgumentException(
                "TenantId is required when scope is Tenant.",
                nameof(tenantId));
        }
        if (scope == SecretScope.Platform && tenantId is not null)
        {
            throw new ArgumentException(
                "TenantId must be null when scope is Platform.",
                nameof(tenantId));
        }
    }

    private static bool RowMatchesScope(SecretRow row, SecretScope scope, Guid? tenantId)
    {
        var rowScope = row.Scope?.ToLowerInvariant() ?? string.Empty;
        return scope switch
        {
            SecretScope.Platform => rowScope == "platform" && row.TenantId is null,
            SecretScope.Tenant => rowScope == "tenant" && row.TenantId == tenantId,
            _ => false,
        };
    }

    private static SecretMetadata ProjectMetadata(SecretRow row)
    {
        var scope = Enum.Parse<SecretScope>(row.Scope, ignoreCase: true);
        var purpose = Enum.Parse<SecretPurpose>(row.Purpose, ignoreCase: true);

        IReadOnlyList<ConsumerRef> consumers;
        try
        {
            consumers = System.Text.Json.JsonSerializer
                .Deserialize<List<ConsumerRef>>(row.ConsumerRefsJson)
                ?? (IReadOnlyList<ConsumerRef>)Array.Empty<ConsumerRef>();
        }
        catch
        {
            consumers = Array.Empty<ConsumerRef>();
        }

        var schedule = DeserializeSchedule(row.RotationScheduleJson);

        DateTimeOffset? lastRotated = row.LastRotatedAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.LastRotatedAt.Value, DateTimeKind.Utc));
        DateTimeOffset? nextDue = row.NextRotationDueAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.NextRotationDueAt.Value, DateTimeKind.Utc));

        return new SecretMetadata(
            Id: row.Id,
            Name: row.Name,
            Scope: scope,
            TenantId: row.TenantId,
            Purpose: purpose,
            ConsumerRefs: consumers,
            OwnerUserId: row.OwnerUserId,
            RotationSchedule: schedule,
            LastRotatedAt: lastRotated,
            NextRotationDueAt: nextDue,
            ActiveVersionNumber: row.ActiveVersionNumber,
            CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)));
    }

    private static SecretVersion ProjectVersion(SecretVersionRow row)
    {
        var status = row.Status switch
        {
            "active" => SecretVersionStatus.Active,
            "retired_grace" => SecretVersionStatus.RetiredGrace,
            "revoked" => SecretVersionStatus.Revoked,
            _ => SecretVersionStatus.Pending,
        };
        return new SecretVersion(
            SecretId: row.SecretId,
            VersionNumber: row.VersionNumber,
            Status: status,
            CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            ActivatedAt: row.ActivatedAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.ActivatedAt.Value, DateTimeKind.Utc)),
            RetiredAt: row.RetiredAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.RetiredAt.Value, DateTimeKind.Utc)),
            CreatedByUserId: row.CreatedByUserId);
    }

    private static RotationSchedule DeserializeSchedule(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Kind", out var kindProp))
                return RotationSchedule.None;
            var kind = kindProp.GetString() ?? "None";
            return kind switch
            {
                "Days" when root.TryGetProperty("Days", out var d)
                    && d.ValueKind == System.Text.Json.JsonValueKind.Number
                    => RotationSchedule.EveryDays(d.GetInt32()),
                "Cron" when root.TryGetProperty("CronExpression", out var c)
                    && c.ValueKind == System.Text.Json.JsonValueKind.String
                    => RotationSchedule.Cron(c.GetString()!),
                _ => RotationSchedule.None,
            };
        }
        catch
        {
            return RotationSchedule.None;
        }
    }
}
