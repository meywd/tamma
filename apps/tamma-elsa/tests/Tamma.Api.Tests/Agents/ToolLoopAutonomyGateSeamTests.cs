using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Epic 43 Seam B — the autonomy gate INSIDE the real
/// <see cref="InlineToolLoopRunner.RunAsync"/> path, driven end-to-end with a
/// scripted fake HTTP handler (the <c>InlineToolLoopRunnerRepairTests</c>
/// harness). Proves the siting contract:
/// <list type="bullet">
/// <item>a denied call NEVER reaches its executor;</item>
/// <item>the denial goes back to the model as a TOOL RESULT in the same
/// conversation (the existing rejected-call machinery — no exception);</item>
/// <item>the gate runs even with NO validator wired (it is not nested in the
/// optional validator block);</item>
/// <item>allowed calls are untouched, including under the shipped
/// behaviour-preserving defaults.</item>
/// </list>
/// </summary>
[TestFixture]
public class ToolLoopAutonomyGateSeamTests
{
    private const string Model = "claude-sonnet-4-20250514";

    [Test]
    public async Task A_denied_tool_call_is_not_executed_and_feeds_back_as_a_tool_result()
    {
        var executed = new List<string>();
        var registry = RegistryRecording(executed);
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "shell_execute", """{"command":"deploy.sh"}"""),
                    ("tc-2", "file_read", """{"path":"README.md"}""")),
            EndTurn("done", 5, 2));

        // Fake gate: deny shell_execute, allow everything else.
        var gate = new Mock<IToolLoopAutonomyGate>();
        gate.Setup(g => g.Evaluate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string? _) => name == "shell_execute"
                ? new ToolLoopGateDecision(
                    ToolLoopGateOutcome.Denied,
                    new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, "shell_execute"),
                    AutonomyDial.AlwaysHuman, AutonomyDial.Min, "always-human")
                : new ToolLoopGateDecision(ToolLoopGateOutcome.Allowed, null, null, AutonomyDial.Min, "at-or-above-min-autonomy"));

        var runner = NewRunner(handler, gate.Object, registry);
        var result = await RunAsync(runner);

        executed.Should().Equal(new[] { "file_read" },
            "the denied shell_execute call must never reach its executor; the allowed call runs");

        // The second HTTP request carries BOTH tool results back to the model —
        // the denial as an ordinary tool result, not an exception.
        handler.CapturedBodies.Should().HaveCount(2);
        handler.CapturedBodies[1].Should().Contain("denied by autonomy policy");
        handler.CapturedBodies[1].Should().Contain("tc-1");
        handler.CapturedBodies[1].Should().Contain("tc-2");

        result.Response.Success.Should().BeTrue("the loop completes normally after a denial");
        result.Turns.Should().Be(2);
    }

    [Test]
    public async Task The_gate_runs_with_no_validator_wired()
    {
        // The siting decision: NOT nested inside the optional validator block.
        // This harness wires no IToolCallValidator at all — the denial must
        // still happen.
        var executed = new List<string>();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_write", """{"path":"x","content":"y"}""")),
            EndTurn("done", 5, 2));

        var gate = new Mock<IToolLoopAutonomyGate>();
        gate.Setup(g => g.Evaluate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new ToolLoopGateDecision(
                ToolLoopGateOutcome.Denied, null, AutonomyDial.Max, AutonomyDial.Min, "below-min-autonomy"));

        var runner = NewRunner(handler, gate.Object, RegistryRecording(executed));
        await RunAsync(runner);

        executed.Should().BeEmpty("the gate must deny even when the optional validator is absent");
        handler.CapturedBodies[1].Should().Contain("denied by autonomy policy");
    }

    [Test]
    public async Task The_real_gate_with_shipped_defaults_changes_nothing()
    {
        // Behaviour preservation (epic D1): the production gate at the shipped
        // dial default allows every shipped tool — the loop output is
        // byte-identical to a pre-gate run.
        var executed = new List<string>();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_read", """{"path":"README.md"}"""),
                    ("tc-2", "shell_execute", """{"command":"echo hi"}""")),
            EndTurn("done", 5, 2));

        var runner = NewRunner(handler, new CatalogDefaultToolLoopAutonomyGate(), RegistryRecording(executed));
        var result = await RunAsync(runner);

        executed.Should().Equal(new[] { "file_read", "shell_execute" });
        handler.CapturedBodies[1].Should().NotContain("denied by autonomy policy");
        result.Response.Success.Should().BeTrue();
    }

    [Test]
    public async Task An_always_human_threshold_denies_through_the_real_gate()
    {
        // The REAL gate implementation (rehearsal threshold seam), end-to-end:
        // AlwaysHuman on tool:shell_execute blocks at the shipped dial, and the
        // model is told a person is required — while file_read still runs.
        var executed = new List<string>();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "shell_execute", """{"command":"rm -rf /prod"}"""),
                    ("tc-2", "file_read", """{"path":"README.md"}""")),
            EndTurn("done", 5, 2));

        var gate = new CatalogDefaultToolLoopAutonomyGate(
            dial: AcceptanceDefaults.DefaultAutonomyLevel,
            minAutonomyOverride: d => d.Key.Key == "shell_execute"
                ? AutonomyDial.AlwaysHuman
                : d.DefaultMinAutonomy);

        var runner = NewRunner(handler, gate, RegistryRecording(executed));
        await RunAsync(runner);

        executed.Should().Equal(new[] { "file_read" });
        handler.CapturedBodies[1].Should().Contain("always require a person");
        handler.CapturedBodies[1].Should().Contain("tool:shell_execute");
    }

    [Test]
    public void The_gate_is_a_required_constructor_dependency()
    {
        // The epic's binding decision: REQUIRED-constructor-injected, never
        // optional-nullable like the rest of the loop's collaborators.
        var act = () => new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, null, null, null, autonomyGate: null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("autonomyGate");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<InlineToolLoopResult> RunAsync(InlineToolLoopRunner runner) =>
        await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-gate",
            repair: null, CancellationToken.None);

    /// <summary>A registry of stub executors that records execution order.</summary>
    private static IToolExecutorRegistry RegistryRecording(List<string> executed)
    {
        var executors = new IToolExecutor[]
        {
            new StubTool("file_read", executed),
            new StubTool("file_write", executed),
            new StubTool("shell_execute", executed),
        };
        return new ToolExecutorRegistry(executors, NullLogger<ToolExecutorRegistry>.Instance);
    }

    private sealed class StubTool(string name, List<string> executed) : IToolExecutor
    {
        public string ToolName => name;
        public string Description => $"stub {name}";
        public Dictionary<string, object> InputSchema => new();
        public Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
        {
            executed.Add(name);
            return Task.FromResult(new ToolExecutionResult(toolCallId, name, true, $"{name} ok", 1));
        }
    }

    private static InlineToolLoopRunner NewRunner(
        HttpMessageHandler handler, IToolLoopAutonomyGate gate, IToolExecutorRegistry registry)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, factory.Object, configuration: null,
            sanitizer: null, autonomyGate: gate, toolRegistry: registry);
    }

    private static LlmProviderConfig Config() => new()
    {
        Name = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "test-key",
        TimeoutSeconds = 120,
    };

    private static string EndTurn(string text, int inputTokens, int outputTokens) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            model = Model,
        });

    private static string ToolUse(params (string Id, string Name, string ArgsJson)[] calls) =>
        JsonSerializer.Serialize(new
        {
            content = calls.Select(c => new
            {
                type = "tool_use",
                id = c.Id,
                name = c.Name,
                input = JsonSerializer.Deserialize<JsonElement>(c.ArgsJson),
            }).ToArray<object>(),
            stop_reason = "tool_use",
            usage = new { input_tokens = 10, output_tokens = 5 },
            model = Model,
        });

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
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
