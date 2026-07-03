using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC5/AC6) — writes signed chain checkpoints. Reads a scope's
/// current head, signs the canonical anchor with the cabinet key, and persists a
/// CP-resident <c>audit_chain_checkpoints</c> row, then emits
/// <c>AUDIT.CHAIN.CHECKPOINTED</c>. Used both on demand (admin endpoint) and by
/// the scheduled workflow (one checkpoint per active scope).
/// </summary>
public sealed class AuditChainCheckpointService : IAuditChainCheckpointService
{
    private readonly ControlPlaneDbContext _cp;
    private readonly IAuditChainRecordSource _records;
    private readonly IAuditChainSigner _signer;
    private readonly IAuditChainEventEmitter _emitter;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditChainCheckpointService> _logger;

    public AuditChainCheckpointService(
        ControlPlaneDbContext cp,
        IAuditChainRecordSource records,
        IAuditChainSigner signer,
        IAuditChainEventEmitter emitter,
        TimeProvider clock,
        ILogger<AuditChainCheckpointService> logger)
    {
        _cp = cp ?? throw new ArgumentNullException(nameof(cp));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AuditChainCheckpoint?> WriteCheckpointAsync(
        AuditChainScope scope, CancellationToken ct = default)
    {
        var head = await _records.GetHeadAsync(scope, ct).ConfigureAwait(false);
        if (head is null)
        {
            _logger.LogInformation(
                "audit.chain.checkpoint.skipped_empty scope={Scope} tenantId={TenantId}",
                scope.Discriminator, scope.TenantId);
            return null; // nothing to anchor yet
        }

        var signedAt = _clock.GetUtcNow().UtcDateTime;
        var (signature, keyVersion) = await _signer.SignAsync(
            scope.Discriminator, scope.TenantId, head.Sequence, head.RecordHash, signedAt, ct)
            .ConfigureAwait(false);

        var checkpoint = new AuditChainCheckpoint
        {
            Id = Guid.NewGuid(),
            Scope = scope.Discriminator,
            TenantId = scope.Kind == AuditChainScopeKind.Tenant ? scope.TenantId : null,
            HeadSequence = head.Sequence,
            HeadHash = head.RecordHash,
            SignedAt = signedAt,
            Signature = signature,
            KeyVersion = keyVersion,
            CreatedAt = signedAt,
        };
        _cp.AuditChainCheckpoints.Add(checkpoint);
        await _cp.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "audit.chain.checkpoint.written scope={Scope} tenantId={TenantId} "
            + "headSequence={HeadSequence} keyVersion={KeyVersion}",
            scope.Discriminator, scope.TenantId, head.Sequence, keyVersion);

        await _emitter.EmitCheckpointedAsync(scope, head.Sequence, keyVersion, ct)
            .ConfigureAwait(false);
        return checkpoint;
    }

    public async Task<int> WriteAllActiveScopesAsync(CancellationToken ct = default)
    {
        var written = 0;

        // Platform / single-user chain (CP-resident audit_records).
        try
        {
            if (await WriteCheckpointAsync(AuditChainScope.Platform, ct).ConfigureAwait(false) is not null)
            {
                written++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "audit.chain.checkpoint.platform_failed — continuing.");
        }

        // Each active tenant's chain.
        var tenantIds = await _cp.Tenants.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var tid in tenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await WriteCheckpointAsync(AuditChainScope.ForTenant(tid), ct)
                    .ConfigureAwait(false) is not null)
                {
                    written++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "audit.chain.checkpoint.tenant_failed tenantId={TenantId} — continuing.", tid);
            }
        }

        return written;
    }
}
