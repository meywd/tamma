using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Agents.Scripted;

/// <summary>
/// One scripted LLM call as the tool-loop runner sees it (2026-08-13, the
/// Epic 31 P5 LLM stub). The runner supplies the call's identity keys —
/// <see cref="Role"/> / <see cref="Action"/> ride the provider config
/// (<see cref="LlmProviderConfig.CallRole"/> / <see cref="LlmProviderConfig.CallAction"/>,
/// stamped by <c>ManagedAgent</c>), <see cref="DocumentType"/> comes from the
/// 39-9 repair plan when the wire request named one.
/// </summary>
/// <param name="Provider">The provider key ("scripted").</param>
/// <param name="Role">Agent role wire token (e.g. "architect"); may be null.</param>
/// <param name="Action">Role+action wire token (e.g. "plan-system-design"); may be null.</param>
/// <param name="DocumentType">Document-type wire key the produced text must validate
/// against (e.g. "plan"); null/empty when the call carries no typed document.</param>
/// <param name="Model">Requested model id (informational only).</param>
/// <param name="CorrelationId">Workflow correlation id (informational only).</param>
public sealed record ScriptedLlmCall(
    string Provider,
    string? Role,
    string? Action,
    string? DocumentType,
    string Model,
    string CorrelationId);

/// <summary>
/// The in-process seam behind the opt-in "scripted" provider: deterministic
/// canned responses keyed on (role, action, document-type), so the engine's
/// cycle steps that think through <c>POST /api/v1/llm/call</c> can run with no
/// network LLM at all. Registered ONLY when <c>Llm:EnableScriptedProvider=true</c>
/// on a non-production host (see <c>ScriptedProviderPosture</c>); when absent,
/// <c>InlineToolLoopRunner</c>'s behaviour is byte-identical to before.
/// </summary>
public interface IScriptedLlmResponder
{
    /// <summary>Whether this responder serves <paramref name="provider"/>
    /// (the canonical "scripted" key or an alias).</summary>
    bool CanHandle(string? provider);

    /// <summary>
    /// Produce the deterministic response for <paramref name="call"/>. NEVER
    /// throws: an unscripted cell returns a FAILED response whose
    /// <see cref="NormalizedLlmResponse.ErrorMessage"/> names the missing
    /// script key(s) (a clear typed error, not a silent default).
    /// </summary>
    NormalizedLlmResponse Respond(ScriptedLlmCall call);
}
