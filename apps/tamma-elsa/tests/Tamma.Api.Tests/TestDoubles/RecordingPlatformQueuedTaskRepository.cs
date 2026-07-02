using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Story 34-4 — recording double for <see cref="IPlatformQueuedTaskRepository"/>
/// that captures every enqueued task (so tests can assert the boundary-activation
/// task was queued) and is otherwise a no-op. The drain-side methods are not
/// exercised by the assignment-service unit tests.
/// </summary>
internal sealed class RecordingPlatformQueuedTaskRepository : IPlatformQueuedTaskRepository
{
    public List<PlatformQueuedTask> Enqueued { get; } = new();

    public Task<PlatformQueuedTask> EnqueueAsync(PlatformQueuedTask task, CancellationToken ct = default)
    {
        if (task.Id == Guid.Empty) task.Id = Guid.NewGuid();
        if (task.CreatedAt == default) task.CreatedAt = DateTime.UtcNow;
        Enqueued.Add(task);
        return Task.FromResult(task);
    }

    public Task<PlatformQueuedTask?> ReserveNextAsync(string workerId, CancellationToken ct = default)
        => Task.FromResult<PlatformQueuedTask?>(null);

    public Task CompleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PlatformQueuedTask?> FailAsync(Guid id, string error, int maxRetries, CancellationToken ct = default)
        => Task.FromResult<PlatformQueuedTask?>(null);

    public Task DeadLetterAsync(Guid id, string error, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PlatformQueuedTask?> ParkUnprocessableAsync(Guid id, string reason, int maxRetries, CancellationToken ct = default)
        => Task.FromResult<PlatformQueuedTask?>(null);

    public Task DeferAsync(Guid id, DateTime visibleAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PlatformQueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<PlatformQueuedTask?>(Enqueued.FirstOrDefault(t => t.Id == id));

    public Task<int> ReapStaleProcessingAsync(TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
        => Task.FromResult(0);
}
