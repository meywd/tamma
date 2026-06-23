using Elsa.Extensions;
using Elsa.Workflows;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 32-5 (AC9) — the shared text-completion helper the cut-over in-engine
/// callers (TDD / ADL / Debug / AI mentorship activities) use INSTEAD of a direct
/// keyed provider HTTP call.
///
/// <para>Before the pivot each of those activities held its own
/// <c>IHttpClientFactory</c>-backed <c>POST /v1/messages</c> path (the rule-1
/// violation: a live external key in the engine process). This helper routes the
/// same request through the single mediation endpoint <c>POST /api/v1/llm/call</c>
/// via <see cref="TammaApiClient.CallLlmAsync"/>, which holds the credential,
/// gates, runs the loop server-side, and meters — the engine holds NO key.</para>
///
/// <para>It returns the response TEXT (the activities each parse their own
/// structured JSON out of that text, exactly as before — their output contracts
/// are unchanged). A failed / empty mediated call surfaces as an exception so the
/// activity's existing <c>try/catch</c> produces its established failure result.</para>
/// </summary>
internal static class MediatedLlmText
{
    /// <summary>
    /// Send a single-shot (no tool loop) text completion through the call-LLM
    /// endpoint and return the response text. The <paramref name="role"/> drives
    /// the API's authoritative Epic-27 prompt resolution; the engine forwards NO
    /// system prompt (the API renders it). Throws on a missing / unsuccessful /
    /// empty mediated response so the caller's catch builds its failure result.
    /// </summary>
    public static async Task<string> CompleteAsync(
        ActivityExecutionContext context,
        string role,
        string prompt,
        CancellationToken ct)
    {
        var apiClient = context.GetRequiredService<TammaApiClient>();
        var tenantId = ResolveTenantId(context);

        var request = new LlmCallApiRequest
        {
            Role = string.IsNullOrWhiteSpace(role) ? "assistant" : role,
            Prompt = prompt,
            EnableToolLoop = false,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var response = await apiClient.CallLlmAsync(request, tenantId, ct).ConfigureAwait(false);

        if (response is null)
        {
            // null == transport / raw-5xx (PostAsync nulled the body). Fail closed —
            // never fabricate an empty completion.
            throw new InvalidOperationException(
                "call-LLM endpoint unavailable (no response body)");
        }

        if (!response.Success)
        {
            var reason = !string.IsNullOrEmpty(response.FailureReason)
                ? response.FailureReason
                : response.FailureCode ?? "LLM call failed";
            throw new InvalidOperationException($"call-LLM failed: {reason}");
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            // A successful-but-textless response would silently degrade the
            // activity's parser to its empty-fallback. Surface it instead.
            throw new InvalidOperationException(
                "call-LLM returned no text; refusing to fabricate a result.");
        }

        return response.Text!;
    }

    /// <summary>
    /// Resolve the tenant scope (X-Tenant-Id) from the workflow's ambient
    /// <c>TenantId</c> variable when present. Empty / unset ⇒ single-user /
    /// platform scope (the endpoint resolves the platform credential).
    /// </summary>
    private static string? ResolveTenantId(ActivityExecutionContext context)
    {
        try
        {
            var raw = context.GetVariable<string?>("TenantId");
            return string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim();
        }
        catch
        {
            return null;
        }
    }
}
