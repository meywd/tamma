using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Database-backed <see cref="ITaskQueue"/>. Derives tenant scoping from the
/// ambient <see cref="ITenantContext"/>; accepts explicit overrides for
/// sources where tenancy is resolved from something other than the caller
/// (notably the GitHub App webhook, which binds tenancy via installation ID).
///
/// <para>Ported from the deleted TypeScript in-memory queue, with the
/// behaviour of <c>requireInstallationId</c> folded into the webhook caller
/// rather than this service — tenant isolation here is enforced via the
/// ambient context, not the installationId field.</para>
/// </summary>
public sealed class DbTaskQueue : ITaskQueue
{
    private readonly IQueuedTaskRepository _repo;
    private readonly ITenantContext _tenantContext;

    public DbTaskQueue(IQueuedTaskRepository repo, ITenantContext tenantContext)
    {
        _repo = repo;
        _tenantContext = tenantContext;
    }

    public Task<QueuedTask> EnqueueAsync(
        string type,
        string payloadJson,
        long? installationId = null,
        Guid? tenantIdOverride = null,
        CancellationToken ct = default)
    {
        var tenantId = tenantIdOverride ?? _tenantContext.TenantId;

        var task = new QueuedTask
        {
            Type = type,
            TenantId = tenantId,
            InstallationId = installationId,
            Payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson
        };

        return _repo.EnqueueAsync(task, ct);
    }

    public Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
        => _repo.GetAsync(id, ct);

    public Task<List<QueuedTask>> ListPendingAsync(int limit = 20, CancellationToken ct = default)
        => _repo.ListPendingAsync(_tenantContext.TenantId, limit, ct);

    public Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default)
        => _repo.MarkProcessingAsync(id, ct);

    public Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
        => _repo.MarkCompletedAsync(id, ct);

    public Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
        => _repo.MarkFailedAsync(id, error, ct);
}
