using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PlatformTasks;

/// <summary>
/// Options for <see cref="PlatformTaskWorker"/>. Bound to the
/// <c>PlatformTaskWorker</c> configuration section.
/// </summary>
public sealed class PlatformTaskWorkerOptions
{
    /// <summary>Configuration section name for binding.</summary>
    public const string SectionName = "PlatformTaskWorker";

    /// <summary>How often the worker polls for new tasks. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Stable id for the worker — recorded on the row's lease
    /// for ops visibility. Defaults to <c>{machine}-{pid}</c>.</summary>
    public string WorkerId { get; set; } =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>Max retries before a task moves to <c>dead_letter</c>.
    /// Default 5.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Visibility timeout for stuck-in-processing rows. Reaper
    /// runs once per <see cref="ReaperInterval"/>. Default 10 minutes.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the visibility-timeout reaper runs. Default
    /// 1 minute. The reaper is independent of the poll loop because
    /// re-claiming a stuck row should happen even when the queue is
    /// otherwise idle.</summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// When <c>true</c> (default) the worker's polling loop runs at
    /// host startup. Tests that drive <see cref="PlatformTaskWorker.ProcessOnceAsync"/>
    /// directly (or don't exercise the worker at all) override this to
    /// <c>false</c> to keep the test suite fast.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Story 28-6 AC5 — background service that drains
/// <c>platform_queued_tasks</c> rows. Each tick:
/// <list type="number">
///   <item><description>Calls <see cref="IPlatformQueuedTaskRepository.ReserveNextAsync"/>
///     to claim one task atomically (Postgres
///     <c>FOR UPDATE SKIP LOCKED</c> on the production path).</description></item>
///   <item><description>Resolves the handler via
///     <see cref="IPlatformTaskHandlerRegistry"/>.</description></item>
///   <item><description>Invokes <see cref="IPlatformTaskHandler.HandleAsync"/>.
///     On success: <c>CompleteAsync</c>. On exception:
///     <c>FailAsync</c> with retry, OR <c>DeadLetterAsync</c> when the
///     handler raised <see cref="PlatformTaskTerminalException"/> /
///     no handler is registered.</description></item>
///   <item><description>Periodically (every
///     <see cref="PlatformTaskWorkerOptions.ReaperInterval"/>) calls
///     <see cref="IPlatformQueuedTaskRepository.ReapStaleProcessingAsync"/>
///     to recover from worker crashes mid-task.</description></item>
/// </list>
///
/// <para><b>Concurrency</b>: multiple worker instances across pods are
/// safe — the repository's reservation primitive uses
/// <c>FOR UPDATE SKIP LOCKED</c> so each row is claimed by exactly one
/// caller. The worker is also one task at a time per process; a future
/// improvement could add bounded parallelism, but the current shape
/// keeps ordering predictable for handlers that touch shared state.</para>
///
/// <para><b>Test gating</b>: set <c>PlatformTaskWorker:RunOnStartup =
/// false</c> in test fixtures so the BackgroundService doesn't poll
/// during unit tests; <see cref="ProcessOnceAsync"/> is the testable
/// drive-once entry point.</para>
/// </summary>
public sealed class PlatformTaskWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly PlatformTaskWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlatformTaskWorker> _logger;

    private DateTimeOffset _lastReaperRun = DateTimeOffset.MinValue;

    public PlatformTaskWorker(
        IServiceProvider services,
        IOptions<PlatformTaskWorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<PlatformTaskWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "PlatformTaskWorker gated off (RunOnStartup=false); polling loop will not start.");
            return;
        }

        _logger.LogInformation(
            "PlatformTaskWorker starting workerId={WorkerId} poll={Interval}s reaper={Reaper}s maxRetries={MaxRetries}",
            _options.WorkerId,
            _options.PollInterval.TotalSeconds,
            _options.ReaperInterval.TotalSeconds,
            _options.MaxRetries);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
                await MaybeReapAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PlatformTaskWorker tick threw; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("PlatformTaskWorker shut down.");
    }

    /// <summary>
    /// Reserve and process a single task. Returns <c>true</c> when one
    /// was processed (success, retry, or dead-letter), <c>false</c>
    /// when the queue was empty.
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformQueuedTaskRepository>();
        var registry = scope.ServiceProvider
            .GetRequiredService<IPlatformTaskHandlerRegistry>();

        var task = await repo.ReserveNextAsync(_options.WorkerId, ct)
            .ConfigureAwait(false);
        if (task is null) return false;

        var handler = registry.Resolve(task.Type);
        if (handler is null)
        {
            _logger.LogWarning(
                "platform_task.no_handler taskId={TaskId} type={TaskType}",
                task.Id,
                task.Type);
            await repo.DeadLetterAsync(
                task.Id,
                $"No IPlatformTaskHandler registered for task type '{task.Type}'.",
                ct).ConfigureAwait(false);
            return true;
        }

        try
        {
            await handler.HandleAsync(task, ct).ConfigureAwait(false);
            await repo.CompleteAsync(task.Id, ct).ConfigureAwait(false);
            _logger.LogDebug(
                "platform_task.completed taskId={TaskId} type={TaskType}",
                task.Id,
                task.Type);
        }
        catch (PlatformTaskTerminalException ex)
        {
            _logger.LogWarning(ex,
                "platform_task.terminal taskId={TaskId} type={TaskType}",
                task.Id,
                task.Type);
            await repo.DeadLetterAsync(task.Id, ex.Message, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Process is shutting down — leave the row in 'processing'
            // for the visibility-timeout reaper to recover. Don't
            // record a retry against the row's count.
            _logger.LogInformation(
                "platform_task.cancelled taskId={TaskId} type={TaskType} (reaper will recover)",
                task.Id,
                task.Type);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "platform_task.failed taskId={TaskId} type={TaskType} retry={RetryCount}",
                task.Id,
                task.Type,
                task.RetryCount);
            // FailAsync handles retry-vs-dead-letter via maxRetries.
            await repo.FailAsync(
                task.Id,
                $"{ex.GetType().Name}: {ex.Message}",
                _options.MaxRetries,
                ct).ConfigureAwait(false);
        }
        return true;
    }

    private async Task MaybeReapAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastReaperRun < _options.ReaperInterval) return;
        _lastReaperRun = now;

        await using var scope = _services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformQueuedTaskRepository>();
        var reaped = await repo.ReapStaleProcessingAsync(
            _options.VisibilityTimeout, _options.MaxRetries, ct)
            .ConfigureAwait(false);
        if (reaped > 0)
        {
            _logger.LogInformation(
                "platform_task.reaper.recovered count={Count} timeout={Timeout}s",
                reaped,
                (int)_options.VisibilityTimeout.TotalSeconds);
        }
    }
}
