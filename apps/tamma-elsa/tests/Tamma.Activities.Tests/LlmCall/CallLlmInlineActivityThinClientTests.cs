using System.Net;
using System.Net.Http.Json;
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
/// Story 32-5 (T5, AC5/AC6) — the thin-client cutover of
/// <see cref="CallLlmInlineActivity"/>.
///
/// <para>The shim has no Elsa <c>ActivityExecutionContext</c> seam in tests (it
/// cannot be constructed), so — following the repo convention of extracting the
/// pure logic (cf. <see cref="ResolvePromptFromRegistryActivity.CallResolveAsync"/>)
/// — these tests exercise:</para>
/// <list type="bullet">
///   <item><see cref="TammaApiClient.CallLlmAsync"/> over a stubbed HTTP handler
///         (request shape + tenant header + response round-trip);</item>
///   <item><see cref="CallLlmInlineActivity.BuildLlmCallRequest"/> (Input props →
///         wire request);</item>
///   <item><see cref="CallLlmInlineActivity.MapResponseToVariables"/> /
///         <see cref="CallLlmInlineActivity.BuildTransportFailure"/> (wire
///         response → the SAME LastDiagnostic / LastResponse / ToolLoop* shapes
///         the legacy local path produced); and</item>
///   <item>the unchanged <c>LlmCallWorkflow.RetryCheck</c> predicate over the
///         <c>LastDiagnostic</c> the shim writes (proves AC6 — the boundary still
///         retries 429 / 0 and stops on 200).</item>
/// </list>
/// </summary>
[TestFixture]
public class CallLlmInlineActivityThinClientTests
{
    // =====================================================================
    // TammaApiClient.CallLlmAsync — POST /api/v1/llm/call
    // =====================================================================

    private static TammaApiClient BuildClient(StubHttpMessageHandler handler)
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

    [Test]
    public async Task CallLlmAsync_PostsToCorrectUrl_AndDeserializesResponse()
    {
        var payload = new
        {
            success = true,
            text = "the answer",
            usage = new { promptTokens = 100, completionTokens = 50, totalTokens = 150, toolLoopTokens = 0, toolLoopTurns = 0, toolLoopExhausted = false },
            credentialSource = "platform",
            providerUsed = "anthropic",
            modelUsed = "claude-sonnet-4",
            correlationId = "wf-1",
            durationMs = 1234,
        };
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
        var client = BuildClient(handler);

        var req = new LlmCallApiRequest { Role = "developer", Prompt = "x", CorrelationId = "wf-1" };
        var result = await client.CallLlmAsync(req);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Text.Should().Be("the answer");
        result.CredentialSource.Should().Be("platform");
        result.Usage.PromptTokens.Should().Be(100);
        result.Usage.CompletionTokens.Should().Be(50);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v1/llm/call");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Test]
    public async Task CallLlmAsync_SerializesRequestAsCamelCase()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"success\":true,\"correlationId\":\"wf-1\"}");
        var client = BuildClient(handler);

        var req = new LlmCallApiRequest
        {
            Role = "developer",
            Persona = "anthropic",
            Prompt = "do the thing",
            EnableToolLoop = true,
            ToolLoopConfig = new ToolLoopConfig { MaxSteps = 5 },
            Params = new LlmCallApiParams { MaxTokens = 2048, Temperature = 0.3 },
            CorrelationId = "wf-1",
        };
        await client.CallLlmAsync(req);

