using Elsa.Workflows;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Core;

/// <summary>
/// Base activity for all Tamma activities.
/// Provides automatic event emission via the SaveEvent pattern.
/// Subclasses override BuildEvent to define their event payload.
/// </summary>
public abstract class TammaActivity : CodeActivity
{
    protected ILogger? Logger { get; set; }

    /// <summary>
    /// Override to define the event type for this activity (e.g., "ADL.CONFIG.INIT").
    /// Return null to skip event emission.
    /// </summary>
    protected virtual string? EventType => null;

    /// <summary>
    /// Override to add custom data to the start event.
    /// </summary>
    protected virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context)
        => new();

    /// <summary>
    /// Override to add custom data to the end event.
    /// </summary>
    protected virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
        => new();

    /// <summary>
    /// Implement your activity logic here instead of Execute.
    /// </summary>
    protected abstract void Run(ActivityExecutionContext context);

    protected override void Execute(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;

        // Emit start event
        if (EventType != null)
        {
            EmitEvent(context, new TammaEvent
            {
                EventType = $"{EventType}.STARTED",
                Status = "started",
                Data = BuildStartData(context),
            });
        }

        try
        {
            Run(context);

            // Emit success event
            if (EventType != null)
            {
                EmitEvent(context, new TammaEvent
                {
                    EventType = $"{EventType}.COMPLETED",
                    Status = "success",
                    Duration = DateTime.UtcNow - startedAt,
                    Data = BuildEndData(context),
                });
            }
        }
        catch (Exception ex)
        {
            // Emit error event
            if (EventType != null)
            {
                EmitEvent(context, new TammaEvent
                {
                    EventType = $"{EventType}.FAILED",
                    Status = "error",
                    Error = ex.Message,
                    Duration = DateTime.UtcNow - startedAt,
                    Data = BuildEndData(context),
                });
            }
            throw;
        }
    }

    private void EmitEvent(ActivityExecutionContext context, TammaEvent evt)
    {
        evt.Timestamp = DateTime.UtcNow;
        evt.ActivityId = Id;
        evt.ActivityName = Name ?? GetType().Name;
        evt.WorkflowInstanceId = context.WorkflowExecutionContext.Id;

        // Store event in workflow transient properties for collection by the orchestrator
        var events = context.WorkflowExecutionContext.TransientProperties
            .GetOrAdd("tamma:events", () => new List<TammaEvent>()) as List<TammaEvent>;
        events?.Add(evt);

        Logger?.LogInformation(
            "[EVENT] {EventType} | {ActivityName} | {Status} | {Duration}ms",
            evt.EventType, evt.ActivityName, evt.Status, evt.Duration?.TotalMilliseconds ?? 0);
    }
}

/// <summary>
/// Base activity for async Tamma activities.
/// </summary>
public abstract class TammaAsyncActivity : CodeActivity
{
    protected ILogger? Logger { get; set; }

    protected virtual string? EventType => null;

    protected virtual Dictionary<string, object?> BuildStartData(ActivityExecutionContext context)
        => new();

    protected virtual Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
        => new();

    protected abstract Task RunAsync(ActivityExecutionContext context);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;

        if (EventType != null)
        {
            EmitEvent(context, new TammaEvent
            {
                EventType = $"{EventType}.STARTED",
                Status = "started",
                Data = BuildStartData(context),
            });
        }

        try
        {
            await RunAsync(context);

            if (EventType != null)
            {
                EmitEvent(context, new TammaEvent
                {
                    EventType = $"{EventType}.COMPLETED",
                    Status = "success",
                    Duration = DateTime.UtcNow - startedAt,
                    Data = BuildEndData(context),
                });
            }
        }
        catch (Exception ex)
        {
            if (EventType != null)
            {
                EmitEvent(context, new TammaEvent
                {
                    EventType = $"{EventType}.FAILED",
                    Status = "error",
                    Error = ex.Message,
                    Duration = DateTime.UtcNow - startedAt,
                    Data = BuildEndData(context),
                });
            }
            throw;
        }
    }

    private void EmitEvent(ActivityExecutionContext context, TammaEvent evt)
    {
        evt.Timestamp = DateTime.UtcNow;
        evt.ActivityId = Id;
        evt.ActivityName = Name ?? GetType().Name;
        evt.WorkflowInstanceId = context.WorkflowExecutionContext.Id;

        var events = context.WorkflowExecutionContext.TransientProperties
            .GetOrAdd("tamma:events", () => new List<TammaEvent>()) as List<TammaEvent>;
        events?.Add(evt);

        Logger?.LogInformation(
            "[EVENT] {EventType} | {ActivityName} | {Status} | {Duration}ms",
            evt.EventType, evt.ActivityName, evt.Status, evt.Duration?.TotalMilliseconds ?? 0);
    }
}

/// <summary>
/// Event emitted by a Tamma activity.
/// </summary>
public class TammaEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Status { get; set; } = "success";
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan? Duration { get; set; }
    public string? ActivityId { get; set; }
    public string? ActivityName { get; set; }
    public string? WorkflowInstanceId { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
}
