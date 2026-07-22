using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 39-9 (AC1, AC2, AC9) — the deterministic repair ring driven end-to-end
/// through the real <see cref="InlineToolLoopRunner"/> with a scripted fake HTTP
/// handler and a FAKE validator delegate (registry-free — D2). Proves in-conversation
/// repair, re-validate-and-exit-on-pass, the cap, the gate, transport orthogonality,
/// MaxSteps isolation, and the null-plan no-op.
/// </summary>
[TestFixture]
public class InlineToolLoopRunnerRepairTests
{
    private const string Model = "claude-sonnet-4-20250514";

    // (a) invalid → valid: the repair turn fixes the document.
    [Test]
    public async Task Repair_InvalidThenValid_PreservesConversation_ContentValidTrue()
    {
        var produce = EndTurn("PRODUCED-DOC", 100, 40);
        var repaired = EndTurn("REPAIRED-DOC", 60, 20);
        var handler = new SequencedCapturingHandler(produce, repaired);
        var runner = NewRunner(handler);

        // Verdicts consumed in order: produce invalid, repair valid.
        var validate = Validator(Invalid("MISSING_FIELD", "Field 'x' is required."), Valid());
        var plan = new RepairRingPlan("decomposition", validate, RepairEnabled: true, MaxRepairTurns: 1);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "SYS-PROMPT", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-a", plan, CancellationToken.None);

        result.ContentValid.Should().BeTrue();
        result.RepairTurns.Should().Be(1);
        result.RepairHistory.Select(h => (h.Turn, h.Valid))
            .Should().Equal((0, false), (1, true));
        result.Response.ResponseText.Should().Be("REPAIRED-DOC");
        // Token totals include BOTH the produce and the repair turn.
        result.InputTokens.Should().Be(160);
        result.OutputTokens.Should().Be(60);

        // The SECOND HTTP request preserves the conversation: original system prompt,
        // the produced document, AND the composed repair message.
        handler.CapturedBodies.Should().HaveCount(2);
        var repairBody = handler.CapturedBodies[1];
        repairBody.Should().Contain("SYS-PROMPT", "the system prompt is preserved (not restarted)");
        repairBody.Should().Contain("PRODUCED-DOC", "the produced document rides in the conversation");
        repairBody.Should().Contain("did not pass validation", "the harness repair message is appended");
        repairBody.Should().Contain("MISSING_FIELD", "the domain-phrased violation is fed back verbatim");
    }

    // (b) always-invalid: stops at the cap, ContentValid false, history 1 + cap.
    [Test]
    public async Task Repair_AlwaysInvalid_StopsAtCap_ContentValidFalse()
    {
        var handler = new SequencedCapturingHandler(
            EndTurn("D0", 10, 5), EndTurn("D1", 10, 5), EndTurn("D2", 10, 5));
        var runner = NewRunner(handler);

        var validate = Validator(
            Invalid("E", "always bad"), Invalid("E", "always bad"), Invalid("E", "always bad"));
        var plan = new RepairRingPlan("decomposition", validate, RepairEnabled: true, MaxRepairTurns: 2);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-b", plan, CancellationToken.None);

        result.ContentValid.Should().BeFalse();
        result.RepairTurns.Should().Be(2, "bounded by the cap of 2");
        result.RepairHistory.Should().HaveCount(3, "turn 0 + cap repair turns");
        result.RepairHistory.Should().OnlyContain(h => !h.Valid);
    }

    // (c) gate off: exactly ONE HTTP call, ContentValid false, RepairTurns 0.
    [Test]
    public async Task Repair_GateOff_NoRepairTurn_OneHttpCall()
    {
        var handler = new SequencedCapturingHandler(EndTurn("D0", 10, 5));
        var runner = NewRunner(handler);

        var validate = Validator(Invalid("E", "bad"));
        var plan = new RepairRingPlan("decomposition", validate, RepairEnabled: false, MaxRepairTurns: 1);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-c", plan, CancellationToken.None);

        result.ContentValid.Should().BeFalse();
        result.RepairTurns.Should().Be(0, "the gate is off ⇒ zero extra turns (AC9)");
        result.RepairHistory.Should().HaveCount(1, "only the turn-0 validation");
        handler.CapturedBodies.Should().HaveCount(1, "no repair re-invocation");
    }

