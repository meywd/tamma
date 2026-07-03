using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Audit;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 — the <see cref="IAuditChainRecordSource"/> the verifier reads
/// through. Resolves the correct physical store per scope: the control-plane
/// context for <see cref="AuditChainScopeKind.Platform"/> (the platform /
/// single-user chain) and the tenant's own schema (via
/// <see cref="ITenantDbContextFactory"/>) for
/// <see cref="AuditChainScopeKind.Tenant"/>. Streams ascending by
/// <c>chain_sequence</c> and never materializes the whole chain (AC4/AC12).
/// </summary>
public sealed class AuditChainRecordSource : IAuditChainRecordSource
{
    private readonly ControlPlaneDbContext _cp;
    private readonly ITenantDbContextFactory? _tenantFactory;

    public AuditChainRecordSource(
        ControlPlaneDbContext cp, ITenantDbContextFactory? tenantFactory = null)
    {
        _cp = cp ?? throw new ArgumentNullException(nameof(cp));
        _tenantFactory = tenantFactory;
    }

    public async IAsyncEnumerable<AuditChainRecordView> StreamAsync(
        AuditChainScope scope, long? from, long? to,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (scope.Kind == AuditChainScopeKind.Tenant)
        {
            if (_tenantFactory is null || scope.TenantId is not Guid tid)
            {
                yield break;
            }
            await using var tenantCtx = await _tenantFactory.CreateAsync(tid, ct)
                .ConfigureAwait(false);
            await foreach (var v in QueryAsync(tenantCtx, scope, from, to, ct)
                .ConfigureAwait(false))
            {
                yield return v;
            }
            yield break;
        }

        await foreach (var v in QueryAsync(_cp, scope, from, to, ct).ConfigureAwait(false))
        {
            yield return v;
        }
    }

    public async Task<string?> GetRecordHashAtAsync(
        AuditChainScope scope, long sequence, CancellationToken ct)
    {
        if (scope.Kind == AuditChainScopeKind.Tenant)
        {
            if (_tenantFactory is null || scope.TenantId is not Guid tid) return null;
            await using var tenantCtx = await _tenantFactory.CreateAsync(tid, ct)
                .ConfigureAwait(false);
            return await HashAtAsync(tenantCtx, sequence, ct).ConfigureAwait(false);
        }
        return await HashAtAsync(_cp, sequence, ct).ConfigureAwait(false);
    }

    public async Task<AuditChainHead?> GetHeadAsync(AuditChainScope scope, CancellationToken ct)
    {
        if (scope.Kind == AuditChainScopeKind.Tenant)
        {
            if (_tenantFactory is null || scope.TenantId is not Guid tid) return null;
            await using var tenantCtx = await _tenantFactory.CreateAsync(tid, ct)
                .ConfigureAwait(false);
            return await HeadAsync(tenantCtx, ct).ConfigureAwait(false);
        }
        return await HeadAsync(_cp, ct).ConfigureAwait(false);
    }

    private static async Task<AuditChainHead?> HeadAsync(DbContext ctx, CancellationToken ct)
    {
        var head = await ctx.Set<AuditRecord>().AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.ChainSequence != null)
            .OrderByDescending(r => r.ChainSequence)
            .Select(r => new { r.ChainSequence, r.RecordHash })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return head?.ChainSequence is long seq && head.RecordHash is not null
            ? new AuditChainHead(seq, head.RecordHash)
            : null;
    }

    private static async IAsyncEnumerable<AuditChainRecordView> QueryAsync(
        DbContext ctx, AuditChainScope scope, long? from, long? to,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IQueryable<AuditRecord> q = ctx.Set<AuditRecord>().AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.ChainSequence != null);
        if (from is long f) q = q.Where(r => r.ChainSequence >= f);
        if (to is long t) q = q.Where(r => r.ChainSequence <= t);
        q = q.OrderBy(r => r.ChainSequence);

        await foreach (var r in q.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return AuditRecordChainMapper.ToView(r, scope);
        }
    }

    private static async Task<string?> HashAtAsync(
        DbContext ctx, long sequence, CancellationToken ct) =>
        await ctx.Set<AuditRecord>().AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.ChainSequence == sequence)
            .Select(r => r.RecordHash)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
