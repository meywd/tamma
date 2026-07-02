using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Security;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-6 — the concrete action-trail emitter. Builds a fully-tagged
/// <see cref="DomainEvent"/> for each trail event, sanitizes its <c>Data</c>
/// (AC6), and appends it via <see cref="IEventRepository.AppendAsync"/> into the
/// resolving tenant's <c>t_&lt;hex&gt;.domain_events</c> schema (AC1). Never throws
/// into the run (AC7).
/// </summary>
public sealed class AgentTrailEmitter : IAgentTrailEmitter
{
    private readonly IEventRepository _events;
    // The SAME sanitization seam ManagedAgent runs (SecureAgentProvider / Story
    // 9-7). Optional (null ⇒ pass-through) to mirror ManagedAgent's contract; it
    // is DI-registered in the API host so production always has it.
    private readonly IContentSanitizer? _sanitizer;
    private readonly ILogger<AgentTrailEmitter> _logger;

    public AgentTrailEmitter(
        IEventRepository events,
        ILogger<AgentTrailEmitter> logger,
        IContentSanitizer? sanitizer = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sanitizer = sanitizer;
    }

    /// <inheritdoc />
    public Task RunCompletedAsync(AgentTrailContext ctx, AgentRunOutcome o, CancellationToken ct = default)
    {
        var type = o.Status switch
        {
            AgentRunStatus.Success => AgentTrailEventTypes.TaskSuccess,
            AgentRunStatus.Partial => AgentTrailEventTypes.TaskPartial,
            _ => AgentTrailEventTypes.TaskFailed,
        };

        var data = new Dictionary<string, object?>
        {
            ["durationMs"] = o.DurationMs,
            ["iterations"] = o.Iterations,
            ["inputTokens"] = o.InputTokens,
            ["outputTokens"] = o.OutputTokens,
            ["costUsd"] = o.CostUsd,
            ["outcomeRef"] = Sanitize(o.OutcomeRef),
            ["failureCode"] = o.FailureCode,
        };

        return EmitAsync(ctx, type, data, extraTags: null, ct);
    }

    /// <inheritdoc />
    public Task ToolCallAsync(AgentTrailContext ctx, ToolCallRecord call, CancellationToken ct = default)
    {
        var type = call.Success
            ? AgentTrailEventTypes.ToolCallSuccess
            : AgentTrailEventTypes.ToolCallFailed;

        var data = new Dictionary<string, object?>
        {
            ["toolName"] = Sanitize(call.ToolName),
            ["argsRef"] = Sanitize(call.ArgsRef),      // sanitized ref — NEVER raw args (AC6)
            ["resultRef"] = Sanitize(call.ResultRef),  // sanitized ref — NEVER raw result (AC6)
            ["durationMs"] = call.DurationMs,
            ["errorCode"] = call.ErrorCode,
        };

        return EmitAsync(ctx, type, data, extraTags: null, ct);
    }

    /// <inheritdoc />
    public Task IterationCompletedAsync(AgentTrailContext ctx, IterationRecord it, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["iteration"] = it.Iteration,
            ["gatePassed"] = it.GatePassed,
            ["findingsCount"] = it.FindingsCount,
        };

