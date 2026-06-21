namespace Tamma.Api.Services.Agents;

/// <summary>
/// Fully resolved agent configuration returned by
/// <see cref="IAgentResolverService"/>.
///
/// Represents the final merge of platform defaults + tenant overrides.
/// All required fields must be non-null/non-empty after resolution —
/// <see cref="AgentResolverService"/> validates this before returning.
/// </summary>
public class ResolvedAgentConfig
{
    /// <summary>Role identifier (e.g. "developer"). Always set.</summary>
    public required string Role { get; init; }

    /// <summary>Stable handle for the agent (e.g. "tamma-developer").</summary>
    public required string Handle { get; init; }

    /// <summary>Provider identifier (e.g. "claude-code", "openai").</summary>
    public required string Provider { get; init; }

    /// <summary>Model identifier.</summary>
    public required string Model { get; init; }

    /// <summary>LLM temperature (0.0 .. 2.0 typically).</summary>
    public double Temperature { get; init; }

    /// <summary>Completion token cap for a single call.</summary>
    public int MaxTokens { get; init; }

    /// <summary>Overall token budget (context + completion) per workflow step.</summary>
    public int TokenBudget { get; init; }

    /// <summary>Allowed tools for this agent (empty = provider default set).</summary>
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    /// <summary>System prompt / role identity preamble.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>
    /// Provenance. Legacy JSONB path: <c>"platform-default"</c> |
    /// <c>"tenant-override"</c>. Story 32-2 entity-aware path extends the value
    /// set with <c>"tenant-private"</c> | <c>"tenant-public"</c> |
    /// <c>"system-public"</c> (which agent in the precedence chain produced the
    /// config). Both legacy values remain valid.
    /// </summary>
    public string Source { get; init; } = "platform-default";

    /// <summary>
    /// Story 32-2 — stable identity of the agent that produced this config.
    /// Null only on the legacy JSONB path (backward compatibility) — the
    /// entity-aware resolve methods always stamp it.
    /// </summary>
    public Guid? AgentId { get; init; }

    /// <summary>
    /// Story 32-2 — the pinned/active config version of the resolved agent.
    /// Null only on the legacy JSONB path.
    /// </summary>
    public int? AgentVersion { get; init; }

    /// <summary>Optional phase context (set by <see cref="IAgentResolverService.ResolveForPhaseAsync"/>).</summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Per-task USD budget ceiling. Null means "inherit role default" (no
    /// clamp applied at resolver time). Finding 007 — TS <c>maxBudgetUsd</c>
    /// clamping rule: <c>maxBudgetUsd = Math.min(taskOverride, role)</c>.
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>
    /// Permission mode. Valid values: <c>"default" | "acceptEdits" |
    /// "bypassPermissions"</c>. <c>bypassPermissions</c> requires the
    /// operator-set env var <c>TAMMA_ALLOW_BYPASS_PERMISSIONS=true</c>;
    /// otherwise the override is silently dropped and the role mode is kept.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Effective allowed tool list after task-override clamping. The TS
    /// behaviour is strictly intersectional — overrides can restrict but
    /// never add tools the role didn't already have.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
}
