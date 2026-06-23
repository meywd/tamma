using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 32-5 (AC4) — runner-level coverage of the agentic tool loop, now the
/// single home of the loop (extracted VERBATIM from
/// <c>CallLlmInlineActivity.AgenticToolLoop</c>). Drives
/// <see cref="IInlineToolLoopRunner.RunAsync"/> end-to-end through a scripted
/// <see cref="IHttpClientFactory"/> and asserts the loop's behavioural
/// invariants: multi-turn round-trips, tool execution + result feedback,
/// cumulative token totals, completed-turn count, maxSteps exhaustion, and
/// LLM-output sanitization. <c>CallLlmInlineActivitySanitizationTests</c>
/// remains the unchanged regression net proving no behaviour drift in the move.
/// </summary>
[TestFixture]
public class InlineToolLoopRunnerTests
{
    private const string Model = "claude-sonnet-4-20250514";

    // =====================================================================
    // Happy path: tool_use → tool execution → end_turn (two turns)
    // =====================================================================

    [Test]
    public async Task RunAsync_MultiTurn_ExecutesTool_FeedsResultBack_ThenCompletes()
    {
        // Turn 1: LLM asks for a tool. Turn 2: LLM returns final text.
        var turn1 = BuildAnthropicToolUseResponse(
            text: "Let me look that up.",
            toolCalls: new() { ("tc-1", "file_read", new { path = "README.md" }) },
            inputTokens: 100, outputTokens: 40);
        var turn2 = BuildAnthropicEndTurnResponse("All done.", inputTokens: 120, outputTokens: 30);

        var factory = ScriptedFactory(turn1, turn2);
        var executor = ToolMock("file_read",
            (id, _) => Task.FromResult(new ToolExecutionResult(id, "file_read", true, "file contents here", 5)));
        var registry = new ToolExecutorRegistry(new[] { executor.Object }, NullLogger<ToolExecutorRegistry>.Instance);

        var runner = NewRunner(factory, registry);
        var tools = new List<ResolvedTool> { new() { Name = "file_read", Description = "Read a file" } };

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "system", "user", 4096, 0.7,
            tools, enableToolLoop: true, new ToolLoopConfig(), "corr-1", CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        result.Response.ResponseText.Should().Be("All done.");
        result.Turns.Should().Be(2, "one tool_use turn + one final end_turn turn");
        result.Exhausted.Should().BeFalse();
        // Cumulative token totals across both turns.
        result.InputTokens.Should().Be(220);
        result.OutputTokens.Should().Be(70);
        // The tool was actually invoked once.
        executor.Verify(e => e.ExecuteAsync("tc-1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =====================================================================
    // Single-turn: end_turn immediately (no tools)
    // =====================================================================

    [Test]
    public async Task RunAsync_EndTurnImmediately_OneTurn_NotExhausted()
    {
        var factory = ScriptedFactory(BuildAnthropicEndTurnResponse("hi", 10, 5));
        var runner = NewRunner(factory, registry: null);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "system", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-2", CancellationToken.None);

        result.Response.Success.Should().BeTrue();
        result.Turns.Should().Be(1);
        result.Exhausted.Should().BeFalse();
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
    }

    // =====================================================================
    // Exhaustion: every turn is tool_use, capped by MaxSteps
    // =====================================================================

    [Test]
    public async Task RunAsync_AllToolUse_HitsMaxSteps_ReportsExhausted()
    {
        // Both scripted responses are tool_use; MaxSteps = 2 forces exhaustion.
        var toolUse = BuildAnthropicToolUseResponse(
            "thinking", new() { ("tc-x", "file_read", new { path = "a" }) }, 50, 20);
        var factory = ScriptedFactory(toolUse, toolUse);
        var executor = ToolMock("file_read",
            (id, _) => Task.FromResult(new ToolExecutionResult(id, "file_read", true, "x", 1)));
        var registry = new ToolExecutorRegistry(new[] { executor.Object }, NullLogger<ToolExecutorRegistry>.Instance);

        var runner = NewRunner(factory, registry);
        var tools = new List<ResolvedTool> { new() { Name = "file_read", Description = "Read" } };

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "system", "user", 4096, 0.7,
            tools, enableToolLoop: true, new ToolLoopConfig { MaxSteps = 2 }, "corr-3", CancellationToken.None);

        result.Exhausted.Should().BeTrue();
        result.Turns.Should().Be(2);
        result.InputTokens.Should().Be(100);
        result.OutputTokens.Should().Be(40);
    }

    // =====================================================================
    // Output sanitization runs on tool output fed back to the LLM
    // =====================================================================

    [Test]
    public async Task RunAsync_SanitizesToolOutput_BeforeFeedingBackToLlm()
    {
        var turn1 = BuildAnthropicToolUseResponse(
            "", new() { ("tc-1", "file_read", new { path = "x" }) }, 10, 5);
        var turn2 = BuildAnthropicEndTurnResponse("done", 10, 5);

        // Capture the SECOND request body — it must carry the SANITIZED tool result.
        var handler = new SequencedCapturingHandler(turn1, turn2);
        var factory = FactoryFor(handler);
        var executor = ToolMock("file_read",
            (id, _) => Task.FromResult(new ToolExecutionResult(
                id, "file_read", true, "<script>alert(1)</script>hello", 1)));
        var registry = new ToolExecutorRegistry(new[] { executor.Object }, NullLogger<ToolExecutorRegistry>.Instance);

        var runner = NewRunner(factory, registry, sanitizer: new ContentSanitizer());
        var tools = new List<ResolvedTool> { new() { Name = "file_read", Description = "Read" } };

        await runner.RunAsync(
            "anthropic", Config(), Model, "system", "user", 4096, 0.7,
            tools, enableToolLoop: true, new ToolLoopConfig(), "corr-4", CancellationToken.None);

        handler.CapturedBodies.Should().HaveCount(2);
        handler.CapturedBodies[1].Should().NotContain("<script>", "tool output is sanitized before feedback");
        handler.CapturedBodies[1].Should().Contain("hello");
    }

    // =====================================================================
    // Provider HTTP failure breaks the loop and surfaces the status
    // =====================================================================

    [Test]
    public async Task RunAsync_ProviderHttpError_BreaksLoop_PreservesStatusCode()
    {
        var handler = new StatusHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var runner = NewRunner(FactoryFor(handler), registry: null);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "system", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-5", CancellationToken.None);

        result.Response.Success.Should().BeFalse();
        result.Response.HttpStatusCode.Should().Be(429, "the upstream status is preserved for RetryCheck");
        result.Turns.Should().Be(0);
        result.Exhausted.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static InlineToolLoopRunner NewRunner(
        IHttpClientFactory factory, IToolExecutorRegistry? registry, IContentSanitizer? sanitizer = null) =>
        new(NullLogger<InlineToolLoopRunner>.Instance, factory, configuration: null,
            sanitizer: sanitizer, toolRegistry: registry,
            toolCallValidator: null, contextCompactor: null,
            eventEmitter: null, parallelExecutor: null, credentialResolver: null);

    private static LlmProviderConfig Config() => new()
    {
        Name = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "test-key",
        TimeoutSeconds = 120
    };

    private static Mock<IToolExecutor> ToolMock(
        string name, Func<string, string, Task<ToolExecutionResult>> handler)
    {
        var mock = new Mock<IToolExecutor>();
        mock.SetupGet(e => e.ToolName).Returns(name);
        mock.SetupGet(e => e.Description).Returns($"{name} tool");
        mock.SetupGet(e => e.InputSchema).Returns(new Dictionary<string, object>());
        mock.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, string args, CancellationToken _) => handler(id, args));
        return mock;
    }

    private static IHttpClientFactory ScriptedFactory(params string[] responseBodies) =>
        FactoryFor(new SequencedCapturingHandler(responseBodies));

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    private static string BuildAnthropicToolUseResponse(
        string text, List<(string Id, string Name, object Input)> toolCalls,
        int inputTokens, int outputTokens)
    {
        var contentBlocks = new List<object>();
        if (!string.IsNullOrEmpty(text))
            contentBlocks.Add(new { type = "text", text });
        foreach (var (id, name, input) in toolCalls)
            contentBlocks.Add(new { type = "tool_use", id, name, input });

        return JsonSerializer.Serialize(new
        {
            content = contentBlocks,
            stop_reason = "tool_use",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            model = Model
        });
    }

    private static string BuildAnthropicEndTurnResponse(string text, int inputTokens, int outputTokens) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            model = Model
        });

    /// <summary>Returns scripted bodies in order; captures each request body for assertions.</summary>
    private sealed class SequencedCapturingHandler(params string[] bodies) : HttpMessageHandler
    {
        public List<string> CapturedBodies { get; } = new();
        private int _index;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var body = _index < bodies.Length ? bodies[_index] : bodies[^1];
            _index++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Always returns the given non-success status.</summary>
    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
