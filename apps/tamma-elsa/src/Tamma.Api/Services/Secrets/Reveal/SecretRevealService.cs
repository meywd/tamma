using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Story 29-3 reveal-once service. See
/// <see cref="ISecretRevealService"/> for the contract; this class
/// owns the persistence + token mechanics. Key properties:
///
/// <list type="bullet">
///   <item><description>Tokens are 32 random bytes (256 bits)
///     base64url-encoded. The caller sees the raw token once; the DB
///     only holds the HMAC-SHA256 hash under the Story 29-2 primary
///     KEK.</description></item>
///   <item><description>Consume flips the row to <c>consumed</c> in a
///     single EF save so a race between two reveal attempts resolves
///     to exactly-one winner via optimistic concurrency on the pre-
///     image. The loser sees <c>AlreadyConsumed</c>.</description></item>
///   <item><description>Token lookup is a unique-index probe on the
///     HMAC hash — no string compare on our side, so the comparison
///     is constant-time by virtue of being a btree equality.</description></item>
///   <item><description>Plaintext is fetched from the Story 29-2
///     backend on demand; never cached inside this service.</description></item>
/// </list>
/// </summary>
public sealed class SecretRevealService : ISecretRevealService
{
    /// <summary>
    /// Reveal-token TTL per Story 29-3 AC1. 60 seconds is long enough
    /// for a human to confirm the copy-to-clipboard modal, short
    /// enough that a stolen token is quickly out of reach.
    /// </summary>
    public static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(60);

    /// <summary>Raw token length in bytes (256 bits).</summary>
    public const int TokenBytesLength = 32;

    private readonly IDbContextFactory<SecretRevealDbContext> _revealFactory;
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ISecretAccessAuditor _auditor;
    private readonly IKekProvider _kekProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecretRevealService> _logger;

    public SecretRevealService(
        IDbContextFactory<SecretRevealDbContext> revealFactory,
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ISecretAccessAuditor auditor,
        IKekProvider kekProvider,
        TimeProvider timeProvider,
        ILogger<SecretRevealService> logger)
    {
        ArgumentNullException.ThrowIfNull(revealFactory);
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _revealFactory = revealFactory;
        _secretsFactory = secretsFactory;
        _backend = backend;
        _auditor = auditor;
        _kekProvider = kekProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RevealTokenIssueResult> IssueCreateAsync(
        string name,
        SecretScope scope,
        Guid? tenantId,
        SecretPurpose purpose,
        string initialPlaintext,
        IReadOnlyList<ConsumerRef>? consumerRefs,
        Guid ownerUserId,
        RotationSchedule? rotationSchedule,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(initialPlaintext))
        {
            throw new ArgumentException(
                "Initial plaintext must be non-empty for reveal-on-create.",
                nameof(initialPlaintext));
        }

        var now = _timeProvider.GetUtcNow();
        var metadata = SecretMetadataFactory.Create(
            name, scope, tenantId, purpose, consumerRefs,
            ownerUserId, rotationSchedule, now);

        await PersistSecretRowAsync(metadata, ct).ConfigureAwait(false);

        try
        {
            await _backend.PutVersionAsync(
                metadata.Id, versionNumber: 1, initialPlaintext, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await _auditor.EmitAsync(
                new SecretAuditEvent(
                    EventType: SecretAuditEventTypes.Write,
                    Reference: metadata.ToRef(),
                    ActorUserId: ownerUserId,
                    VersionNumber: 1,
                    Outcome: SecretAuditOutcome.Failure,
                    Detail: "backend_putversion_failed",
                    OccurredAt: now),
                ct)
                .ConfigureAwait(false);
            throw;
        }

        await ActivateFirstVersionAsync(metadata.Id, ownerUserId, ct)
            .ConfigureAwait(false);

        var activatedMetadata = metadata with { ActiveVersionNumber = 1 };

        var (rawToken, expiresAt) = await IssueTokenAsync(
            secretId: metadata.Id,
            versionNumber: 1,
            createdByUserId: ownerUserId,
            now: now,
            ct)
            .ConfigureAwait(false);

        await _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: SecretAuditEventTypes.Write,
                Reference: metadata.ToRef(),
                ActorUserId: ownerUserId,
                VersionNumber: 1,
                Outcome: SecretAuditOutcome.Success,
                Detail: null,
                OccurredAt: now),
            ct)
            .ConfigureAwait(false);

