using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (D2) — the composed document-content validation seam handed to
/// <c>ManagedAgent</c> / the runner. It carries the document-type wire KEY (for
/// event tags) plus a pure <see cref="Validate"/> delegate built API-side over
/// <c>DocumentTypeRegistry.Resolve(key).Validate</c> (a delegate cannot cross
/// HTTP). Composition is done by <see cref="DocumentValidationBinder.Bind"/>;
/// <c>ManagedAgent</c> and the runner never touch the registry — keeping them
/// unit-testable with fake validators.
/// </summary>
/// <param name="DocumentTypeKey">The document-type wire key (event tag only).</param>
/// <param name="Validate">Produced-document text → validation verdict. Never throws
/// for malformed input — returns an invalid result with a synthetic
/// <c>PAYLOAD_NOT_JSON</c> violation.</param>
public sealed record DocumentContentValidation(
    string DocumentTypeKey,
    Func<string, DocumentValidationResult> Validate);

/// <summary>
/// Story 32-5 — the INTERNAL input to <see cref="IManagedAgent.RunAsync"/>,
/// mapped one-to-one from the wire <see cref="LlmCallRequest"/> by
/// <see cref="From"/>. It exists so the composition layer
/// (<c>ManagedAgent</c>, T3) never depends on HTTP-binding concerns — the
/// endpoint owns tenant resolution and produces a fully-resolved request.
///
/// <para>Per CLAUDE.md's two-scoping rule, <see cref="TenantId"/> == null means
/// single-user / platform scope; a non-null value is the SaaS tenant.</para>
///
/// <para><b>Finding C1 (cross-tenant credential spoofing).</b>
/// <see cref="TenantId"/> is the AUTHORITATIVE, auth-derived tenant — it is
/// the scope used by the SaaS gate, the budget guard, and the credential
/// resolver (BYOK key / platform fallback). It is NEVER taken from the wire
/// body: a caller could otherwise name any tenant GUID in
/// <see cref="LlmCallRequest.TenantId"/> and be gated / budgeted / credentialed
/// as that tenant. <see cref="From"/> derives it from the authenticated
/// principal (the endpoint's <c>ITenantContext</c>), not from the request.</para>
/// </summary>
public sealed record ManagedAgentRequest
{
    /// <summary>Authoritative, auth-derived tenant scope. <c>null</c>
    /// ⇒ single-user / platform. NEVER sourced from the wire body
    /// (see <see cref="From"/> / Finding C1).</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Explicit custom/persona agent; <c>null</c> ⇒ resolve by role.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>System persona name (drives Epic 27 persona prompt resolution).</summary>
    public string? Persona { get; init; }

    /// <summary>Explicit provider override (the provider KEY — <c>anthropic</c> /
    /// <c>openai</c> / <c>openrouter</c>). Finding I-1: when set, the API honours
    /// it as the provider for this call (the workflow's per-iteration provider
    /// chain), resolving the credential for THIS provider and choosing the model
    /// for it. <c>null</c> ⇒ use the role-resolved provider.</summary>
    public string? Provider { get; init; }

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

    /// <summary>Story 39-9 (D2/D10) — the composed document-content validation seam.
    /// <c>null</c> ⇒ no validation runs (the repair ring is invisible; behaviour
    /// byte-identical to before). Composed API-side by
    /// <see cref="DocumentValidationBinder.Bind"/> from the wire
    /// <see cref="LlmCallRequest.DocumentType"/> key.</summary>
    public DocumentContentValidation? DocumentValidation { get; init; }

    /// <summary>Story 39-9 (D10) — the issue id, threaded through as an additive
    /// optional input purely for the <c>LLM.*</c> event tags (AC6). <c>null</c>/empty
    /// for the 30+ existing dispatchers ⇒ zero behaviour change.</summary>
    public string? IssueId { get; init; }

    /// <summary>
    /// Pure mapping from the wire request to the internal request. The tenant
    /// scope is the <paramref name="authoritativeTenantId"/> derived from the
    /// authenticated principal (the endpoint's <c>ITenantContext</c>, populated
    /// by <c>TenantContextMiddleware</c> from the service principal's
    /// <c>X-Tenant-Id</c>/claims) — <c>null</c> ⇒ single-user / platform.
    ///
    /// <para><b>Finding C1.</b> The body's
    /// <see cref="LlmCallRequest.TenantId"/> is INTENTIONALLY IGNORED for the
    /// trust-bearing scope: it must never be able to override the authenticated
    /// tenant (gate / budget / credential resolution all key off
    /// <see cref="TenantId"/>). The wire field is retained on the record so the
    /// thin client may still send it, but it carries no server-side authority.
    /// Every other field is carried forward verbatim.</para>
    /// </summary>
    /// <param name="documentValidation">Story 39-9 (D2) — the composed
    /// document-content validation seam, built by the endpoint from
    /// <see cref="LlmCallRequest.DocumentType"/> via
    /// <see cref="DocumentValidationBinder.Bind"/> BEFORE this mapping (so a bad
    /// document-type key can fail loud at the endpoint, never here). <c>null</c> ⇒
    /// no validation. The delegate is composed outside this pure map because a
    /// registry lookup + fail-loud is not a pure mapping concern.</param>
    public static ManagedAgentRequest From(
        LlmCallRequest request,
        Guid? authoritativeTenantId,
        DocumentContentValidation? documentValidation = null) =>
        new()
        {
            TenantId = authoritativeTenantId,
            AgentId = request.AgentId,
            Persona = request.Persona,
            Provider = request.Provider,
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
            // Story 39-9 (D10) — additive pass-throughs (default empty ⇒ no change).
            DocumentValidation = documentValidation,
            IssueId = request.IssueId,
        };
}
