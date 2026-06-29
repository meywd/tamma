using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Database-backed <see cref="ITaskQueue"/>. Strictly tenant-scoped:
/// derives the tenant from the ambient <see cref="ITenantContext"/>
/// unless an explicit override is supplied (used by the GitHub webhook
/// handler that derives tenancy from the installation id, not the
/// authenticated user).
///
/// <para>Story 28-1 PR B — platform-scope tasks no longer flow through
/// here. The Cranl provisioning walk and <c>RetireScheduler</c> hit
/// <see cref="IPlatformQueuedTaskRepository"/> directly; the GitHub
/// webhook handler routes orphan webhooks to the platform repo too.
/// This surface ONLY enqueues into a real tenant's per-tenant queue.</para>
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
        if (tenantId is not Guid tid || tid == Guid.Empty)
        {
            throw new InvalidOperationException(
                "DbTaskQueue.EnqueueAsync requires a tenant id. Set " +
                "ITenantContext or pass tenantIdOverride. Platform-scope " +
                "callers must use IPlatformQueuedTaskRepository directly.");
        }

        var task = new QueuedTask
        {
            Type = type,
            TenantId = tid,
            InstallationId = installationId,
            Payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson
        };

        return _repo.EnqueueAsync(task, ct);
    }

    public Task<QueuedTask?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => _repo.GetAsync(tenantId, id, ct);

    public Task<List<QueuedTask>> ListPendingAsync(Guid tenantId, int limit = 20, CancellationToken ct = default)
        => _repo.ListPendingAsync(tenantId, limit, ct);

    public Task<QueuedTask?> MarkProcessingAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => _repo.MarkProcessingAsync(tenantId, id, ct);

    public Task MarkCompletedAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => _repo.MarkCompletedAsync(tenantId, id, ct);

    public Task MarkFailedAsync(Guid tenantId, Guid id, string error, CancellationToken ct = default)
        => _repo.MarkFailedAsync(tenantId, id, error, ct);
}