        return EmitAsync(ctx, AgentTrailEventTypes.IterationCompleted, data, extraTags: null, ct);
    }

    /// <inheritdoc />
    public Task PanelAggregatedAsync(AgentTrailContext ctx, PanelRecord panel, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["strategy"] = Sanitize(panel.Strategy),
            ["participantAgentIds"] = panel.ParticipantAgentIds.Select(g => g.ToString()).ToArray(),
            ["chosenAgentId"] = panel.ChosenAgentId?.ToString(),
        };

        return EmitAsync(ctx, AgentTrailEventTypes.PanelAggregated, data, extraTags: null, ct);
    }

    /// <inheritdoc />
    public Task BugRecordedAsync(AgentTrailContext ctx, BugRecord bug, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object?>
        {
            ["bugType"] = bug.BugType,
            ["severity"] = bug.Severity,
            ["descriptionRef"] = Sanitize(bug.DescriptionRef), // ref — NEVER the raw description (AC6)
        };

        // AC3 — REVIEW.BUG.RECORDED additionally carries bugType in Tags.
        var extraTags = new Dictionary<string, string?> { ["bugType"] = bug.BugType };

        return EmitAsync(ctx, AgentTrailEventTypes.BugRecorded, data, extraTags, ct);
    }

    // -----------------------------------------------------------------------
    // core append — never throws into the run (AC7)
    // -----------------------------------------------------------------------

    private async Task EmitAsync(
        AgentTrailContext ctx,
        string type,
        IReadOnlyDictionary<string, object?> data,
        IReadOnlyDictionary<string, string?>? extraTags,
        CancellationToken ct)
    {
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = ctx.TenantId,          // resolving tenant — structural isolation (AC1/AC4)
                IssueNumber = ctx.IssueNumber,
                Tags = AgentTrailTags.Build(ctx, extraTags),
                Metadata = StandardMetadata(),
                Data = JsonSerializer.Serialize(data),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);

            _logger.LogDebug(
                "Agent trail event appended. type={Type}, agentId={AgentId}, correlationId={CorrelationId}, tenantId={TenantId}",
                type, ctx.AgentId, ctx.CorrelationId, ctx.TenantId);
        }
        catch (Exception ex)
        {
            // AC7 — a trail-write failure NEVER aborts the run. Log + best-effort
            // breadcrumb so the gap is observable, then swallow.
            _logger.LogWarning(ex,
                "Agent trail write failed for {Type}; the run is NOT affected. "
                + "agentId={AgentId}, correlationId={CorrelationId}, tenantId={TenantId}",
                type, ctx.AgentId, ctx.CorrelationId, ctx.TenantId);
            await TryWriteFailureBreadcrumbAsync(ctx, type, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort <c>AGENT.TRAIL.WRITE_FAILED</c> breadcrumb (AC7).
    /// Itself swallows so a breadcrumb failure can never surface into the run.</summary>
    private async Task TryWriteFailureBreadcrumbAsync(AgentTrailContext ctx, string failedType, CancellationToken ct)
    {
        try
        {
            var data = new Dictionary<string, object?> { ["failedType"] = failedType };
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = AgentTrailEventTypes.TrailWriteFailed,
                TenantId = ctx.TenantId,
                IssueNumber = ctx.IssueNumber,
                Tags = AgentTrailTags.Build(ctx),
                Metadata = StandardMetadata(),
                Data = JsonSerializer.Serialize(data),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The store is unreachable for this write too — a WARN log is the last
            // durable signal. Never rethrow (AC7).
            _logger.LogWarning(ex,
                "Agent trail WRITE_FAILED breadcrumb also failed to persist. "
                + "failedType={FailedType}, agentId={AgentId}, correlationId={CorrelationId}, tenantId={TenantId}",
                failedType, ctx.AgentId, ctx.CorrelationId, ctx.TenantId);
        }
    }

    /// <summary>The standard DCB envelope, mirroring the neighbouring emission
    /// sites (<c>AgentEndpoints.UpdateConfig</c>, <c>ManagedAgent</c>).</summary>
    private static string StandardMetadata() =>
        JsonSerializer.Serialize(new
        {
            workflowVersion = "1.0.0",
            eventSource = "system",
        });

    /// <summary>Defence-in-depth redaction of a free-text ref/value before it is
    /// persisted into the immutable event stream (AC6). By contract these fields
    /// carry REFERENCES, not blobs — the sanitizer strips any HTML / zero-width /
    /// injection-shaped content that slipped through. Never throws.</summary>
    private string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value) || _sanitizer is null)
        {
            return value;
        }

        return _sanitizer.SanitizeOutput(value).Result;
    }
}