    // (d) transport 503 during the repair turn → provider failure, preserved status.
    [Test]
    public async Task Repair_Transport503DuringRepair_SurfacesAsProviderFailure()
    {
        var handler = new FirstOkThen503(EndTurn("D0", 10, 5), "rate limited");
        var runner = NewRunner(handler);

        var validate = Validator(Invalid("E", "bad")); // only turn 0 validates
        var plan = new RepairRingPlan("decomposition", validate, RepairEnabled: true, MaxRepairTurns: 1);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-d", plan, CancellationToken.None);

        result.Response.Success.Should().BeFalse("a transport failure during repair is a provider failure");
        result.Response.HttpStatusCode.Should().Be(503, "the upstream status is preserved (orthogonality)");
    }

    // (e) repair turns leave the tool-loop MaxSteps accounting untouched.
    [Test]
    public async Task Repair_DoesNotConsumeToolLoopTurnsOrExhaustion()
    {
        var handler = new SequencedCapturingHandler(EndTurn("D0", 10, 5), EndTurn("D1", 10, 5));
        var runner = NewRunner(handler);

        var validate = Validator(Invalid("E", "bad"), Valid());
        var plan = new RepairRingPlan("decomposition", validate, RepairEnabled: true, MaxRepairTurns: 1);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig { MaxSteps = 20 }, "corr-e", plan,
            CancellationToken.None);

        result.Turns.Should().Be(1, "only the single produce turn counts as a tool-loop turn");
        result.Exhausted.Should().BeFalse("repair turns never drive MaxSteps exhaustion");
        result.RepairTurns.Should().Be(1);
    }

    // (f) null plan → repair fields default; behaviour byte-identical to today.
    [Test]
    public async Task NullPlan_LeavesRepairFieldsDefault_ByteIdenticalBehaviour()
    {
        var handler = new SequencedCapturingHandler(EndTurn("only", 10, 5));
        var runner = NewRunner(handler);

        var result = await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, new ToolLoopConfig(), "corr-f", repair: null,
            CancellationToken.None);

        result.ContentValid.Should().BeNull();
        result.RepairTurns.Should().Be(0);
        result.RepairHistory.Should().BeEmpty();
        result.Response.ResponseText.Should().Be("only");
        handler.CapturedBodies.Should().HaveCount(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static DocumentValidationResult Valid() => DocumentValidationResult.Valid();

    private static DocumentValidationResult Invalid(string code, string message) =>
        DocumentValidationResult.Invalid(new DocumentViolation(code, message));

    /// <summary>A stateful validator delegate consuming the supplied verdicts in order
    /// (the last verdict repeats if more calls arrive).</summary>
    private static Func<string, DocumentValidationResult> Validator(params DocumentValidationResult[] verdicts)
    {
        var i = 0;
        return _ =>
        {
            var v = verdicts[Math.Min(i, verdicts.Length - 1)];
            i++;
            return v;
        };
    }

    private static InlineToolLoopRunner NewRunner(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, factory.Object, configuration: null,
            sanitizer: null, toolRegistry: null, toolCallValidator: null, contextCompactor: null,
            eventEmitter: null, parallelExecutor: null, credentialResolver: null);
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

    /// <summary>First call returns 200 with the produce body; the second (repair
    /// re-invocation) returns 503.</summary>
    private sealed class FirstOkThen503(string firstBody, string errorBody) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isFirst = _index++ == 0;
            var msg = isFirst
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(firstBody, System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
                };
            return Task.FromResult(msg);
        }
    }
}
