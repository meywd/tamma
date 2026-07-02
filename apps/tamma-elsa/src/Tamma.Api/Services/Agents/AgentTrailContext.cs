namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-6 — the identity/scoping carrier stamped onto every action-trail
/// event's <c>Tags</c>. It is the single source of the flat tag contract (AC3):
/// <c>agentId</c>, <c>agentVersion</c>, <c>role</c>, <c>provider</c>, <c>model</c>,
/// <c>promptRef</c>, <c>issueId</c>, <c>iteration</c>, <c>correlationId</c>,
/// <c>credentialSource</c>. Populated once per run from the resolved agent
/// identity + credential source; passed to every <see cref="IAgentTrailEmitter"/>
/// call so all emission sites are consistent.
///
/// <para><b>Tenant isolation backbone (AC1/AC4):</b> <see cref="TenantId"/> is the
/// resolving tenant — every emitted event carries it and
/// <c>IEventRepository.AppendAsync</c> writes it structurally into that tenant's
/// <c>t_&lt;hex&gt;.domain_events</c> schema. A trail event is NEVER written to the
/// control plane or another tenant's schema.</para>
/// </summary>
public sealed record AgentTrailContext
{
    /// <summary>Resolving tenant. <c>null</c> ⇒ single-user / platform scope
    /// (the run's own instance). Structural isolation key.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Stable agent identity — the join key all of 32-10 re-keys off.
    /// Sourced from the resolved config until 32-1's entity lands.
    ///
    /// <para><b>Sentinel — <c>Guid.Empty</c> = "agent-unresolved".</b> A run that fails
    /// BEFORE the agent is resolved (unknown role, no enabled default, prompt-unresolved,
    /// gate/budget/credential denial evaluated pre-resolve) still emits a fully-tagged
    /// terminal <c>AGENT.TASK.FAILED</c> so the failure stays visible in the trail — but
    /// it carries <c>agentId = 00000000-0000-0000-0000-000000000000</c> because no agent
    /// identity existed yet. Per-agent attribution rollups (32-9 / 32-10) MUST EXCLUDE
    /// <c>Guid.Empty</c>: it is not a real agent, and folding it in would bucket every
    /// pre-resolution failure under one phantom agent.</para></summary>
    public Guid AgentId { get; init; }

    /// <summary>Pinned config version of the resolved agent.</summary>
    public int AgentVersion { get; init; }

    /// <summary>Role the run served (e.g. "developer"). Always set.</summary>
    public required string Role { get; init; }

    /// <summary>Provider that served (or was attempted for) the run.</summary>
    public required string Provider { get; init; }

    /// <summary>Model actually used.</summary>
    public required string Model { get; init; }

    /// <summary>Prompt reference (Epic 27 role:action key / version) — NEVER the
    /// prompt body. May be <c>null</c>.</summary>
    public string? PromptRef { get; init; }

    /// <summary>Issue identifier the run belongs to. May be <c>null</c>.</summary>
    public string? IssueId { get; init; }

    /// <summary>Optional numeric issue number stored on the event row itself
    /// (<see cref="Tamma.Data.Entities.DomainEvent.IssueNumber"/>).</summary>
    public int? IssueNumber { get; init; }

    /// <summary>Loop iteration ordinal (0 for the terminal run event).</summary>
    public int Iteration { get; init; }

    /// <summary>Workflow instance id, carried as a STRING tag on every trail event.
    /// It scopes a single run's events together within this tenant's DCB stream, so
    /// the achievable trail keying today is <c>agentId</c> + <c>correlationId</c>
    /// (both string tags) inside <c>t_&lt;hex&gt;.domain_events</c>.
    ///
    /// <para><b>No per-run join to <c>ProviderDiagnostic</c> today.</b> Such a join is
    /// NOT executable against the current schema: <c>ProviderDiagnostic.CorrelationId</c>
    /// is a <c>Guid?</c> (not this string), <c>ProviderDiagnostic</c> has no
    /// <c>agentId</c> column (only <c>AgentType</c> = the role string), and the managed
    /// run path emits NO <c>ProviderDiagnostic</c> row at all — it meters via
    /// <c>IUsageEmitter</c>. A true per-run trail↔diagnostics correlation is deferred to
    /// a future story / schema change (see Story 35-2 <c>ProviderDiagnostic.BillingMode</c>
    /// / diagnostics work). The only field the two share today is the agent role
    /// (<c>AgentType</c>), which supports a role-scoped — not run-scoped — re-key.</para></summary>
    public required string CorrelationId { get; init; }