        return new RevealTokenIssueResult(
            Metadata: activatedMetadata,
            RevealToken: rawToken,
            ExpiresAt: expiresAt);
    }

    /// <inheritdoc />
    public async Task<RevealTokenIssueResult> IssueRotateAsync(
        Guid secretId,
        string newPlaintext,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPlaintext))
        {
            throw new ArgumentException(
                "New plaintext must be non-empty for rotation.",
                nameof(newPlaintext));
        }

        var now = _timeProvider.GetUtcNow();
        SecretMetadata rotatedMetadata;
        int newVersion;

        await using (var secretsCtx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var row = await secretsCtx.Secrets
                .FirstOrDefaultAsync(s => s.Id == secretId, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"No secret matches id={secretId}.");

            newVersion = row.ActiveVersionNumber + 1;
            var currentMetadata = ProjectMetadata(row);

            await _auditor.EmitAsync(
                new SecretAuditEvent(
                    EventType: SecretAuditEventTypes.RotateStarted,
                    Reference: currentMetadata.ToRef(),
                    ActorUserId: actorUserId,
                    VersionNumber: newVersion,
                    Outcome: SecretAuditOutcome.Success,
                    Detail: null,
                    OccurredAt: now),
                ct)
                .ConfigureAwait(false);

            try
            {
                await _backend.PutVersionAsync(
                    secretId, newVersion, newPlaintext, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _auditor.EmitAsync(
                    new SecretAuditEvent(
                        EventType: SecretAuditEventTypes.RotateFailed,
                        Reference: currentMetadata.ToRef(),
                        ActorUserId: actorUserId,
                        VersionNumber: newVersion,
                        Outcome: SecretAuditOutcome.Failure,
                        Detail: "backend_putversion_failed",
                        OccurredAt: now),
                    ct)
                    .ConfigureAwait(false);
                throw;
            }

            var newVersionRow = await secretsCtx.SecretVersions
                .FirstOrDefaultAsync(
                    v => v.SecretId == secretId && v.VersionNumber == newVersion, ct)
                .ConfigureAwait(false);
            if (newVersionRow is not null)
            {
                newVersionRow.Status = "active";
                newVersionRow.ActivatedAt = now.UtcDateTime;
                newVersionRow.CreatedByUserId = actorUserId;
            }

            var prevActive = await secretsCtx.SecretVersions
                .FirstOrDefaultAsync(
                    v => v.SecretId == secretId
                         && v.VersionNumber == row.ActiveVersionNumber
                         && v.Status == "active", ct)
                .ConfigureAwait(false);
            if (prevActive is not null)
            {
                prevActive.Status = "retired_grace";
                prevActive.RetiredAt = now.UtcDateTime;
            }

            row.ActiveVersionNumber = newVersion;
            row.LastRotatedAt = now.UtcDateTime;
            row.UpdatedAt = now.UtcDateTime;

            await secretsCtx.SaveChangesAsync(ct).ConfigureAwait(false);

            rotatedMetadata = SecretMetadataFactory.WithRotation(
                currentMetadata, newVersion, now);
        }

        var (rawToken, expiresAt) = await IssueTokenAsync(
            secretId: secretId,
            versionNumber: newVersion,
            createdByUserId: actorUserId,
            now: now,
            ct)
            .ConfigureAwait(false);

        await _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: SecretAuditEventTypes.RotateSucceeded,
                Reference: rotatedMetadata.ToRef(),
                ActorUserId: actorUserId,
                VersionNumber: newVersion,
                Outcome: SecretAuditOutcome.Success,
                Detail: null,
                OccurredAt: now),
            ct)
            .ConfigureAwait(false);

        return new RevealTokenIssueResult(
            Metadata: rotatedMetadata,
            RevealToken: rawToken,
            ExpiresAt: expiresAt);
    }

    /// <inheritdoc />
    public async Task<RevealTokenConsumeResult> ConsumeAsync(
        string rawToken,
        RevealCallerContext caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        ArgumentNullException.ThrowIfNull(caller);

        var now = _timeProvider.GetUtcNow();

        byte[] hash;
        try
        {
            hash = HashToken(rawToken);
        }
        catch (FormatException)
        {
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.NotFound,
                SecretId: null, VersionNumber: null, SecretName: null,
                Plaintext: null, ExpiresAt: null);
        }

        await using var revealCtx = await _revealFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var tokenRow = await revealCtx.RevealTokens
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct)
            .ConfigureAwait(false);

        if (tokenRow is null)
        {
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.NotFound,
                SecretId: null, VersionNumber: null, SecretName: null,
                Plaintext: null, ExpiresAt: null);
        }

        var expiresAt = new DateTimeOffset(
            DateTime.SpecifyKind(tokenRow.ExpiresAt, DateTimeKind.Utc));

        if (tokenRow.Status == "consumed")
        {
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.AlreadyConsumed,
                SecretId: tokenRow.SecretId,
                VersionNumber: tokenRow.VersionNumber,
                SecretName: null, Plaintext: null,
                ExpiresAt: expiresAt);
        }

        if (tokenRow.Status == "expired" || tokenRow.ExpiresAt <= now.UtcDateTime)
        {
            if (tokenRow.Status != "expired")
            {
                tokenRow.Status = "expired";
                try
                {
                    await revealCtx.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException)
                {
                }
            }
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.Expired,
                SecretId: tokenRow.SecretId,
                VersionNumber: tokenRow.VersionNumber,
                SecretName: null, Plaintext: null,
                ExpiresAt: expiresAt);
        }

        tokenRow.Status = "consumed";
        tokenRow.ConsumedAt = now.UtcDateTime;
        tokenRow.ConsumedUserAgent = TruncateUserAgent(caller.UserAgent);
        tokenRow.ConsumedIpHash = HashIpOrNull(caller.RemoteIp);

        try
        {
            await revealCtx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.AlreadyConsumed,
                SecretId: tokenRow.SecretId,
                VersionNumber: tokenRow.VersionNumber,
                SecretName: null, Plaintext: null,
                ExpiresAt: expiresAt);
        }

        SecretMetadata? metadata = null;
        await using (var secretsCtx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var secretRow = await secretsCtx.Secrets
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == tokenRow.SecretId, ct)
                .ConfigureAwait(false);
            if (secretRow is not null)
            {
                metadata = ProjectMetadata(secretRow);
            }
        }

        var plaintext = await _backend.GetVersionPlaintextAsync(
            tokenRow.SecretId, tokenRow.VersionNumber, ct)
            .ConfigureAwait(false);

        if (plaintext is null)
        {
            await _auditor.EmitAsync(
                new SecretAuditEvent(
                    EventType: SecretAuditEventTypes.Read,
                    Reference: metadata?.ToRef() ?? SecretRef.ForPlatform("unknown"),
                    ActorUserId: tokenRow.CreatedByUserId,
                    VersionNumber: tokenRow.VersionNumber,
                    Outcome: SecretAuditOutcome.Failure,
                    Detail: "version_scrubbed",
                    OccurredAt: now),
                ct)
                .ConfigureAwait(false);
            return new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.Expired,
                SecretId: tokenRow.SecretId,
                VersionNumber: tokenRow.VersionNumber,
                SecretName: metadata?.Name,
                Plaintext: null,
                ExpiresAt: expiresAt);
        }

        var auditRef = metadata?.ToRef() ?? SecretRef.ForPlatform("unknown");
        await _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: SecretAuditEventTypes.Reveal,
                Reference: auditRef,
                ActorUserId: tokenRow.CreatedByUserId,
                VersionNumber: tokenRow.VersionNumber,
                Outcome: SecretAuditOutcome.Success,
                Detail: FormatRevealDetail(tokenRow),
                OccurredAt: now),
            ct)
            .ConfigureAwait(false);

        return new RevealTokenConsumeResult(
            RevealTokenConsumeOutcome.Success,
            SecretId: tokenRow.SecretId,
            VersionNumber: tokenRow.VersionNumber,
            SecretName: metadata?.Name,
            Plaintext: plaintext,
            ExpiresAt: expiresAt);
    }

    /// <inheritdoc />
    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await using var ctx = await _revealFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var expiredRows = await ctx.RevealTokens
            .Where(r => r.Status == "unused" && r.ExpiresAt <= nowUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (expiredRows.Count == 0) return 0;

        foreach (var row in expiredRows)
        {
            row.Status = "expired";
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Reveal sweeper flipped {Count} rows to status=expired",
            expiredRows.Count);
        return expiredRows.Count;
    }

    // ─────────────────────────────────────────────────────────────────

    private async Task PersistSecretRowAsync(
        SecretMetadata metadata, CancellationToken ct)
    {
        await using var secretsCtx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = new SecretRow
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Scope = metadata.Scope.ToString().ToLowerInvariant(),
            TenantId = metadata.TenantId,
            Purpose = metadata.Purpose.ToString(),
            OwnerUserId = metadata.OwnerUserId,
            ActiveVersionNumber = 0,
            LastRotatedAt = null,
            NextRotationDueAt = metadata.NextRotationDueAt?.UtcDateTime,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            UpdatedAt = metadata.UpdatedAt.UtcDateTime,
            ConsumerRefsJson = SerializeConsumers(metadata.ConsumerRefs),
            RotationScheduleJson = SerializeSchedule(metadata.RotationSchedule),
        };
        secretsCtx.Secrets.Add(row);
        await secretsCtx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task ActivateFirstVersionAsync(
        Guid secretId, Guid actorUserId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var secretsCtx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var versionRow = await secretsCtx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == 1, ct)
            .ConfigureAwait(false);
        if (versionRow is not null)
        {
            versionRow.Status = "active";
            versionRow.ActivatedAt = now;
            versionRow.CreatedByUserId = actorUserId;
        }

        var secretRow = await secretsCtx.Secrets
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secretRow is not null)
        {
            secretRow.ActiveVersionNumber = 1;
            secretRow.LastRotatedAt = now;
            secretRow.UpdatedAt = now;
        }

        await secretsCtx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<(string RawToken, DateTimeOffset ExpiresAt)> IssueTokenAsync(
        Guid secretId,
        int versionNumber,
        Guid createdByUserId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(TokenBytesLength);
        var rawToken = Base64UrlEncode(rawBytes);
        var hash = HashToken(rawToken);
        var expiresAt = now.Add(TokenTtl);

        await using var ctx = await _revealFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        ctx.RevealTokens.Add(new SecretRevealTokenRow
        {
            Id = Guid.NewGuid(),
            TokenHash = hash,
            SecretId = secretId,
            VersionNumber = versionNumber,
            CreatedByUserId = createdByUserId,
            CreatedAt = now.UtcDateTime,
            ExpiresAt = expiresAt.UtcDateTime,
            Status = "unused",
        });

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        return (rawToken, expiresAt);
    }

    /// <summary>
    /// HMAC-SHA256 the raw base64url token under the Story 29-2
    /// primary KEK.
    /// </summary>
    private byte[] HashToken(string rawToken)
    {
        var tokenBytes = Base64UrlDecode(rawToken);
        var kekId = _kekProvider.PrimaryKekId;
        var kek = _kekProvider.GetKek(kekId);
        try
        {
            using var hmac = new HMACSHA256(kek);
            return hmac.ComputeHash(tokenBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 0: break;
            default: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(padded);
    }

    private static string? TruncateUserAgent(string? ua) =>
        string.IsNullOrEmpty(ua) ? null : (ua.Length <= 512 ? ua : ua[..512]);

    private static string? HashIpOrNull(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string FormatRevealDetail(SecretRevealTokenRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(row.ConsumedUserAgent))
            parts.Add($"ua={row.ConsumedUserAgent}");
        if (!string.IsNullOrEmpty(row.ConsumedIpHash))
            parts.Add($"ipHash={row.ConsumedIpHash}");
        return parts.Count == 0 ? "reveal_token_consumed" : string.Join("; ", parts);
    }

    private static string SerializeConsumers(IReadOnlyList<ConsumerRef> consumers) =>
        consumers.Count == 0 ? "[]" : System.Text.Json.JsonSerializer.Serialize(consumers);

    private static string SerializeSchedule(RotationSchedule schedule) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Kind = schedule.Kind.ToString(),
            schedule.Days,
            schedule.CronExpression,
        });

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
