using System;
using System.Collections.Immutable;

namespace Tamma.Activities.Guardrails;

/// <summary>
/// Story 38-4 — the sanctioned-seam allowlist, the vendor-credential injection denylist,
/// the denied Slack-send invocations, and the design-§5.3 exemptions, expressed as DATA
/// so the guardrail is forward-compatible (Epic 35 adds Stripe → add its client type here;
/// no analyzer rewrite).
/// </summary>
internal static class Allowlist
{
    /// <summary>The engine surface the analyzer targets. <c>Tamma.Api</c> is deliberately
    /// EXCLUDED — the API is supposed to hold credentials and call vendors.</summary>
    public static bool IsEngineSurface(string? assemblyName) =>
        assemblyName is "Tamma.Activities" or "Tamma.ElsaServer";

    /// <summary>The sanctioned engine→API seam (documentation only). The analyzer never
    /// flags HTTP whose host is not a statically-resolvable EXTERNAL host, so
    /// <c>TammaApiClient</c>'s config-driven base URL and the <c>Engine:CallbackUrl</c>
    /// interpolated hosts are never flagged.</summary>
    public const string ApiClientType = "Tamma.Activities.LlmCall.TammaApiClient";

    /// <summary>The internal-endpoint host key (<c>TriggerCIActivity</c>'s pattern).</summary>
    public const string CallbackHostConfigKey = "Engine:CallbackUrl";

