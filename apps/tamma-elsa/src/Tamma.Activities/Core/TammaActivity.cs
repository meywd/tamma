using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Core;

// ============================================
// Event Model
// ============================================

/// <summary>
/// Event emitted by a Tamma activity.
/// </summary>
public class TammaEvent
{
    /// <summary>
    /// Stable per-event id minted at emit time (UUID v4 — net8 has no
    /// <c>Guid.CreateVersion7</c>). Carried through the wire DTO to
    /// <c>DomainEvent.Id</c> so the durable append is idempotent: a retry of an
    /// already-persisted event (the drain re-sends the whole pending slice on
    /// any non-2xx) is a no-op (<c>ON CONFLICT (Id) DO NOTHING</c>) instead of
    /// a duplicate audit row. Without this the at-least-once drain duplicates
    /// events whenever a batch partially fails (C2).
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;
    public string Status { get; set; } = "success";
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan? Duration { get; set; }
    public string? ActivityId { get; set; }
    public string? ActivityName { get; set; }
    public string? WorkflowInstanceId { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();

    /// <summary>
    /// Optional flexible JSONB-style tags (issueId / prId / userId / mode /
    /// provider / ...) carried onto the persisted <c>domain_events</c> row by
    /// the durable drain. Distinct from <see cref="Data"/> (the structured
    /// payload): tags are the queryable DCB index keys.
    /// </summary>
    public Dictionary<string, object?>? Tags { get; set; }
}

// ============================================
// Interface
// ============================================

/// <summary>
/// Interface for Tamma activities that emit lifecycle events.
/// Implement on any activity base class (CodeActivity, Activity, Composite).
/// </summary>
public interface ITammaActivity
{
    /// <summary>
    /// Event type prefix (e.g., "ADL.CONFIG.INIT").
    /// Return null to skip event emission.
    /// </summary>
    string? EventType { get; }

    /// <summary>
    /// Custom data for the start event.
    /// </summary>
    Dictionary<string, object?> BuildStartData(ActivityExecutionContext context);

    /// <summary>
    /// Custom data for the end event (success or failure).
    /// </summary>
    Dictionary<string, object?> BuildEndData(ActivityExecutionContext context);
}

// ============================================
// Event Emission Helper
// ============================================

/// <summary>
/// Static helper for emitting events from any activity.
/// Used by all TammaActivity variants.
/// </summary>
public static class TammaEventEmitter
{
    public static void EmitStart(ActivityExecutionContext context, ITammaActivity activity, IActivity source, ILogger? logger)
    {
        if (activity.EventType == null) return;
        Emit(context, source, logger, new TammaEvent
        {
            EventType = $"{activity.EventType}.STARTED",
            Status = "started",
            Data = activity.BuildStartData(context),
        });
    }

    public static void EmitSuccess(ActivityExecutionContext context, ITammaActivity activity, IActivity source, ILogger? logger, TimeSpan duration)
    {
        if (activity.EventType == null) return;
        Emit(context, source, logger, new TammaEvent
        {
            EventType = $"{activity.EventType}.COMPLETED",
            Status = "success",
            Duration = duration,
            Data = activity.BuildEndData(context),
        });
    }

    public static void EmitFailure(ActivityExecutionContext context, ITammaActivity activity, IActivity source, ILogger? logger, TimeSpan duration, string error)
    {
        if (activity.EventType == null) return;
        Emit(context, source, logger, new TammaEvent
        {
            EventType = $"{activity.EventType}.FAILED",
            Status = "error",
            Error = error,
            Duration = duration,
            Data = activity.BuildEndData(context),
        });
    }

    /// <summary>
    /// Emit an arbitrary TammaEvent (e.g. AGENT.RESULTS.PARTIAL — a
    /// non-standard status the caller has already composed).
    /// </summary>
    public static void Emit(ActivityExecutionContext context, IActivity source, ILogger? logger, TammaEvent evt)
    {
        EmitInternal(context, source, logger, evt);
    }

    private static void EmitInternal(ActivityExecutionContext context, IActivity source, ILogger? logger, TammaEvent evt)
    {
        evt.Timestamp = DateTime.UtcNow;
        evt.ActivityId = source.Id;
        evt.ActivityName = source.Name ?? source.GetType().Name;
        evt.WorkflowInstanceId = context.WorkflowExecutionContext.Id;

        var props = context.WorkflowExecutionContext.TransientProperties;
        if (!props.TryGetValue("tamma:events", out var existing) || existing is not List<TammaEvent>)
        {
            existing = new List<TammaEvent>();
            props["tamma:events"] = existing;
        }
        ((List<TammaEvent>)existing).Add(evt);

        logger?.LogInformation(
            "[EVENT] {EventType} | {ActivityName} | {Status} | {Duration}ms",
            evt.EventType, evt.ActivityName, evt.Status, evt.Duration?.TotalMilliseconds ?? 0);
    }
}

// ============================================
// Base Classes
// ============================================

/// <summary>
/// Sync activity with no outcomes. Inherits CodeActivity.
/// </summary>
public abstract class TammaActivity : CodeActivity, ITammaActivity
{
    protected ILogger? Logger { get; set; }

    public virtual string? EventType => null;
    public virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new();
    public virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new();

    protected abstract void Run(ActivityExecutionContext context);

    protected override void Execute(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, Logger);

        try
        {
            Run(context);
            TammaEventEmitter.EmitSuccess(context, this, this, Logger, DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            TammaEventEmitter.EmitFailure(context, this, this, Logger, DateTime.UtcNow - startedAt, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Async activity with no outcomes. Inherits CodeActivity.
/// </summary>
public abstract class TammaAsyncActivity : CodeActivity, ITammaActivity
{
    protected ILogger? Logger { get; set; }

    public virtual string? EventType => null;
    public virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new();
    public virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new();

    protected abstract Task RunAsync(ActivityExecutionContext context);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, Logger);

        try
        {
            await RunAsync(context);
            TammaEventEmitter.EmitSuccess(context, this, this, Logger, DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            TammaEventEmitter.EmitFailure(context, this, this, Logger, DateTime.UtcNow - startedAt, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Sync activity that returns a result. Inherits CodeActivity&lt;T&gt;.
/// Use for activities like ValidateWorkItemActivity that produce a typed output.
/// </summary>
public abstract class TammaResultActivity<T> : CodeActivity<T>, ITammaActivity
{
    protected ILogger? Logger { get; set; }

    public virtual string? EventType => null;
    public virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new();
    public virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new();

    protected abstract T RunWithResult(ActivityExecutionContext context);

    protected override void Execute(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, Logger);

        try
        {
            var result = RunWithResult(context);
            context.SetResult(result);
            TammaEventEmitter.EmitSuccess(context, this, this, Logger, DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            TammaEventEmitter.EmitFailure(context, this, this, Logger, DateTime.UtcNow - startedAt, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Async activity WITH outcomes (FlowNode). Inherits Activity.
/// Use for activities that need Continue/Stop, True/False, etc.
/// </summary>
public abstract class TammaOutcomeActivity : Activity, ITammaActivity
{
    protected ILogger? Logger { get; set; }

    public virtual string? EventType => null;
    public virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new();
    public virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new();

    protected abstract Task RunAsync(ActivityExecutionContext context);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, Logger);

        try
        {
            await RunAsync(context);
            TammaEventEmitter.EmitSuccess(context, this, this, Logger, DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            TammaEventEmitter.EmitFailure(context, this, this, Logger, DateTime.UtcNow - startedAt, ex.Message);
            throw;
        }
    }
}
