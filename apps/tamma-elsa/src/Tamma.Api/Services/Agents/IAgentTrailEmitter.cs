namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-6 — the SINGLE seam every producer (32-5 managed run, 32-7 panels,
/// 32-8 review gate) calls to record an agent ACTION TRAIL event into the
/// resolving tenant's <c>domain_events</c> stream. No producer hand-builds a
/// <c>DomainEvent</c>: this one place enforces tag completeness (AC3), redaction
/// (AC6), and the non-blocking contract (AC7).
///
/// <para><b>Non-blocking (AC7):</b> NONE of these methods throw into the run. A
/// trail-write failure is logged (WARN) and surfaced as a best-effort
/// <c>AGENT.TRAIL.WRITE_FAILED</c> breadcrumb so the gap is observable, never
/// silent — but the caller's run always continues.</para>
/// </summary>
public interface IAgentTrailEmitter
{
    /// <summary>Record the terminal <c>AGENT.TASK.SUCCESS</c>/<c>.FAILED</c>/
    /// <c>.PARTIAL</c> for a completed run.</summary>
    Task RunCompletedAsync(AgentTrailContext ctx, AgentRunOutcome outcome, CancellationToken ct = default);

    /// <summary>Record one <c>AGENT.TOOL_CALL.SUCCESS</c>/<c>.FAILED</c> for a
    /// tool invocation in the run's tool loop.</summary>
    Task ToolCallAsync(AgentTrailContext ctx, ToolCallRecord call, CancellationToken ct = default);

    /// <summary>Record one <c>AGENT.ITERATION.COMPLETED</c> for a finished
    /// design/review iteration.</summary>
    Task IterationCompletedAsync(AgentTrailContext ctx, IterationRecord iteration, CancellationToken ct = default);

    /// <summary>Record an <c>AGENT.PANEL.AGGREGATED</c> when a panel (32-7)
    /// combines N agent results.</summary>
    Task PanelAggregatedAsync(AgentTrailContext ctx, PanelRecord panel, CancellationToken ct = default);

    /// <summary>Record a <c>REVIEW.BUG.RECORDED</c> when a bug is classified at
    /// review/gate (32-8). Additionally tags <c>bugType</c>.</summary>
    Task BugRecordedAsync(AgentTrailContext ctx, BugRecord bug, CancellationToken ct = default);
}