    /// <summary>Where the provider key came from: <c>"byok"</c> | <c>"platform"</c>
    /// — NEVER the key. Defaults to <c>"platform"</c> when 32-3 has not resolved a
    /// source yet.</summary>
    public string CredentialSource { get; init; } = "platform";
}

/// <summary>Terminal outcome status of a managed run (AC2).</summary>
public enum AgentRunStatus
{
    /// <summary>Usable response produced.</summary>
    Success,

    /// <summary>Partial result (e.g. a panel where only some participants succeeded).</summary>
    Partial,

    /// <summary>Run failed.</summary>
    Failed,
}

/// <summary>
/// Story 32-6 — terminal run metrics for an <c>AGENT.TASK.*</c> event's
/// <c>Data</c>. All large/sensitive payloads are REFERENCED
/// (<see cref="OutcomeRef"/>), never inlined (AC6).
/// </summary>
public sealed record AgentRunOutcome
{
    public AgentRunStatus Status { get; init; }
    public long DurationMs { get; init; }
    public int Iterations { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal CostUsd { get; init; }

    /// <summary>Reference (key/id) to the full outcome blob — never the blob.</summary>
    public string? OutcomeRef { get; init; }

    /// <summary>Typed failure code on the failed leg (<c>null</c> on success).</summary>
    public string? FailureCode { get; init; }
}

/// <summary>
/// Story 32-6 — one tool invocation in the run's tool loop. Args/results are
/// REFERENCED (<see cref="ArgsRef"/>/<see cref="ResultRef"/>), never inlined (AC6).
/// </summary>
public sealed record ToolCallRecord
{
    public required string ToolName { get; init; }

    /// <summary>Sanitized reference to the tool args — never the raw args.</summary>
    public string? ArgsRef { get; init; }

    /// <summary>Sanitized reference to the tool result — never the raw result.</summary>
    public string? ResultRef { get; init; }

    public long DurationMs { get; init; }
    public bool Success { get; init; } = true;
    public string? ErrorCode { get; init; }
}

/// <summary>Story 32-6 — one design/review loop iteration (AC2).</summary>
public sealed record IterationRecord
{
    public int Iteration { get; init; }
    public bool GatePassed { get; init; }
    public int FindingsCount { get; init; }
}

/// <summary>
/// Story 32-6 — a panel aggregation of N agent results (32-7 producer).
/// Forward-compatible: the emitter method ships now; the call site lands with 32-7.
/// </summary>
public sealed record PanelRecord
{
    /// <summary><c>single</c> | <c>consensus</c> | <c>lead+critics</c> | <c>llm-judge-merge</c>.</summary>
    public required string Strategy { get; init; }

    public IReadOnlyList<Guid> ParticipantAgentIds { get; init; } = Array.Empty<Guid>();

    public Guid? ChosenAgentId { get; init; }
}

/// <summary>
/// Story 32-6 — a bug classified at review/gate (32-8 producer). The
/// <c>REVIEW.BUG.RECORDED</c> event additionally tags <see cref="BugType"/> (AC3).
/// Forward-compatible: the emitter method ships now; the call site lands with 32-8.
/// </summary>
public sealed record BugRecord
{
    /// <summary><c>visual</c> | <c>functional</c> | <c>regression</c> |
    /// <c>security</c> | <c>perf</c> | <c>style</c>.</summary>
    public required string BugType { get; init; }

    public string? Severity { get; init; }

    /// <summary>Reference to the bug description blob — never the raw description.</summary>
    public string? DescriptionRef { get; init; }
}
