using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.ToolExecution;
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
        // At the shipped dial default (70) every tool whose zone level is AT/BELOW
        // 70 runs unimpeded — the loop output is byte-identical to a pre-gate run
        // for those tools. (shell_execute sits at 80 and is now gated at the
        // default dial — see An_always_human_threshold_denies_through_the_real_gate
        // and Shell_execute_is_denied_at_the_default_dial; here we exercise the
        // still-automated file tools.)
        var executed = new List<string>();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_read", """{"path":"README.md"}"""),
                    ("tc-2", "file_write", """{"path":"x.txt","content":"hi"}""")),
            EndTurn("done", 5, 2));

        var runner = NewRunner(handler, new CatalogDefaultToolLoopAutonomyGate(), RegistryRecording(executed));
        var result = await RunAsync(runner);

        executed.Should().Equal(new[] { "file_read", "file_write" });
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
    public async Task A_denied_tool_call_is_excluded_from_the_parallel_execution_path_too()
    {
        // 43-4 review (2026-07-29): the earlier seam tests only exercised the
        // SEQUENTIAL fork. The gate sits pre-fork, so a denial must equally
        // keep the call out of ParallelToolExecutor's batch — proven here
        // end-to-end with EnableParallelTools and a real parallel executor.
        var executed = new List<string>();
        var registry = RegistryRecording(executed);
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "shell_execute", """{"command":"deploy.sh"}"""),
                    ("tc-2", "file_read", """{"path":"README.md"}"""),
                    ("tc-3", "file_write", """{"path":"a","content":"b"}""")),
            EndTurn("done", 5, 2));

        var gate = new Mock<IToolLoopAutonomyGate>();
        gate.Setup(g => g.Evaluate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string? _) => name == "shell_execute"
                ? new ToolLoopGateDecision(
                    ToolLoopGateOutcome.Denied,
                    new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, "shell_execute"),
                    AutonomyDial.AlwaysHuman, AutonomyDial.Min, "always-human")
                : new ToolLoopGateDecision(ToolLoopGateOutcome.Allowed, null, null, AutonomyDial.Min, "at-or-above-min-autonomy"));

        var runner = NewRunner(handler, gate.Object, registry,
            new ParallelToolExecutor(NullLogger<ParallelToolExecutor>.Instance));
        var result = await RunAsync(runner, new ToolLoopConfig { EnableParallelTools = true });

        executed.Should().BeEquivalentTo(new[] { "file_read", "file_write" },
            "the denied call must never reach the parallel executor; the allowed calls all run");
        executed.Should().NotContain("shell_execute");

        // All three calls still answer back to the model — the denial as an
        // ordinary tool result alongside the two parallel results.
        handler.CapturedBodies.Should().HaveCount(2);
        handler.CapturedBodies[1].Should().Contain("denied by autonomy policy");
        handler.CapturedBodies[1].Should().Contain("tc-1");
        handler.CapturedBodies[1].Should().Contain("tc-2");
        handler.CapturedBodies[1].Should().Contain("tc-3");
        result.Response.Success.Should().BeTrue();
    }

    // ── Denial message shape (43-4 review, 2026-07-29) ────────────────────

    [Test]
    public void Denial_message_names_the_threshold_when_MinAutonomy_is_present()
    {
        var decision = new ToolLoopGateDecision(
            ToolLoopGateOutcome.Denied,
            new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, "shell_execute"),
            MinAutonomy: 90, Dial: 70, "below-min-autonomy");

        InlineToolLoopRunner.ComposeDenialMessage("shell_execute", decision).Should().Be(
            "Tool call denied by autonomy policy: 'shell_execute' (action 'tool:shell_execute') "
            + "requires minimum autonomy 90, above the current autonomy level 70. "
            + "This action cannot run automatically; continue without it.");
    }

    [Test]
    public void Denial_message_with_a_null_threshold_is_a_well_formed_sentence()
    {
        // The bug this pins: MinAutonomy=null with a non-"always-human" reason
        // used to render "requires minimum autonomy , above the current
        // autonomy level 70" — the null case must omit the threshold clause.
        var decision = new ToolLoopGateDecision(
            ToolLoopGateOutcome.Denied, ActionKey: null,
            MinAutonomy: null, Dial: 70, "policy-denied");

        var message = InlineToolLoopRunner.ComposeDenialMessage("shell_execute", decision);

        message.Should().Be(
            "Tool call denied by autonomy policy: 'shell_execute' "
            + "is not permitted at the current autonomy level 70. "
            + "This action cannot run automatically; continue without it.");
        message.Should().NotContain("minimum autonomy ,");
    }

    [Test]
    public void Denial_message_for_always_human_says_a_person_is_required()
    {
        // always-human carries MinAutonomy=AlwaysHuman, but the message speaks
        // human, not numbers — and stays well-formed either way.
        var decision = new ToolLoopGateDecision(
            ToolLoopGateOutcome.Denied,
            new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, "shell_execute"),
            AutonomyDial.AlwaysHuman, AutonomyDial.Min, "always-human");

        InlineToolLoopRunner.ComposeDenialMessage("shell_execute", decision).Should().Be(
            "Tool call denied by autonomy policy: 'shell_execute' (action 'tool:shell_execute') "
            + "is configured to always require a person. "
            + "This action cannot run automatically; continue without it.");
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

    // ── F11 (2026-07-30) — a BREAK-GLASS BYPASS at Seam B is audited, and the
    //    audit row cannot be silently dropped ─────────────────────────────────

    /// <summary>
    /// The gate is sync by design (43-5 AC12: the per-tool-call path must never
    /// block on a database), so the DURABLE record of a bypass is written here,
    /// on the loop's async path. One row per bypassed call, carrying the
    /// override's reason and expiry.
    /// </summary>
    [Test]
    public async Task A_break_glass_bypass_writes_an_audit_row_per_call()
    {
        var events = new RecordingEventRepository();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_read", """{"path":"README.md"}"""),
                    ("tc-2", "file_write", """{"path":"a","content":"b"}""")),
            EndTurn("done", 5, 2));

        var runner = NewRunner(
            handler, BypassingGate(), RegistryRecording(new List<string>()),
            actionGateEvents: new Tamma.Api.Services.Actions.ActionGateEventsService(events));

        await RunAsync(runner);

        events.Appended.Should().HaveCount(2,
            "one durable row per bypassed decision, not one per outage");
        events.Appended.Should().OnlyContain(e =>
            e.Type == Tamma.Api.Services.Actions.ActionGateEventsService.BreakGlassBypassType);

        using var tags = JsonDocument.Parse(events.Appended[0].Tags!);
        tags.RootElement.GetProperty("seam").GetString().Should().Be("tool-loop",
            "a bypass on the tool loop and one at a 43-9 seam have very different blast radii");
        tags.RootElement.GetProperty("breakGlass").GetString().Should().Be("true");
        tags.RootElement.GetProperty("expiresAtUtc").GetString().Should().NotBeNullOrEmpty();

        using var data = JsonDocument.Parse(events.Appended[0].Data!);
        data.RootElement.GetProperty("reason").GetString().Should().Be("INC-4412");
    }

    /// <summary>
    /// The append is on the NON-swallowing path: a bypass that cannot be recorded
    /// fails the run instead of happening quietly. An unrecorded bypass is
    /// indistinguishable from an unauthorised one, and "loud and audited" is the
    /// whole condition on which this lever exists.
    /// </summary>
    [Test]
    public async Task A_break_glass_bypass_that_cannot_be_audited_does_not_happen_quietly()
    {
        var events = new RecordingEventRepository { Throw = true };
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_read", """{"path":"README.md"}""")),
            EndTurn("done", 5, 2));

        var runner = NewRunner(
            handler, BypassingGate(), RegistryRecording(new List<string>()),
            actionGateEvents: new Tamma.Api.Services.Actions.ActionGateEventsService(events));

        var act = async () => await RunAsync(runner);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>An ordinary (non-bypassed) decision writes nothing here — the
    /// bypass row is a signal, not new noise on the healthy path.</summary>
    [Test]
    public async Task An_ordinary_decision_writes_no_break_glass_row()
    {
        var events = new RecordingEventRepository();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "file_read", """{"path":"README.md"}""")),
            EndTurn("done", 5, 2));

        var runner = NewRunner(
            handler, new CatalogDefaultToolLoopAutonomyGate(), RegistryRecording(new List<string>()),
            actionGateEvents: new Tamma.Api.Services.Actions.ActionGateEventsService(events));

        await RunAsync(runner);

        events.Appended.Should().BeEmpty();
    }

    // ── Review 5a (2026-08-03) — a denied call that a grant COVERS proceeds, and
    //    that override is recorded on the durable audit path ──────────────────

    [Test]
    public async Task A_denied_call_a_grant_covers_executes_AND_writes_an_authorized_row()
    {
        // Review 5a: a denied tool call that PROCEEDS on a correlation-standing
        // grant is a security-relevant override and must leave a durable
        // ACTION.GATE.AUTHORIZED row tying the executed call to the authorizing
        // grant — not just a transient INFO log. RED before the fix: the covered
        // branch logged and continued with no _actionGateEvents emission.
        var events = new RecordingEventRepository();
        var executed = new List<string>();
        var handler = new SequencedCapturingHandler(
            ToolUse(("tc-1", "shell_execute", """{"command":"deploy.sh"}""")),
            EndTurn("done", 5, 2));

        var grant = new Tamma.Data.Entities.ActionAuthorization
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CorrelationId = "corr-gate",
            TargetKind = "action",
            TargetKey = "tool:shell_execute",
            State = "granted",
            Scope = "correlation-standing",
            RequestedAtUtc = DateTime.UtcNow,
        };
        var broker = new ToolLoopAuthorizationBroker(
            new StandingCoverLedger(grant),
            new FixedPrincipalResolver(grant.TenantId, null));

        var runner = NewRunner(
            handler, DenyShellGate(), RegistryRecording(executed),
            actionGateEvents: new Tamma.Api.Services.Actions.ActionGateEventsService(events),
            authorizationBroker: broker);

        await RunAsync(runner);

        executed.Should().Equal(new[] { "shell_execute" },
            "the denied call is COVERED by the standing grant, so it proceeds to execution");

        var authorized = events.Appended.Should().ContainSingle(e =>
            e.Type == Tamma.Api.Services.Actions.ActionGateEventsService.AuthorizedType,
            "a covered denial writes a durable AUTHORIZED row").Subject;
        using var tags = JsonDocument.Parse(authorized.Tags!);
        tags.RootElement.GetProperty("actionKey").GetString().Should().Be("tool:shell_execute");
        tags.RootElement.GetProperty("correlationId").GetString().Should().Be("corr-gate");
        tags.RootElement.GetProperty("authorizationId").GetString().Should().Be(grant.Id.ToString());
    }

    /// <summary>A gate that denies shell_execute (with an ActionKey so the covered
    /// branch has a wire to consult) and allows everything else.</summary>
    private static IToolLoopAutonomyGate DenyShellGate()
    {
        var gate = new Mock<IToolLoopAutonomyGate>();
        gate.Setup(g => g.Evaluate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string? _) => name == "shell_execute"
                ? new ToolLoopGateDecision(
                    ToolLoopGateOutcome.Denied,
                    new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, "shell_execute"),
                    AutonomyDial.AlwaysHuman, AutonomyDial.Min, "always-human")
                : new ToolLoopGateDecision(
                    ToolLoopGateOutcome.Allowed, null, null, AutonomyDial.Min, "at-or-above-min-autonomy"));
        return gate.Object;
    }

    /// <summary>A ledger whose <c>TryConsumeAsync</c> returns the one standing
    /// grant for its (correlation, action); everything else throws (unused here).</summary>
    private sealed class StandingCoverLedger(Tamma.Data.Entities.ActionAuthorization grant)
        : Tamma.Data.Repositories.IActionAuthorizationLedger
    {
        public Task<Tamma.Data.Entities.ActionAuthorization?> TryConsumeAsync(
            Guid? tenantId, Guid? userId, string correlationId, string actionKeyWire,
            CancellationToken ct = default)
            => Task.FromResult(
                string.Equals(correlationId, grant.CorrelationId, StringComparison.Ordinal)
                && string.Equals(actionKeyWire, grant.TargetKey, StringComparison.Ordinal)
                    ? grant : null);

        public Task<Tamma.Data.Entities.ActionAuthorization> RequestAsync(
            Guid? tenantId, Guid? userId, string correlationId, string targetKind, string targetKey,
            string? reason, int? autonomyLevelAtRequest, TimeSpan? ttl = null,
            string scope = "single-use", CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Tamma.Data.Entities.ActionAuthorization> MintStandingGrantAsync(
            Guid? tenantId, Guid? userId, string correlationId, string targetKind, string targetKey,
            Guid decidedByUserId, string? reason, TimeSpan? ttl = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Tamma.Data.Entities.ActionAuthorization?> DecideAsync(
            Guid? tenantId, Guid? userId, Guid id, bool granted, Guid decidedByUserId,
            string? reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Tamma.Data.Entities.ActionAuthorization>> ListDecidedSinceAsync(
            Guid? tenantId, Guid? userId, DateTime sinceUtc, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Resolves a fixed governance principal (no HTTP context needed).</summary>
    private sealed class FixedPrincipalResolver(Guid? tenantId, Guid? userId)
        : Tamma.Api.Services.Actions.IGovernancePrincipalResolver
    {
        public Task<Tamma.Core.Actions.GovernancePrincipal> ResolveAsync(
            System.Security.Claims.ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(new Tamma.Core.Actions.GovernancePrincipal(tenantId, userId));
    }

    /// <summary>A gate whose every decision is a break-glass bypass.</summary>
    private static IToolLoopAutonomyGate BypassingGate()
    {
        var state = Tamma.Core.Actions.BreakGlassState.Engaged(
            DateTimeOffset.UtcNow.AddHours(1), "INC-4412");
        var gate = new Mock<IToolLoopAutonomyGate>();
        gate.Setup(g => g.Evaluate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string? _) => new ToolLoopGateDecision(
                ToolLoopGateOutcome.Allowed,
                new Tamma.Core.Actions.ActionKey(Tamma.Core.Actions.ActionNamespace.Tool, name),
                AutonomyDial.Min, AutonomyDial.Min,
                Tamma.Core.Actions.AutonomyGateEvaluator.ReasonBreakGlassBypass,
                state));
        return gate.Object;
    }

    private sealed class RecordingEventRepository : Tamma.Data.Repositories.IEventRepository
    {
        public List<Tamma.Data.Entities.DomainEvent> Appended { get; } = new();
        public bool Throw { get; init; }

        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
        {
            if (Throw) throw new InvalidOperationException("event store unavailable");
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<Tamma.Data.Entities.DomainEvent>());
        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)>
            QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>(
                (Array.Empty<Tamma.Data.Entities.DomainEvent>(), 0));
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)>
            ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>(
                (Array.Empty<Tamma.Data.Entities.DomainEvent>(), 0));
    }

    private static async Task<InlineToolLoopResult> RunAsync(
        InlineToolLoopRunner runner, ToolLoopConfig? loopConfig = null) =>
        await runner.RunAsync(
            "anthropic", Config(), Model, "sys", "user", 4096, 0.7,
            tools: null, enableToolLoop: true, loopConfig ?? new ToolLoopConfig(), "corr-gate",
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
        HttpMessageHandler handler, IToolLoopAutonomyGate gate, IToolExecutorRegistry registry,
        ParallelToolExecutor? parallelExecutor = null,
        Tamma.Api.Services.Actions.ActionGateEventsService? actionGateEvents = null,
        ToolLoopAuthorizationBroker? authorizationBroker = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        return new InlineToolLoopRunner(
            NullLogger<InlineToolLoopRunner>.Instance, factory.Object, configuration: null,
            sanitizer: null, autonomyGate: gate, toolRegistry: registry,
            parallelExecutor: parallelExecutor,
            actionGateEvents: actionGateEvents,
            authorizationBroker: authorizationBroker);
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
