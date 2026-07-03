namespace Tamma.Data.Entities;

public class ProviderDiagnostic
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;
    public double RequestDurationMs { get; set; }

    /// <summary>
    /// Total tokens (input + output). Retained for back-compat. New code
    /// should populate <see cref="InputTokens"/> and <see cref="OutputTokens"/>
    /// directly so post-hoc cost reconciliation works (input and output
    /// tokens are billed at different rates by most providers).
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>Input (prompt) tokens billed at the provider's input rate.</summary>
    public int InputTokens { get; set; }

    /// <summary>Output (completion) tokens billed at the provider's output rate.</summary>
    public int OutputTokens { get; set; }

    public decimal Cost { get; set; }

    /// <summary>
    /// Story 34-3 / 35-2 — the BYOK-vs-platform billing posture that governed
    /// this call, as the lowercase <c>MetricBillingMode</c> token
    /// (<c>"byok"</c> | <c>"platform"</c>, default <c>"platform"</c>). Written on
    /// the LLM-call usage path from the 35-2 billing-mode tagger (which reads the
    /// 34-3 owner and reconciles 32-3's credential source). The markup engine
    /// (34-5) and the analytics dimensional rollup (36-2, <c>ResolveCostBasis</c>)
    /// key off this column so a BYOK call is never token-marked-up / re-billed.
    /// </summary>
    public string BillingMode { get; set; } = "platform";

    public Guid? TenantId { get; set; }
    public string? Model { get; set; }
    public string? RequestType { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    /// <summary>Structured error code from the provider (e.g. "rate_limit").</summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Cross-step trace id. Stitches together context-scan → plan → implement →
    /// review for a single workflow run. Restored from TS migration 014.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>The agent role that issued the call ("developer", "tester", …).</summary>
    public string? AgentType { get; set; }

    /// <summary>Project / repo identifier the call belongs to.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Tamma engine instance that issued the call.</summary>
    public string? EngineId { get; set; }

    /// <summary>Task id within the engine's queue.</summary>
    public string? TaskId { get; set; }

    /// <summary>Task type ("implement", "review", "scan", …).</summary>
    public string? TaskType { get; set; }

    public DateTime CreatedAt { get; set; }
}
