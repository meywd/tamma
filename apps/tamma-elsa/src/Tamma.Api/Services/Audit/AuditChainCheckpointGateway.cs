using Microsoft.EntityFrameworkCore;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC5/AC7) — the <see cref="IAuditChainCheckpointGateway"/> the
/// verifier confirms anchors through. Checkpoint rows are ALWAYS control-plane
/// resident (for platform AND tenant scopes), so this reads only the CP context;
/// signature validation delegates to <see cref="IAuditChainSigner"/> (cabinet key).
/// </summary>
public sealed class AuditChainCheckpointGateway : IAuditChainCheckpointGateway
{
    private readonly ControlPlaneDbContext _cp;
    private readonly IAuditChainSigner _signer;

    public AuditChainCheckpointGateway(ControlPlaneDbContext cp, IAuditChainSigner signer)
    {
        _cp = cp ?? throw new ArgumentNullException(nameof(cp));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    public async Task<AuditChainCheckpointView?> GetLastCoveringAsync(
        AuditChainScope scope, long? to, CancellationToken ct)
    {
        var discriminator = scope.Discriminator;
        var tenantId = scope.Kind == AuditChainScopeKind.Tenant ? scope.TenantId : null;

        var q = _cp.AuditChainCheckpoints.AsNoTracking()
            .Where(c => c.Scope == discriminator && c.TenantId == tenantId);
        if (to is long t) q = q.Where(c => c.HeadSequence <= t);

        var row = await q.OrderByDescending(c => c.HeadSequence)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : ToView(row);
    }

    public Task<bool> VerifySignatureAsync(AuditChainCheckpointView checkpoint, CancellationToken ct) =>
        _signer.VerifyAsync(checkpoint, ct);

    public async Task<long?> GetMaxHeadSequenceAsync(AuditChainScope scope, CancellationToken ct)
    {
        var discriminator = scope.Discriminator;
        var tenantId = scope.Kind == AuditChainScopeKind.Tenant ? scope.TenantId : null;

        // Max<long?> over an empty set is null (no checkpoints for this scope yet).
        return await _cp.AuditChainCheckpoints.AsNoTracking()
            .Where(c => c.Scope == discriminator && c.TenantId == tenantId)
            .MaxAsync(c => (long?)c.HeadSequence, ct)
            .ConfigureAwait(false);
    }

    internal static AuditChainCheckpointView ToView(AuditChainCheckpoint row) =>
        new()
        {
            Id = row.Id,
            Scope = row.Scope,
            TenantId = row.TenantId,
            HeadSequence = row.HeadSequence,
            HeadHash = row.HeadHash,
            SignedAt = row.SignedAt,
            Signature = row.Signature,
            KeyVersion = row.KeyVersion,
        };
}
