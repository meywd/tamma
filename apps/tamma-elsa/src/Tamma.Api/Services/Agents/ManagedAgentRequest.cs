using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 — the INTERNAL input to <see cref="IManagedAgent.RunAsync"/>,
/// mapped one-to-one from the wire <see cref="LlmCallRequest"/> by
/// <see cref="From"/>. It exists so the composition layer
/// (<c>ManagedAgent</c>, T3) never depends on HTTP-binding concerns — the
/// endpoint owns the <c>X-Tenant-Id</c>-vs-body tenant precedence and produces a
/// fully-resolved request.
///
/// <para>Per CLAUDE.md's two-scoping rule, <see cref="TenantId"/> == null means
/// single-user / platform scope; a non-null value is the SaaS tenant.</para>
/// </summary>
public sealed record ManagedAgentRequest
{
    /// <summary>Resolved tenant scope (body wins, else the header arg). <c>null</c>
    /// ⇒ single-user / platform.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Explicit custom/persona agent; <c>null</c> ⇒ resolve by role.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>System persona name (drives Epic 27 persona prompt resolution).</summary>
    public string? Persona { get; init; }

    /// <summary>The role to serve. Always set.</summary>
    public required string Role { get; init; }

    /// <summary>Role+action prompt key (Epic 27).</summary>
    public string? Action { get; init; }

    /// <summary>Workflow phase for <c>ResolveForPhaseAsync</c> (32-2).</summary>
    public string? Phase { get; init; }

    /// <summary>The task / user prompt. Always set.</summary>
    public required string Prompt { get; init; }

    /// <summary>Template variables injected into the rendered prompt.</summary>
    public Dictionary<string, object?> Variables { get; init; } = new();

    /// <summary>Optional model override (clamped to the agent allowance).</summary>
    public string? Model { get; init; }

    /// <summary>Optional tool allow-list.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Whether the agentic tool loop runs.</summary>
    public bool EnableToolLoop { get; init; }

    /// <summary>Tool-loop configuration (honoured when
    /// <see cref="EnableToolLoop"/> is true).</summary>
    public ToolLoopConfig? ToolLoopConfig { get; init; }

    /// <summary>Inference parameters.</summary>
    public LlmCallParams Params { get; init; } = new();

    /// <summary>Workflow instance id. Always set.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Pure mapping from the wire request to the internal request. Tenant
    /// precedence (AC1): the body's <see cref="LlmCallRequest.TenantId"/> wins
    /// when present, otherwise the <paramref name="headerTenantId"/> derived from
    /// <c>X-Tenant-Id</c>; both null ⇒ single-user / platform. Every other field
    /// is carried forward verbatim. No behaviour beyond the mapping.
    /// </summary>
    public static ManagedAgentRequest From(LlmCallRequest request, Guid? headerTenantId) =>
        new()
        {
            TenantId = request.TenantId ?? headerTenantId,
            AgentId = request.AgentId,
            Persona = request.Persona,
            Role = request.Role,
            Action = request.Action,
            Phase = request.Phase,
            Prompt = request.Prompt,
            Variables = request.Variables,
            Model = request.Model,
            Tools = request.Tools,
            EnableToolLoop = request.EnableToolLoop,
            ToolLoopConfig = request.ToolLoopConfig,
            Params = request.Params,
            CorrelationId = request.CorrelationId,
        };
}
