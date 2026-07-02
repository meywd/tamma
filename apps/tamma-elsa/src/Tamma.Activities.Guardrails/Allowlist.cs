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
    // Epic 38 (Phase 3, cutover COMPLETE) — the engine reaches all four integration domains
    // (GitHub, CI, JIRA, email) exclusively via Tamma.Api mediation over TammaApiClient, and
    // holds NO integration credential. The COMPOSITE `Tamma.Core.Interfaces.IIntegrationService`
    // and every focused variant (GitHub/Slack/CI/JIRA/email) are therefore DENIED as engine
    // injections — the earlier "still injected in 5 activities" exclusion is retired now that
    // those activities are thin-client (Phase 2). Reintroducing any as a ctor/field/property
    // fails the build (TAMMA001). The composite's Slack SEND methods stay additionally denied
    // at the call site via DeniedInvocationNames below (defence in depth).
    // ------------------------------------------------------------------------------------
    public static readonly ImmutableHashSet<string> InjectionDenylist = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Octokit.IGitHubClient",
        "Octokit.GitHubClient",
        "Tamma.Activities.AgentDispatch.IGitHubActionsClient",
        "Tamma.Core.Interfaces.IIntegrationService",
        "Tamma.Core.Interfaces.IGitHubIntegrationService",
        "Tamma.Core.Interfaces.ISlackIntegrationService",
        "Tamma.Core.Interfaces.ICIIntegrationService",
        "Tamma.Core.Interfaces.IJiraIntegrationService",
        "Tamma.Core.Interfaces.IEmailIntegrationService",
        // The engine must not resolve provider credentials post-32-5. (The LLM core that
        // legitimately holds it — InlineToolLoopRunner — now lives in the Tamma.Api assembly,
        // outside the analyzed engine surface, so no engine exemption is needed.)
        "Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver",
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
    // Correction 2 — denied Slack SEND invocations on ANY receiver (defence in depth). The
    // composite IIntegrationService INJECTION is now itself denied (Epic 38 Phase 3), but
    // these Slack send METHOD names stay denied at the call site so a re-introduced
    // engine-side Slack post — via the composite or any other receiver that exposes them —
    // is caught even if the injection pass is somehow bypassed.
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
        "Tamma.Activities.AgentDispatch.WebhookSignalRegistry");
}
