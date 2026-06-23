using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (design §2.2) — the wire request bound by
/// <c>POST /api/v1/llm/call</c> (<c>LlmCallEndpoints</c>). The engine's
/// ~80-line <c>CallLlmInlineActivity</c> thin client maps its current
/// <c>Input&lt;&gt;</c> props into this record and sends it via
/// <c>TammaApiClient.CallLlmAsync</c>. The handler ignores any body
/// <see cref="TenantId"/> and instead uses the auth-derived tenant
/// (Finding C1), then delegates to <see cref="IManagedAgent.RunAsync"/> (via
/// <see cref="ManagedAgentRequest.From"/>).
///
/// <para>This record carries NO provider API key — the engine holds no key. The
/// key is resolved server-side inside <c>Tamma.Api</c> (32-3 cabinet,
/// BYOK→platform), used request-scoped for the outbound HTTPS call, and never
/// returned (see <see cref="LlmCallResponse"/>).</para>
/// </summary>
public sealed record LlmCallRequest
{
    /// <summary>
    /// Tenant scope as sent by the thin client. <c>null</c> ⇒ single-user /
    /// platform scope.
    ///
    /// <para><b>Finding C1.</b> This field carries NO server-side authority: the
    /// endpoint uses the auth-derived tenant (<c>ITenantContext</c>), NOT this
    /// value, for the gate / budget / credential path. It cannot override the
    /// authenticated scope (cross-tenant spoofing). Retained on the wire so the
    /// thin client may still send it, but it is ignored by
    /// <see cref="ManagedAgentRequest.From"/>.</para>
    /// </summary>
    public Guid? TenantId { get; init; }

    /// <summary>
    /// Explicit custom/persona agent to run. When <c>null</c>, the agent is
    /// resolved by <see cref="Role"/> (+ <see cref="Phase"/>) via the 32-2/32-18
    /// resolver applying the 32-16 per-tenant enablement gate.
    /// </summary>
    public Guid? AgentId { get; init; }

    /// <summary>System persona name (e.g. <c>"claude"</c>, <c>"gemini"</c>,
    /// <c>"codegpt"</c>). Drives Epic 27 persona prompt resolution (32-15).</summary>
    public string? Persona { get; init; }

    /// <summary>
    /// Explicit provider override (the provider KEY — <c>"anthropic"</c> /
    /// <c>"openai"</c> / <c>"openrouter"</c>, NOT a persona name). Finding I-1.
    ///
    /// <para>The workflow OWNS the provider chain (<c>LlmCallWorkflow.BuildRetryLoop</c>
    /// → <c>ForEach&lt;provider&gt;</c> invokes the endpoint ONCE per provider per
    /// attempt). When the per-iteration provider is set here, the API honours it as
    /// the provider for THIS call — resolving the credential for THIS provider and
    /// choosing the model accordingly — so the chain is meaningful again (each
    /// iteration tries the next provider via the API). When <c>null</c>, the
    /// provider resolved by <see cref="Role"/> is used.</para>
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>One of the 8 valid roles — drives Epic 27 prompt resolution.
    /// Required: the role is the minimal anchor every managed run carries.</summary>
    public required string Role { get; init; }

    /// <summary>Role+action prompt key for the Epic 27 <c>(principal, role, action)</c>
    /// lookup. <c>null</c> ⇒ the action-default template.</summary>
    public string? Action { get; init; }

    /// <summary>Workflow phase for <c>ResolveForPhaseAsync</c> (32-2). Optional
    /// context that refines which agent serves the role for this step.</summary>
    public string? Phase { get; init; }

    /// <summary>The task / user prompt (the "user message"). Required.</summary>
    public required string Prompt { get; init; }

    /// <summary>Template variables injected into the rendered prompt. Defaults to
    /// an empty map — never <c>null</c>.</summary>
    public Dictionary<string, object?> Variables { get; init; } = new();

    /// <summary>Optional model override. Clamped to the persona/agent allowance
    /// server-side; an out-of-allowance value is rejected, never silently used.</summary>
    public string? Model { get; init; }

    /// <summary>Optional allow-list of tool names the LLM may invoke. <c>null</c> ⇒
    /// the agent's resolved default tool set.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Whether to run the agentic tool loop. Default <c>false</c>
    /// (single-turn). Passed through to the server-side runner — never executed
    /// in the engine.</summary>
    public bool EnableToolLoop { get; init; }

    /// <summary>Tool-loop configuration (only honoured when
    /// <see cref="EnableToolLoop"/> is true). Reuses the existing
    /// <see cref="Tamma.Activities.LlmCall.Models.ToolLoopConfig"/> shape so the
    /// engine and the server-side runner share one contract.</summary>
    public ToolLoopConfig? ToolLoopConfig { get; init; }

    /// <summary>Inference parameters (<c>maxTokens</c>/<c>temperature</c>/
    /// <c>budgetCapUsd</c>). Defaults to <see cref="LlmCallParams"/>'s defaults.</summary>
    public LlmCallParams Params { get; init; } = new();

    /// <summary>Workflow instance id — links the run back to the parent workflow
    /// and tags every DCB event. Required.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>
/// Story 32-5 (design §2.2) — inference parameters carried by
/// <see cref="LlmCallRequest.Params"/>. Defaults mirror
/// <c>LlmCallWorkflowInput</c> (maxTokens 4096, temperature 0.7) and a 0 budget
/// cap meaning "unlimited" (the budget gate treats 0 as no cap, consistent with
/// the existing <c>BudgetState</c>).
/// </summary>
public sealed record LlmCallParams
{
    /// <summary>Completion token cap for this call.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Sampling temperature (0.0 .. 2.0 typically).</summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>Per-call USD budget cap. <c>0</c> ⇒ unlimited (no clamp).</summary>
    public decimal BudgetCapUsd { get; init; }
}
