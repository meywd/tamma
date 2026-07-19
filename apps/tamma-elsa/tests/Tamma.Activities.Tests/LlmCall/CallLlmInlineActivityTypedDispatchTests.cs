using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Typed-dispatch prompt fix (Story 32-5 T6 follow-up) — on the typed dispatch
/// path (agentRole/action/variables workflow inputs) the legacy InputJson is
/// empty, so <see cref="CallLlmInlineActivity.BuildLlmCallRequest"/> used to
/// send <c>Prompt=""</c> / <c>Role="developer"</c> / <c>Action=null</c> /
/// <c>MaxTokens=4096</c>: the registry-rendered prompt (and the
/// registry-resolved MaxTokens) never reached the provider.
///
/// <para>These tests assert what WE send (no LLM involved):</para>
/// <list type="bullet">
///   <item>typed values present ⇒ the wire request carries the exact
///         role/action/rendered prompt/variables/registry MaxTokens;</item>
///   <item>typed values absent ⇒ the legacy InputJson mapping is
///         byte-identical to before;</item>
///   <item>wire capture — the rendered prompt text actually appears in the
///         outgoing <c>POST /api/v1/llm/call</c> body, captured by a stub
///         <see cref="HttpMessageHandler"/> under <see cref="TammaApiClient"/>.</item>
/// </list>
///
/// <para><b>Test level note.</b> There is no Elsa workflow-execution harness in
/// this test project (an <c>ActivityExecutionContext</c> cannot be constructed —
/// see <see cref="CallLlmInlineActivityThinClientTests"/>), so the wire-capture
/// test drives <see cref="TammaApiClient.CallLlmAsync"/> with the request built
/// by <see cref="CallLlmInlineActivity.BuildLlmCallRequest"/> — the exact
/// composition <c>ExecuteAsync</c> performs. The workflow-side wiring
/// (variables → activity props) is asserted structurally in
/// <c>Workflows/LlmCallWorkflowTests</c>.</para>
/// </summary>
[TestFixture]
public class CallLlmInlineActivityTypedDispatchTests
{
    private const string RenderedPrompt =
        "## Task\nPlan the implementation for issue #42.\n\n## Conventions\nuse tabs";

    /// <summary>A typed dispatch carries NO legacy InputJson — the parsed input
    /// is the all-defaults instance (UserPrompt="", Role="developer", MaxTokens=4096).</summary>
    private static LlmCallWorkflowInput EmptyTypedDispatchInput() => new();

    // =====================================================================
    // Typed values present → they reach the wire request
    // =====================================================================

