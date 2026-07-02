using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-6 (T1) — <see cref="AgentTrailEmitter"/> unit tests. The event store
/// is a recording double so we can assert, per event family: the right
/// <c>Type</c>, the full required tag set (AC3), redaction of <c>Data</c> (AC6),
/// the resolving <c>TenantId</c> on every event (AC1), and the non-blocking
/// contract (AC7) — an append failure never propagates and a
/// <c>AGENT.TRAIL.WRITE_FAILED</c> breadcrumb is attempted.
/// </summary>
[TestFixture]
public class AgentTrailEmitterTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Agent = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private RecordingEventRepository _events = null!;
    private AgentTrailEmitter _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _events = new RecordingEventRepository();
        _sut = new AgentTrailEmitter(_events, NullLogger<AgentTrailEmitter>.Instance, new ContentSanitizer());
    }

    private static AgentTrailContext Ctx(int iteration = 0) => new()
    {
        TenantId = Tenant,
        AgentId = Agent,
        AgentVersion = 7,
        Role = "developer",
        Provider = "anthropic",
        Model = "claude-sonnet-4",
        PromptRef = "developer:implement@3",
        IssueId = "ISSUE-42",
        IssueNumber = 42,
        Iteration = iteration,
        CorrelationId = "corr-abc",
        CredentialSource = "byok",
    };

    // ── Event types + full tag set (AC2 / AC3) ──────────────────────────

    [Test]
    public async Task RunCompleted_Success_EmitsTaskSuccess_WithFullTagSet()
    {
        await _sut.RunCompletedAsync(Ctx(), new AgentRunOutcome
        {
            Status = AgentRunStatus.Success,
            DurationMs = 1234,
            Iterations = 2,
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.0030m,
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.TASK.SUCCESS");
        evt.TenantId.Should().Be(Tenant);
        evt.IssueNumber.Should().Be(42);

        var tags = Tags(evt);
        tags.Should().Contain("agentId", Agent.ToString());
        tags.Should().Contain("agentVersion", "7");
        tags.Should().Contain("role", "developer");
        tags.Should().Contain("provider", "anthropic");
        tags.Should().Contain("model", "claude-sonnet-4");
        tags.Should().Contain("promptRef", "developer:implement@3");
        tags.Should().Contain("issueId", "ISSUE-42");
        tags.Should().Contain("iteration", "0");
        tags.Should().Contain("correlationId", "corr-abc");
        tags.Should().Contain("credentialSource", "byok");

        // Metrics live in Data, not Tags.
        var data = Data(evt);
        data.GetProperty("durationMs").GetInt64().Should().Be(1234);
        data.GetProperty("inputTokens").GetInt32().Should().Be(100);
        data.GetProperty("outputTokens").GetInt32().Should().Be(50);
    }

    [Test]
    public async Task RunCompleted_Failed_EmitsTaskFailed()
    {
        await _sut.RunCompletedAsync(Ctx(), new AgentRunOutcome
        {
            Status = AgentRunStatus.Failed,
            FailureCode = "PROVIDER_ERROR",
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.TASK.FAILED");
        Data(evt).GetProperty("failureCode").GetString().Should().Be("PROVIDER_ERROR");
    }

    [Test]
    public async Task RunCompleted_Partial_EmitsTaskPartial()
    {
        await _sut.RunCompletedAsync(Ctx(), new AgentRunOutcome { Status = AgentRunStatus.Partial });
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be("AGENT.TASK.PARTIAL");
    }

    [Test]
    public async Task ToolCall_Success_EmitsToolCallSuccess()
    {
        await _sut.ToolCallAsync(Ctx(), new ToolCallRecord
        {
            ToolName = "file_read",
            ArgsRef = "blob:args-1",
            ResultRef = "blob:res-1",
            DurationMs = 12,
            Success = true,
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.TOOL_CALL.SUCCESS");
        Data(evt).GetProperty("toolName").GetString().Should().Be("file_read");
        Tags(evt).Should().ContainKey("agentId");
    }

    [Test]
    public async Task ToolCall_Failure_EmitsToolCallFailed()
    {
        await _sut.ToolCallAsync(Ctx(), new ToolCallRecord
        {
            ToolName = "shell",
            Success = false,
            ErrorCode = "nonzero_exit",
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.TOOL_CALL.FAILED");
        Data(evt).GetProperty("errorCode").GetString().Should().Be("nonzero_exit");
    }

    [Test]
    public async Task IterationCompleted_EmitsIterationEvent()
    {
        await _sut.IterationCompletedAsync(Ctx(iteration: 3), new IterationRecord
        {
            Iteration = 3,
            GatePassed = true,
            FindingsCount = 5,
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.ITERATION.COMPLETED");
        Tags(evt).Should().Contain("iteration", "3");
        Data(evt).GetProperty("findingsCount").GetInt32().Should().Be(5);
    }

    [Test]
    public async Task PanelAggregated_EmitsPanelEvent_WithParticipants()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _sut.PanelAggregatedAsync(Ctx(), new PanelRecord
        {
            Strategy = "consensus",
            ParticipantAgentIds = new[] { a, b },
            ChosenAgentId = a,
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.PANEL.AGGREGATED");
        var data = Data(evt);
        data.GetProperty("strategy").GetString().Should().Be("consensus");
        data.GetProperty("participantAgentIds").GetArrayLength().Should().Be(2);
        data.GetProperty("chosenAgentId").GetString().Should().Be(a.ToString());
    }

    [Test]
    public async Task BugRecorded_EmitsBugEvent_WithBugTypeTag()
    {
        await _sut.BugRecordedAsync(Ctx(), new BugRecord
        {
            BugType = "security",
            Severity = "high",
            DescriptionRef = "blob:bug-1",
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("REVIEW.BUG.RECORDED");
        // AC3 — REVIEW.BUG.RECORDED additionally carries bugType in Tags.
        Tags(evt).Should().Contain("bugType", "security");
        Data(evt).GetProperty("severity").GetString().Should().Be("high");
    }

    // ── Redaction (AC6) ─────────────────────────────────────────────────

    [Test]
    public async Task Emit_RedactsHtmlAndInjectionShapedContent_FromData()
    {
        await _sut.ToolCallAsync(Ctx(), new ToolCallRecord
        {
            ToolName = "file_write",
            // Secret/injection-shaped content that must never land raw in the
            // immutable event stream. By contract this is a REF, but the emitter
            // sanitizes anyway (defence in depth).
            ArgsRef = "<script>steal('sk-secret-key')</script>path",
            Success = true,
        });

        var evt = _events.Appended.Should().ContainSingle().Subject;
        var raw = evt.Data;
        raw.Should().NotContain("<script>", "HTML must be stripped before persistence");
        var argsRef = Data(evt).GetProperty("argsRef").GetString();
        argsRef.Should().NotContain("<script>");
    }

    // ── Non-blocking contract (AC7) ─────────────────────────────────────

    [Test]
    public async Task Emit_AppendThrows_DoesNotPropagate_AndAttemptsBreadcrumb()
    {
        var failing = new FailingEventRepository();
        var sut = new AgentTrailEmitter(failing, NullLogger<AgentTrailEmitter>.Instance, new ContentSanitizer());

        // Must NOT throw even though every append throws.
        Func<Task> act = () => sut.RunCompletedAsync(Ctx(), new AgentRunOutcome { Status = AgentRunStatus.Success });
        await act.Should().NotThrowAsync();

        // The emitter attempted the terminal event AND the breadcrumb (both threw,
        // both swallowed). Two append attempts is the observable signal.
        failing.Attempts.Should().Be(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Dictionary<string, string?> Tags(DomainEvent e)
        => JsonSerializer.Deserialize<Dictionary<string, string?>>(e.Tags)!;

    private static JsonElement Data(DomainEvent e)
        => JsonDocument.Parse(e.Data).RootElement.Clone();

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentQueue<DomainEvent> Queue { get; } = new();
        public IReadOnlyList<DomainEvent> Appended => Queue.ToList();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Queue.Enqueue(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }

    private sealed class FailingEventRepository : IEventRepository
    {
        public int Attempts { get; private set; }

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Attempts++;
            throw new InvalidOperationException("simulated append failure");
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