        var body = handler.LastRequestBody!;
        // camelCase property names (matches the API's CamelCase serializer).
        body.Should().Contain("\"role\":\"developer\"");
        body.Should().Contain("\"persona\":\"anthropic\"");
        body.Should().Contain("\"prompt\":\"do the thing\"");
        body.Should().Contain("\"enableToolLoop\":true");
        body.Should().Contain("\"correlationId\":\"wf-1\"");
        body.Should().Contain("\"params\":");
        body.Should().Contain("\"maxTokens\":2048");
        // never PascalCase.
        body.Should().NotContain("\"Role\":");
        body.Should().NotContain("\"CorrelationId\":");
    }

    [Test]
    public async Task CallLlmAsync_SetsTenantHeader_WhenTenantIdProvided()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"success\":true}");
        var client = BuildClient(handler);

        await client.CallLlmAsync(
            new LlmCallApiRequest { Role = "developer", Prompt = "x", CorrelationId = "wf-1" },
            tenantId: "11111111-1111-1111-1111-111111111111");

        handler.LastRequest!.Headers.TryGetValues("X-Tenant-Id", out var values).Should().BeTrue();
        values!.Single().Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Test]
    public async Task CallLlmAsync_ReturnsNull_OnRaw5xx()
    {
        // A genuine 5xx is nulled by PostAsync. The shim treats a null body as a
        // transient failure so RetryCheck advances (asserted separately below).
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");
        var client = BuildClient(handler);

        var result = await client.CallLlmAsync(
            new LlmCallApiRequest { Role = "developer", Prompt = "x", CorrelationId = "wf-1" });

        result.Should().BeNull();
    }

    // =====================================================================
    // BuildLlmCallRequest — Input props → wire request
    // =====================================================================

    [Test]
    public void BuildLlmCallRequest_MapsInputProps_IntoWireRequest()
    {
        var input = new LlmCallWorkflowInput
        {
            Role = "developer",
            OperationName = "implement-feature",
            UserPrompt = "fix the bug",
            MaxTokens = 2048,
            Temperature = 0.3,
            BudgetCapUsd = 1.5m,
            ModelOverrides = new() { ["anthropic"] = "claude-opus-4" },
        };
        var toolsJson = JsonSerializer.Serialize(new List<ResolvedTool>
        {
            new() { Name = "Read" }, new() { Name = "Write" },
        });

        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            input, providerName: "anthropic", systemPrompt: "You are a dev",
            toolsJson: toolsJson, model: "claude-opus-4",
            enableToolLoop: true, toolLoopConfig: new ToolLoopConfig { MaxSteps = 7 },
            tenantIdRaw: "22222222-2222-2222-2222-222222222222",
            correlationId: "wf-instance-123");

        req.Role.Should().Be("developer");
        req.Persona.Should().Be("anthropic", "the per-iteration provider name maps to Persona");
        req.Action.Should().Be("implement-feature");
        req.Prompt.Should().Be("fix the bug");
        req.Model.Should().Be("claude-opus-4");
        req.Tools.Should().Equal("Read", "Write");
        req.EnableToolLoop.Should().BeTrue();
        req.ToolLoopConfig!.MaxSteps.Should().Be(7);
        req.Params.MaxTokens.Should().Be(2048);
        req.Params.Temperature.Should().Be(0.3);
        req.Params.BudgetCapUsd.Should().Be(1.5m);
        req.TenantId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        req.CorrelationId.Should().Be("wf-instance-123");
        req.Variables.Should().ContainKey("systemPrompt").WhoseValue.Should().Be("You are a dev");
    }

    [Test]
    public void BuildLlmCallRequest_OmitsToolLoopConfig_WhenLoopDisabled()
    {
        var req = CallLlmInlineActivity.BuildLlmCallRequest(
            new LlmCallWorkflowInput { Role = "developer", UserPrompt = "x" },
            providerName: "anthropic", systemPrompt: null, toolsJson: null, model: null,
            enableToolLoop: false, toolLoopConfig: new ToolLoopConfig { MaxSteps = 7 },
            tenantIdRaw: null, correlationId: "wf-1");

        req.EnableToolLoop.Should().BeFalse();
        req.ToolLoopConfig.Should().BeNull("toolLoopConfig is only carried when the loop is enabled");
        req.Tools.Should().BeNull();
        req.TenantId.Should().BeNull("blank tenant id ⇒ single-user/platform scope");
        req.Variables.Should().NotContainKey("systemPrompt", "no system prompt ⇒ no override variable");
    }

    // =====================================================================
    // MapResponseToVariables — wire response → legacy variable shapes
    // =====================================================================

    private static readonly DateTime StartedAt = new(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void MapResponseToVariables_Success_WritesDiagnosticAndResponse_LikeLegacy()
    {
        var resp = new LlmCallApiResponse
        {
            Success = true,
            Text = "the answer",
            ModelUsed = "claude-sonnet-4",
            CredentialSource = "byok",
            Usage = new LlmCallUsageDto
            {
                PromptTokens = 120, CompletionTokens = 60, TotalTokens = 180,
                ToolLoopTokens = 180, ToolLoopTurns = 3, ToolLoopExhausted = false,
            },
            ToolCalls = new[]
            {
                new LlmCallToolCallDto { Name = "Read", Id = "tc-1", ArgumentsJson = "{\"path\":\"a\"}" },
            },
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            resp, providerName: "anthropic", model: "claude-sonnet-4",
            attemptNumber: 1, durationMs: 1234, startedAtUtc: StartedAt);

        // LastDiagnostic — drives RetryCheck/SuccessCheck.
        mapped.Diagnostic.ProviderName.Should().Be("anthropic");
        mapped.Diagnostic.Model.Should().Be("claude-sonnet-4");
        mapped.Diagnostic.AttemptNumber.Should().Be(1);
        mapped.Diagnostic.Succeeded.Should().BeTrue();
        mapped.Diagnostic.HttpStatusCode.Should().Be(200, "success with no upstream status ⇒ 200 (not in the transient set)");
        mapped.Diagnostic.ErrorMessage.Should().BeNull();
        mapped.Diagnostic.DurationMs.Should().Be(1234);
        mapped.Diagnostic.StartedAtUtc.Should().Be(StartedAt);
        mapped.Diagnostic.PromptTokens.Should().Be(120);
        mapped.Diagnostic.CompletionTokens.Should().Be(60);
        mapped.Diagnostic.CredentialSource.Should().Be("byok");

        // LastResponse — drives BuildSuccessOutput.
        mapped.Response.Success.Should().BeTrue();
        mapped.Response.ResponseText.Should().Be("the answer");
        mapped.Response.Model.Should().Be("claude-sonnet-4");
        mapped.Response.PromptTokens.Should().Be(120);
        mapped.Response.CompletionTokens.Should().Be(60);
        mapped.Response.HttpStatusCode.Should().Be(200);
        mapped.Response.ToolCalls.Should().ContainSingle();
        mapped.Response.ToolCalls![0].ToolName.Should().Be("Read");
        mapped.Response.ToolCalls![0].Id.Should().Be("tc-1");
        mapped.Response.ToolCalls![0].ArgumentsJson.Should().Be("{\"path\":\"a\"}");

        // ToolLoop* counters.
        mapped.ToolLoopTokens.Should().Be(180);
        mapped.ToolLoopTurns.Should().Be(3);
        mapped.ToolLoopExhausted.Should().BeFalse();
    }

    [Test]
    public void MapResponseToVariables_ProviderError429_PreservesHttpStatus_ForRetryCheck()
    {
        var resp = new LlmCallApiResponse
        {
            Success = false,
            CredentialSource = "platform",
            FailureCode = "PROVIDER_ERROR",
            FailureReason = "provider returned 429",
            HttpStatusCode = 429,
            Usage = new LlmCallUsageDto { PromptTokens = 7, CompletionTokens = 0 },
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            resp, "anthropic", "claude-sonnet-4", attemptNumber: 1, durationMs: 50, StartedAt);

        mapped.Diagnostic.Succeeded.Should().BeFalse();
        mapped.Diagnostic.HttpStatusCode.Should().Be(429, "the upstream status is preserved so RetryCheck retries");
        mapped.Diagnostic.ErrorMessage.Should().Be("PROVIDER_ERROR: provider returned 429");
        mapped.Diagnostic.PromptTokens.Should().Be(7, "usage accrued before the failure is preserved");
        mapped.Response.Success.Should().BeFalse();
        mapped.Response.HttpStatusCode.Should().Be(429);
    }

    [Test]
    public void MapResponseToVariables_CredentialUnavailable_NoUpstreamStatus_MapsToZero()
    {
        var resp = new LlmCallApiResponse
        {
            Success = false,
            FailureCode = "PROVIDER_CREDENTIAL_UNAVAILABLE",
            FailureReason = "no usable credential",
            HttpStatusCode = null,
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            resp, "anthropic", null, attemptNumber: 1, durationMs: 5, StartedAt);

        mapped.Diagnostic.Succeeded.Should().BeFalse();
        mapped.Diagnostic.HttpStatusCode.Should().Be(0, "a failure with no upstream status ⇒ transient 0");
        mapped.Diagnostic.ErrorMessage.Should().Be("PROVIDER_CREDENTIAL_UNAVAILABLE: no usable credential");
    }

    [Test]
    public void MapResponseToVariables_LoopExhausted_CarriesExhaustedFlag()
    {
        var resp = new LlmCallApiResponse
        {
            Success = false,
            FailureCode = "LOOP_EXHAUSTED",
            FailureReason = "max steps reached",
            HttpStatusCode = 0,
            Usage = new LlmCallUsageDto
            {
                PromptTokens = 500, CompletionTokens = 200,
                ToolLoopTokens = 700, ToolLoopTurns = 20, ToolLoopExhausted = true,
            },
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            resp, "anthropic", "claude-sonnet-4", attemptNumber: 1, durationMs: 9000, StartedAt);

        mapped.Diagnostic.Succeeded.Should().BeFalse();
        mapped.Diagnostic.HttpStatusCode.Should().Be(0);
        mapped.ToolLoopTokens.Should().Be(700);
        mapped.ToolLoopTurns.Should().Be(20);
        mapped.ToolLoopExhausted.Should().BeTrue();
    }

    [Test]
    public void BuildTransportFailure_WritesFailedDiagnostic_WithTransientZeroStatus()
    {
        var mapped = CallLlmInlineActivity.BuildTransportFailure(
            "anthropic", "claude-sonnet-4", attemptNumber: 1, durationMs: 10, StartedAt);

        mapped.Diagnostic.Succeeded.Should().BeFalse();
        mapped.Diagnostic.HttpStatusCode.Should().Be(0, "a null body (transport/5xx) is transient → RetryCheck advances");
        mapped.Response.Success.Should().BeFalse();
        mapped.ToolLoopExhausted.Should().BeFalse();
    }

    // =====================================================================
    // AC6 — the unchanged workflow boundary still retries/stops correctly
    // on the LastDiagnostic the shim writes (RetryCheck predicate).
    // =====================================================================

    /// <summary>The exact transient predicate from <c>LlmCallWorkflow.RetryCheck</c>:
    /// deserialize the serialized <c>LastDiagnostic</c> and decide retry vs stop.</summary>
    private static bool RetryCheckPredicate(string lastDiagnosticJson)
    {
        if (string.IsNullOrWhiteSpace(lastDiagnosticJson)) return false;
        var diag = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(lastDiagnosticJson);
        if (diag == null) return false;
        var code = diag.HttpStatusCode;
        return code == 429 || code == 502 || code == 503 || code == 504 || code == 0;
    }

    /// <summary>The exact success predicate from <c>LlmCallWorkflow.SuccessCheck</c>.</summary>
    private static bool SuccessCheckPredicate(string lastDiagnosticJson)
    {
        if (string.IsNullOrWhiteSpace(lastDiagnosticJson)) return false;
        var diag = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(lastDiagnosticJson);
        return diag?.Succeeded == true;
    }

    [Test]
    public void RetryCheck_Over_ShimWrittenDiagnostic_Retries429_StopsOn200()
    {
        // 429 failure → the chain retries (AC6).
        var failure = CallLlmInlineActivity.MapResponseToVariables(
            new LlmCallApiResponse
            {
                Success = false, FailureCode = "PROVIDER_ERROR",
                FailureReason = "provider returned 429", HttpStatusCode = 429,
            },
            "anthropic", "claude-sonnet-4", 1, 50, StartedAt);
        var failureJson = JsonSerializer.Serialize(failure.Diagnostic);

        RetryCheckPredicate(failureJson).Should().BeTrue("429 is transient — RetryCheck advances");
        SuccessCheckPredicate(failureJson).Should().BeFalse();

        // 200 success → the chain stops (success).
        var success = CallLlmInlineActivity.MapResponseToVariables(
            new LlmCallApiResponse { Success = true, Text = "ok", ModelUsed = "m" },
            "anthropic", "m", 1, 50, StartedAt);
        var successJson = JsonSerializer.Serialize(success.Diagnostic);

        RetryCheckPredicate(successJson).Should().BeFalse("200 is not in the transient set");
        SuccessCheckPredicate(successJson).Should().BeTrue("a successful run stops the loop");
    }

    [Test]
    public void RetryCheck_Over_TransportFailureDiagnostic_Retries()
    {
        var transport = CallLlmInlineActivity.BuildTransportFailure("anthropic", "m", 1, 10, StartedAt);
        var json = JsonSerializer.Serialize(transport.Diagnostic);

        RetryCheckPredicate(json).Should().BeTrue("a transport/5xx null body maps to 0 → transient → retry");
        SuccessCheckPredicate(json).Should().BeFalse();
    }

    // =====================================================================
    // Test double — canned HTTP handler (mirrors TammaApiClientTests)
    // =====================================================================

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Captured request body — read during <see cref="SendAsync"/>
        /// because <c>PostAsync</c> disposes the request (and its content) on
        /// return, so the body is unreadable afterwards.</summary>
        public string? LastRequestBody { get; private set; }

        public StubHttpMessageHandler(HttpStatusCode status, string json)
        {
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public StubHttpMessageHandler(Exception exception) => _exception = exception;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (_exception is not null) throw _exception;
            return _response!;
        }
    }
}