    [Test]
    public void BuildLlmCallRequest_TypedValues_CarryRoleActionPromptVariablesAndRegistryMaxTokens()
    {
        var variablesJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["role"] = "architect",
            ["conventions"] = "use tabs",
            ["workItemJson"] = "{\"issue\":42}",
        });

        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            EmptyTypedDispatchInput(),
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-typed-1",
            agentRole: "architect",
            action: "plan-implementation",
            renderedPrompt: RenderedPrompt,
            variablesJson: variablesJson,
            registryMaxTokens: 8192);

        req.Role.Should().Be("architect",
            "the typed agentRole must win over the InputJson default ('developer')");
        req.Action.Should().Be("plan-implementation",
            "the typed action must win over the InputJson OperationName (null on typed dispatches)");
        req.Prompt.Should().Be(RenderedPrompt,
            "THE BUG — the registry-rendered prompt must be the wire Prompt (it was previously the empty InputJson UserPrompt)");
        req.Variables.Should().ContainKey("role");
        req.Variables.Should().ContainKey("conventions");
        req.Variables.Should().ContainKey("workItemJson");
        req.Params.MaxTokens.Should().Be(8192,
            "the registry-resolved MaxTokens must reach the provider params on the registry path");
    }

    [Test]
    public void BuildLlmCallRequest_TypedAction_RegistryMaxTokensUnset_FallsBackToInputMaxTokens()
    {
        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            EmptyTypedDispatchInput(),
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-typed-2",
            agentRole: "developer", action: "implement-feature",
            renderedPrompt: "rendered", variablesJson: null,
            registryMaxTokens: 0);

        req.Params.MaxTokens.Should().Be(4096,
            "registryMaxTokens=0 means 'unset' — the legacy input default (4096) applies");
    }

    [Test]
    public void BuildLlmCallRequest_TypedPromptWhitespace_FallsBackToInputUserPrompt()
    {
        var input = new LlmCallWorkflowInput { Role = "tester", UserPrompt = "legacy user prompt" };

        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            input,
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-typed-3",
            agentRole: null, action: null, renderedPrompt: "   ",
            variablesJson: null, registryMaxTokens: 0);

        req.Prompt.Should().Be("legacy user prompt",
            "a blank rendered prompt must not clobber the legacy UserPrompt");
    }

    // =====================================================================
    // Registry-path gating — empty typed action keeps the legacy
    // variables/MaxTokens (the empty-action fallback path writes a
    // hard-coded 4096 + a placeholder variables bag)
    // =====================================================================

    [Test]
    public void BuildLlmCallRequest_EmptyTypedAction_GatesVariablesAndRegistryMaxTokens()
    {
        // Legacy caller with an explicit MaxTokens; the workflow's empty-action
        // fallback path still writes registryMaxTokens=4096 and
        // variablesJson={"conventions":""} — neither may leak onto the wire.
        var input = new LlmCallWorkflowInput
        {
            Role = "tester",
            UserPrompt = "legacy user prompt",
            MaxTokens = 2048,
        };

        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            input,
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-legacy-1",
            agentRole: "tester",
            action: "",
            renderedPrompt: "legacy user prompt", // ResolvedPrompt echoes the fallback on this path
            variablesJson: "{\"conventions\":\"\"}",
            registryMaxTokens: 4096);

        req.Params.MaxTokens.Should().Be(2048,
            "the empty-action hard-coded 4096 must NOT override the caller's explicit input.MaxTokens");
        req.Variables.Should().BeEmpty(
            "the placeholder variables bag is only meaningful on the registry path (non-empty action)");
        req.Prompt.Should().Be("legacy user prompt");
        req.Role.Should().Be("tester");
        req.Action.Should().BeNull("empty typed action + empty OperationName ⇒ null on the wire");
    }

    // =====================================================================
    // Typed values absent → byte-identical legacy behavior
    // =====================================================================

    [Test]
    public void BuildLlmCallRequest_TypedValuesAbsent_KeepsLegacyMapping_ByteIdentical()
    {
        var input = new LlmCallWorkflowInput
        {
            Role = "tester",
            OperationName = "code_review",
            UserPrompt = "legacy prompt",
            MaxTokens = 2048,
            Temperature = 0.3,
            BudgetCapUsd = 1.5m,
        };

        // Old-signature call (no typed args) — the compatibility surface every
        // pre-existing caller uses.
        var legacyShaped = CallLlmInlineActivity.BuildLlmCallRequest(
            input, providerName: "anthropic", systemPrompt: "ignored", toolsJson: null,
            model: "claude-sonnet-4", enableToolLoop: false,
            toolLoopConfig: new ToolLoopConfig(), tenantIdRaw: null, correlationId: "wf-1");

        // The exact legacy mapping, field by field.
        legacyShaped.Role.Should().Be("tester");
        legacyShaped.Action.Should().Be("code_review");
        legacyShaped.Prompt.Should().Be("legacy prompt");
        legacyShaped.Variables.Should().BeEmpty();
        legacyShaped.Params.MaxTokens.Should().Be(2048);
        legacyShaped.Params.Temperature.Should().Be(0.3);
        legacyShaped.Params.BudgetCapUsd.Should().Be(1.5m);

        // And explicit "absent" typed values (null / "" / 0) produce the exact
        // same wire bytes as omitting them.
        var explicitAbsent = CallLlmInlineActivity.BuildLlmCallRequest(
            input, providerName: "anthropic", systemPrompt: "ignored", toolsJson: null,
            model: "claude-sonnet-4", enableToolLoop: false,
            toolLoopConfig: new ToolLoopConfig(), tenantIdRaw: null, correlationId: "wf-1",
            agentRole: null, action: "", renderedPrompt: null,
            variablesJson: null, registryMaxTokens: 0);

        JsonSerializer.Serialize(explicitAbsent)
            .Should().Be(JsonSerializer.Serialize(legacyShaped),
                "absent typed values must leave the legacy InputJson mapping byte-identical");
    }

    [Test]
    public void BuildLlmCallRequest_TypedValuesAbsent_EmptyRole_StillDefaultsToDeveloper()
    {
        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            new LlmCallWorkflowInput { Role = "", UserPrompt = "x" },
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-1");

        req.Role.Should().Be("developer",
            "the canonical wire default is preserved when no role is available anywhere");
    }

    // =====================================================================
    // Wire capture — the rendered prompt appears in the outgoing
    // POST /api/v1/llm/call body on the typed path
    // =====================================================================

    [Test]
    public async Task TypedDispatch_RenderedPrompt_AppearsInOutgoingPostBody()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, "{\"success\":true}");
        var client = BuildClient(handler);

        var request = CallLlmInlineActivity.BuildLlmCallRequest(
            EmptyTypedDispatchInput(),
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig(),
            tenantIdRaw: null, correlationId: "wf-wire-1",
            agentRole: "architect",
            action: "plan-implementation",
            renderedPrompt: "THE RENDERED REGISTRY PROMPT with {{conventions}} already interpolated",
            variablesJson: "{\"conventions\":\"use tabs\"}",
            registryMaxTokens: 8192);

        await client.CallLlmAsync(request);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/llm/call");

        var body = handler.LastRequestBody!;
        body.Should().Contain(
            "\"prompt\":\"THE RENDERED REGISTRY PROMPT with {{conventions}} already interpolated\"",
            "THE BUG — the registry-rendered prompt must be the outgoing user message");
        body.Should().NotContain("\"prompt\":\"\"",
            "the typed path must never send the empty legacy UserPrompt");
        body.Should().Contain("\"role\":\"architect\"");
        body.Should().Contain("\"action\":\"plan-implementation\"");
        body.Should().Contain("\"maxTokens\":8192");
        body.Should().Contain("\"conventions\":\"use tabs\"");
    }

    // =====================================================================
    // Test plumbing (mirrors CallLlmInlineActivityThinClientTests)
    // =====================================================================

    private static TammaApiClient BuildClient(CapturingHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
            })
            .Build();
        return new TammaApiClient(http, NullLogger<TammaApiClient>.Instance, cfg);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Captured during <see cref="SendAsync"/> — <c>PostAsync</c>
        /// disposes the request content on return.</summary>
        public string? LastRequestBody { get; private set; }

        public CapturingHttpMessageHandler(HttpStatusCode status, string json)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
