namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-6 (AC2) — DCB event-type constants for the per-agent ACTION TRAIL
/// written to the tenant-scoped <c>domain_events</c> stream. Pattern
/// <c>AGGREGATE.ACTION.STATUS</c> (per CLAUDE.md "Event Types"), matching the
/// neighbouring <see cref="AgentRunEventTypes"/> / <see cref="AgentEventTypes"/>
/// convention. Kept here as the single source so the emitter never hand-types a
/// literal that could drift from the audit/analytics consumers (32-8/32-9/32-10/32-11).
///
/// <para>Distinct from <see cref="AgentRunEventTypes"/> (<c>AGENT.RUN.*</c>, the
/// 32-5 run-record family): the <c>AGENT.TASK.*</c> terminal here is the
/// audit/analytics <em>trail</em> substrate. A single managed run produces both —
/// one <c>AGENT.RUN.*</c> record (32-5) AND one terminal <c>AGENT.TASK.*</c> trail
/// event (32-6), plus per-step tool-call / iteration events.</para>
/// </summary>
public static class AgentTrailEventTypes
{
    /// <summary>A managed agent run completed with a usable response (terminal trail).</summary>
    public const string TaskSuccess = "AGENT.TASK.SUCCESS";

    /// <summary>A managed agent run failed (terminal trail).</summary>
    public const string TaskFailed = "AGENT.TASK.FAILED";

    /// <summary>A managed agent run produced a partial result (terminal trail;
    /// e.g. a panel where only some participants succeeded).</summary>
    public const string TaskPartial = "AGENT.TASK.PARTIAL";

    /// <summary>One tool invocation inside the run's tool loop succeeded.</summary>
    public const string ToolCallSuccess = "AGENT.TOOL_CALL.SUCCESS";

    /// <summary>One tool invocation inside the run's tool loop failed.</summary>
    public const string ToolCallFailed = "AGENT.TOOL_CALL.FAILED";

    /// <summary>One design/review iteration of the loop finished.</summary>
    public const string IterationCompleted = "AGENT.ITERATION.COMPLETED";

    /// <summary>A panel (32-7 <c>AggregatePanelActivity</c>) combined N agent
    /// results into one.</summary>
    public const string PanelAggregated = "AGENT.PANEL.AGGREGATED";

    /// <summary>A bug was classified at review/gate (32-8). Additionally tags
    /// <c>bugType</c>.</summary>
    public const string BugRecorded = "REVIEW.BUG.RECORDED";

    /// <summary>Best-effort breadcrumb emitted when a trail write terminally
    /// fails (AC7). The run itself is never aborted — this makes the gap
    /// observable instead of silent.</summary>
    public const string TrailWriteFailed = "AGENT.TRAIL.WRITE_FAILED";

    /// <summary>Type prefix used by the <c>/runs</c> read to select only the
    /// terminal <c>AGENT.TASK.*</c> family.</summary>
    public const string TaskPrefix = "AGENT.TASK";
}
