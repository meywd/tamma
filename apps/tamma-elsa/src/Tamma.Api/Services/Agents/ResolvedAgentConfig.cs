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

    /// <summary>Provenance: "platform-default" or "tenant-override".</summary>
    public string Source { get; init; } = "platform-default";

    /// <summary>Optional phase context (set by <see cref="IAgentResolverService.ResolveForPhaseAsync"/>).</summary>
    public string? Phase { get; init; }
}
