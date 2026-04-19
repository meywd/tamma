using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Configuration knobs for <see cref="TaskQueueProcessor"/>. Bound by
/// <c>AddTaskQueue</c> from configuration or supplied directly by tests.
/// </summary>
public sealed class TaskQueueProcessorOptions
{
    /// <summary>How often the processor polls for pending tasks. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many rows to pull per poll. Default 10.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Number of attempts before a task is marked failed. The initial attempt
    /// counts, so <c>MaxRetries = 3</c> means: try, fail → requeue, try, fail →
    /// requeue, try, fail → flip to <c>failed</c>. Default 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Audit finding 026 — visibility timeout. Rows that stay in
    /// <c>processing</c> longer than this are presumed orphaned by a worker
    /// that died between MarkProcessingAsync and MarkCompletedAsync, and the
    /// reaper resets them to <c>pending</c> (or <c>failed</c> when the retry
    /// budget is exhausted). Default 10 minutes.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Hosted service that polls <see cref="IQueuedTaskRepository"/> for pending
/// tasks, dispatches each via a registered <see cref="ITaskHandler"/>, and
/// records the outcome on the row.
///
/// <para>Ported from the deleted TypeScript <c>InMemoryTaskQueue</c> plus the
/// implicit dispatcher that lived in the webhook handler. Retry policy is
/// bounded by <see cref="TaskQueueProcessorOptions.MaxRetries"/>; each failure
/// increments <see cref="QueuedTask.RetryCount"/> and re-queues, until the
/// ceiling flips the row to <c>failed</c>.</para>
/// </summary>
public sealed class TaskQueueProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TaskQueueProcessorOptions _options;
    private readonly ILogger<TaskQueueProcessor> _logger;

    public TaskQueueProcessor(
        IServiceProvider serviceProvider,
        TaskQueueProcessorOptions options,
        ILogger<TaskQueueProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TaskQueueProcessor started. Poll interval={Interval}, batch={Batch}, max retries={Retries}",
            _options.PollInterval, _options.BatchSize, _options.MaxRetries);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOnceAsync(stoppingToken);
                if (processed > 0)
                {
                    _logger.LogDebug("TaskQueueProcessor processed {Count} tasks this cycle", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TaskQueueProcessor cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("TaskQueueProcessor stopped");
    }

    /// <summary>
    /// Exposed for tests — runs a single poll cycle and returns the number of
    /// tasks the processor attempted (completed, requeued, or marked failed).
    /// </summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        // Fresh scope per poll so scoped services (DbContext, repositories)
        // don't leak across cycles.
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IQueuedTaskRepository>();
        var registry = scope.ServiceProvider.GetRequiredService<ITaskHandlerRegistry>();

        // Audit finding 026 — reap orphaned `processing` rows before claiming
        // new work, so a worker that died mid-task does not leave the row
        // stuck forever. Exceptions here are logged but do not abort the
        // poll; reaping is best-effort.
        try
        {
            var reaped = await repo.ReapStaleProcessingAsync(
                _options.VisibilityTimeout, _options.MaxRetries, ct);
            if (reaped > 0)
            {
                _logger.LogWarning(
                    "TaskQueueProcessor reaped {Count} stale processing rows " +
                    "(visibility timeout={Timeout})",
                    reaped, _options.VisibilityTimeout);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TaskQueueProcessor reaper pass failed");
        }

        var pending = await repo.ListPendingAsync(tenantId: null, _options.BatchSize, ct);
        if (pending.Count == 0) return 0;

        var processed = 0;
        foreach (var task in pending)
        {
            if (ct.IsCancellationRequested) break;

            var claimed = await repo.MarkProcessingAsync(task.Id, ct);
            if (claimed is null)
            {
                // Someone else claimed it between list + mark; move on.
                continue;
            }

            var handler = registry.ResolveFor(claimed.Type);
            if (handler is null)
            {
                await repo.MarkFailedAsync(
                    claimed.Id,
                    $"no handler registered for task type '{claimed.Type}'",
                    ct);
                processed++;
                continue;
            }

            try
            {
                await handler.HandleAsync(claimed, ct);
                await repo.MarkCompletedAsync(claimed.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await HandleFailureAsync(repo, claimed, ex, ct);
            }

            processed++;
        }

        return processed;
    }

    private async Task HandleFailureAsync(
        IQueuedTaskRepository repo,
        QueuedTask claimed,
        Exception ex,
        CancellationToken ct)
    {
        // RetryCount is bumped on every failure — even the terminal one — so the
        // persisted row tells ops exactly how many attempts the task consumed.
        var requeued = await repo.IncrementRetryAndRequeueAsync(claimed.Id, ex.Message, ct);
        var retryCount = requeued?.RetryCount ?? claimed.RetryCount + 1;

        if (retryCount >= _options.MaxRetries)
        {
            _logger.LogError(ex,
                "Task {TaskId} ({Type}) failed after {Attempts} attempts — marking failed",
                claimed.Id, claimed.Type, retryCount);
            await repo.MarkFailedAsync(claimed.Id, ex.Message, ct);
            return;
        }

        _logger.LogWarning(ex,
            "Task {TaskId} ({Type}) failed (attempt {Attempts}/{Max}) — requeuing",
            claimed.Id, claimed.Type, retryCount, _options.MaxRetries);
    }
}