    // ------------------------------------------------------------------------------------
    // Vendor-credential INJECTION denylist — ctor parameters / fields / properties whose
    // type is one of these re-introduces an in-process external credential (rule-1
    // violation, design §1.2 audit table). Post-32-5/38-1/38-2/38-3 there are ZERO engine
    // INJECTIONS of these: the ADL git service and the GitHub-Actions client are METHOD
    // PARAMETERS of the reused static cores (called by Tamma.Api's mediation services), not
    // ctor/field injections; Slack/GitHub effects route through TammaApiClient.
    //
    // DELIBERATELY EXCLUDED — the COMPOSITE `Tamma.Core.Interfaces.IIntegrationService`:
    //   it is STILL legitimately injected in 5 engine activities (MergeCompleteActivity,
    //   DiagnoseBlockerActivity, CodeReviewActivity, MergeAndCompleteReviewActivity,
    //   DeliverQuestionsActivity) for NON-Slack ops (GitHub merge/CI, JIRA, email) that
    //   Epic 38 did NOT mediate. Denying its INJECTION would fail the build on clean main.
    //   Those un-migrated GitHub/CI/JIRA/email uses are a TRACKED FOLLOW-UP; once they move
    //   to Tamma.Api endpoints, add IIntegrationService here. The Slack hole this exclusion
    //   would otherwise open is closed by DeniedInvocationNames below (Correction 2) — the
    //   composite's Slack SEND methods are denied at the call site.
    // ------------------------------------------------------------------------------------
    public static readonly ImmutableHashSet<string> InjectionDenylist = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Octokit.IGitHubClient",
        "Octokit.GitHubClient",
        "Tamma.Activities.AgentDispatch.IGitHubActionsClient",
        "Tamma.Core.Interfaces.IGitHubIntegrationService",
        "Tamma.Core.Interfaces.ISlackIntegrationService",
        // The engine must not resolve provider credentials post-32-5. (The one legitimate
        // holder, InlineToolLoopRunner, is the Tamma.Api-executed LLM core — exempted below.)
        "Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver",
        // FIX M1 — the inline tool-loop runner is the Tamma.Api-executed LLM core: it holds
        // the direct anthropic/openai calls + IProviderCredentialResolver. It is DI-registered
        // ONLY in Tamma.Api (Program.cs). INJECTING it into any engine activity would drive a
        // credentialed LLM call from a workflow STEP — a rule-1 violation — so its injection is
        // denied here. NB no conflict with the whole-type EXEMPTION below: the exemption keys on
        // the ANALYZED (owner) type, so it only suppresses the runner's OWN members (its
        // IProviderCredentialResolver ctor injection); a DIFFERENT engine type injecting the
        // runner has a non-exempt owner and is correctly flagged.
        "Tamma.Activities.LlmCall.IInlineToolLoopRunner",
        "Tamma.Activities.LlmCall.InlineToolLoopRunner",
        // Forward-compatible (Epic 35 billing / future vendor SDKs) — harmless string data
        // until such a type is actually referenced by the engine:
        "SlackNet.ISlackApiClient",
        "Stripe.StripeClient",
        "Stripe.IStripeClient");

    // ------------------------------------------------------------------------------------
    // FIX I1 — service-locator method names. Resolving a denylisted vendor-credential type
    // via the DI container (Microsoft.Extensions.DependencyInjection's
    // GetService/GetRequiredService/GetKeyedService/GetRequiredKeyedService, or Elsa's
    // ActivityExecutionContext.GetService<T>()) inside a method BODY is not a ctor param /
    // field / property, so passes (2) miss it — yet it is the most natural way a future dev
    // re-introduces a credentialed vendor effect. A call to one of these whose type argument
    // (generic <T> OR the typeof(T)/Type-arg overload) is on InjectionDenylist is flagged.
    // Keyed on the DENYLISTED type argument, so a legitimate context.GetService<TammaApiClient>()
    // / GetService<IConfiguration>() never trips — only a denylisted vendor type does.
    // ------------------------------------------------------------------------------------
    public static readonly ImmutableHashSet<string> ServiceLocatorMethodNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "GetService",
        "GetRequiredService",
        "GetKeyedService",
        "GetRequiredKeyedService");

    // ------------------------------------------------------------------------------------
    // Correction 2 — denied Slack SEND invocations on ANY receiver. Because the composite
    // IIntegrationService INJECTION is allowed (for GitHub/CI/JIRA/email), also deny its
    // Slack send METHODS at the call site so a re-introduced engine-side Slack post via the
    // composite is still caught. Its GitHub/CI methods stay allowed (the tracked follow-up).
    // ------------------------------------------------------------------------------------
    public static readonly ImmutableHashSet<string> DeniedInvocationNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "SendSlackMessageAsync",
        "SendSlackDirectMessageAsync");

    /// <summary>HttpClient send-method names (instance + <c>System.Net.Http.Json</c>
    /// extensions) whose statically-resolvable external target host is a violation.</summary>
    public static readonly ImmutableHashSet<string> HttpSendMethodNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "PostAsync", "PostAsJsonAsync",
        "PutAsync", "PutAsJsonAsync",
        "PatchAsync", "PatchAsJsonAsync",
        "GetAsync", "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync", "GetFromJsonAsync",
        "DeleteAsync", "DeleteFromJsonAsync",
        "SendAsync",
        // FIX M3 — net5+ synchronous HttpClient.Send(HttpRequestMessage). Gated on the
        // containing type being HttpClient/HttpMessageInvoker (IsHttpSendMethod), so an
        // unrelated `.Send()` on a bus/actor is not matched; still only a resolvable external
        // host (including one dug out of an inline `new HttpRequestMessage(m, "https://...")`)
        // is flagged.
        "Send");

    /// <summary>The types that own the HTTP send methods (so a same-named method on an
    /// unrelated type — a bus <c>SendAsync</c>, a cache <c>GetAsync</c> — is not flagged).</summary>
    public static readonly ImmutableHashSet<string> HttpClientTypeNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpMessageInvoker",
        "System.Net.Http.Json.HttpClientJsonExtensions",
        "System.Net.Http.HttpClientJsonExtensions");

    // ------------------------------------------------------------------------------------
    // EXEMPT types (design §5.3) — a local process / local filesystem / inbound signal is
    // NOT an external API call. Matched by the analyzed type's own FQN, or any base type /
    // implemented interface FQN.
    // ------------------------------------------------------------------------------------
    public static readonly ImmutableHashSet<string> ExemptTypeNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        // Local single-user CLI agents (design §5.3). No C# implementation exists today
        // (CLI providers are the TS harness) — kept as forward-compatible data that matches
        // an implementor's interface list if one is ever added to the engine surface.
        "Tamma.Providers.ICLIAgentProvider",
        // In-engine LOCAL tools — local filesystem / local process, not external HTTP:
        "Tamma.Activities.LlmCall.Tools.FileReadTool",
        "Tamma.Activities.LlmCall.Tools.ShellExecuteTool",
        "Tamma.Activities.LlmCall.Tools.GitOperationsTool",
        // Inbound webhook-signal store (inbound; no outbound call):
        "Tamma.Activities.AgentDispatch.WebhookSignalRegistry",
        // TRACKED FOLLOW-UP — the API-plane LLM execution core. It is DI-registered and
        // executed ONLY in Tamma.Api (ManagedAgent); it lives in the Tamma.Activities
        // ASSEMBLY purely for code-organisation (32-5 extracted it verbatim from
        // CallLlmInlineActivity). It ctor-injects IProviderCredentialResolver and holds the
        // direct anthropic/openai calls = the sanctioned Tamma.Api LLM core, NOT an
        // engine-executed path. Exempt so it does not false-positive the engine guardrail.
        // Follow-up: physically relocate InlineToolLoopRunner (+ IInlineToolLoopRunner) into
        // Tamma.Api, then delete this line (its IProviderCredentialResolver injection is
        // then correctly outside the engine surface).
        "Tamma.Activities.LlmCall.InlineToolLoopRunner");
}
