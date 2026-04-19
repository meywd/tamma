using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Base class for <see cref="ITaskHandler"/> implementations that enforces
/// per-task tenant scoping before the concrete handler logic runs.
///
/// <para>Audit finding 027: <see cref="TaskQueueProcessor"/> is architected
/// as a system-scope consumer — it polls every tenant's pending tasks and
/// dispatches them on a single shared thread. This is fine, but each
/// handler MUST set the ambient <see cref="ITenantContext"/> from the
/// claimed task's <see cref="QueuedTask.TenantId"/> before doing any
/// tenant-scoped DB work, otherwise the EF global query filters degrade
/// to "all tenants" and a handler bug becomes a cross-tenant data
/// hazard.</para>
///
/// <para>Inherit from this base instead of implementing
/// <see cref="ITaskHandler"/> directly to get the tenant scoping for
/// free. <see cref="HandleAsync"/> resolves <see cref="ITenantContext"/>
/// from the scoped service provider, sets it to the task's tenant, then
/// hands off to <see cref="HandleCoreAsync"/>.</para>
/// </summary>
public abstract class TaskHandlerBase : ITaskHandler
{
    private readonly IServiceProvider _services;

    protected TaskHandlerBase(IServiceProvider services)
    {
        _services = services;
    }

    public abstract string TypePrefix { get; }

    public async Task HandleAsync(QueuedTask task, CancellationToken ct)
    {
        var tenantContext = _services.GetService<ITenantContext>();
        if (tenantContext is not null && task.TenantId.HasValue)
        {
            tenantContext.SetTenantId(task.TenantId.Value);
        }
        try
        {
            await HandleCoreAsync(task, ct);
        }
        finally
        {
            tenantContext?.ClearTenantId();
        }
    }

    /// <summary>
    /// Handler-specific business logic. The ambient <see cref="ITenantContext"/>
    /// is already set to <c>task.TenantId</c> when this is invoked (when a
    /// tenant context exists in scope and the task carries a tenant).
    /// </summary>
    protected abstract Task HandleCoreAsync(QueuedTask task, CancellationToken ct);
}
