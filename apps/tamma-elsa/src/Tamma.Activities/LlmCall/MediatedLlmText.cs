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
        CancellationToken ct,
        // 2026-08-13 (engine-driven E2E run 34): the taxonomy ACTION of the call.
        // This path passed no action, which (a) hid the call's identity from the
        // audit tags and (b) left the scripted provider's role/action key empty —
        // every TDD/debug single-shot missed its cell ("[tester/, *]"). Callers
        // pass their canonical wire action (a real Prompts/{role}/{action}.md cell).
        string? action = null)
    {
        var apiClient = context.GetRequiredService<TammaApiClient>();
        var tenantId = ResolveTenantId(context);

        var request = new LlmCallApiRequest
        {
            // Default to a canonical AgentRole wire ("developer") — the API's
            // AgentResolverService 422s on a non-canonical/unaliased role, so a
            // blank-role default of "assistant" (neither canonical nor aliased)
            // would fail every such call.
            Role = string.IsNullOrWhiteSpace(role) ? "developer" : role,
            Action = string.IsNullOrWhiteSpace(action) ? null : action,
            Prompt = prompt,
            EnableToolLoop = false,
            // 2026-08-13 (engine-driven E2E run 33): honour the SAME deployment-tier
            // provider selection the llm-call workflow applies (LlmCallWorkflow's
            // ResolveChain: caller > DB chain > Llm:DefaultProviderChain). This
            // direct path passed NO provider, so ManagedAgent fell back to the
            // persona's provider — the two mediation paths chose DIFFERENT
            // providers for the same role, and a deployment whose selected chain
            // holds no key for the persona default fails only on THIS path
            // (observed: TDD write-tests failing PROVIDER_CREDENTIAL_UNAVAILABLE
            // on anthropic while every llm-call ran scripted). Null when the
            // deployment sets no chain — the persona default then applies as before.
            Provider = ResolveConfiguredProvider(context),
            // Story 43-14 (AC4) — the RUN correlation, not the sub-workflow's id.
            CorrelationId = string.IsNullOrWhiteSpace(context.WorkflowExecutionContext.CorrelationId)
                ? context.WorkflowExecutionContext.Id
                : context.WorkflowExecutionContext.CorrelationId!,
        };

        var response = await apiClient.CallLlmAsync(request, tenantId, ct).ConfigureAwait(false);

        // 2026-08-14: the workflow path walks the WHOLE chain and then falls back
        // to the persona default (ForEachProviderChain); pinning this path to the
        // chain HEAD with no fallback turned a keyless/failing head into a hard
        // failure for every single-shot call that previously succeeded on the
        // persona default. Walk the remaining entries, then one final attempt
        // with no provider at all (persona default) — the pre-selection
        // behaviour, kept as the last resort rather than the first choice.
        if (request.Provider is not null && !IsUsable(response))
        {
            foreach (var fallback in RemainingProviders(context, request.Provider))
            {
                response = await apiClient
                    .CallLlmAsync(request with { Provider = fallback }, tenantId, ct)
                    .ConfigureAwait(false);
                if (IsUsable(response)) break;
            }

            if (!IsUsable(response))
            {
                response = await apiClient
                    .CallLlmAsync(request with { Provider = null }, tenantId, ct)
                    .ConfigureAwait(false);
            }
        }

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
    /// The deployment-tier provider selection: the FIRST allowlist-passing entry of
    /// <c>Llm:DefaultProviderChain</c>, or null when the deployment configures no
    /// chain (the API's persona/agent-config default then applies). Mirrors the
    /// config tier of <c>LlmCallWorkflow.ResolveChain</c> — this single-shot path
    /// has no caller/DB chain, so the config tier is the only one that can apply.
    /// </summary>
    /// <summary>A response we can actually use: present, successful, with text.</summary>
    private static bool IsUsable(LlmCallApiResponse? response) =>
        response is { Success: true } && !string.IsNullOrWhiteSpace(response.Text);

    /// <summary>
    /// The allow-listed chain entries AFTER <paramref name="current"/> — the
    /// fallback order the workflow path already walks.
    /// </summary>
    private static IEnumerable<string> RemainingProviders(
        ActivityExecutionContext context, string current)
    {
        var chain = AllowedProviderChain(context);
        var seen = false;
        foreach (var provider in chain)
        {
            if (!seen)
            {
                seen = string.Equals(provider, current, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            yield return provider;
        }
    }

    /// <summary>The allow-listed entries of <c>Llm:DefaultProviderChain</c>, in order.</summary>
    private static IReadOnlyList<string> AllowedProviderChain(ActivityExecutionContext context)
    {
        var configuration = context.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (configuration is null) return Array.Empty<string>();

        var chain = Microsoft.Extensions.Configuration.ConfigurationBinder
            .Get<string[]>(configuration.GetSection("Llm:DefaultProviderChain"));
        if (chain is null || chain.Length == 0) return Array.Empty<string>();

        var allowlist = context.GetService<Tamma.Activities.Security.ProviderAllowlist>()
                        ?? new Tamma.Activities.Security.ProviderAllowlist();
        return chain
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(p => allowlist.IsAllowed(p))
            .ToList();
    }

    internal static string? ResolveConfiguredProvider(ActivityExecutionContext context)
    {
        var configuration = context.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (configuration is null) return null;

        var chain = Microsoft.Extensions.Configuration.ConfigurationBinder
            .Get<string[]>(configuration.GetSection("Llm:DefaultProviderChain"));
        if (chain is null || chain.Length == 0) return null;

        var allowlist = context.GetService<Tamma.Activities.Security.ProviderAllowlist>()
                        ?? new Tamma.Activities.Security.ProviderAllowlist();
        return chain
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .FirstOrDefault(p => allowlist.IsAllowed(p));
    }

    /// <summary>
    /// Resolve the tenant scope (X-Tenant-Id) from the workflow's ambient tenant
    /// variable. Mirrors the established convention used by
    /// <c>EventPersistenceMiddleware</c> / <c>CheckBudgetActivity</c>: read
    /// <c>TenantId</c> (legacy fallback <c>AccountId</c>) as an <c>object</c> —
    /// it may be stamped as a <see cref="Guid"/> or a string — and coerce to a
    /// canonical Guid string. An empty / unset / non-Guid value ⇒ platform scope
    /// (the endpoint resolves the platform credential). Reading as <c>object?</c>
    /// (not <c>string?</c>) is deliberate: a Guid-typed variable previously failed
    /// the typed read and was silently swallowed to platform scope.
    /// </summary>
    private static string? ResolveTenantId(ActivityExecutionContext context)
    {
        var raw = context.GetVariable<object?>("TenantId")
                  ?? context.GetVariable<object?>("AccountId");
        return CoerceTenantId(raw);
    }

    private static string? CoerceTenantId(object? raw) => raw switch
    {
        Guid g when g != Guid.Empty => g.ToString(),
        string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p.ToString(),
        _ => null,
    };
}
