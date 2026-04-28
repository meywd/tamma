using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Wave C.4 — unit tests for <see cref="AlertEventEmitter"/>. Verifies
/// the emitter shapes <see cref="DomainEvent"/> / <see cref="PlatformEvent"/>
/// rows correctly for each of the five alert source event types, including
/// the credential-redaction guarantee.
/// </summary>
[TestFixture]
public class AlertEventEmitterTests
{
    private RecordingEventRepository _events = null!;
    private RecordingPlatformEventPublisher _platform = null!;
    private AlertEventEmitter _emitter = null!;

    [SetUp]
    public void SetUp()
    {
        _events = new RecordingEventRepository();
        _platform = new RecordingPlatformEventPublisher();
        _emitter = new AlertEventEmitter(
            _events, _platform, NullLogger<AlertEventEmitter>.Instance);
    }

    // ---- BUDGET.EXHAUSTED ---------------------------------------------

    [Test]
    public async Task EmitBudgetExhausted_Api_WritesDomainEventWithRequiredFields()
    {
        var tenantId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");

        await _emitter.EmitBudgetExhaustedAsync(new BudgetExhaustedEvent(
            TenantId: tenantId,
            CorrelationId: correlationId,
            Source: "api",
            Spent: 10.50m,
            Limit: 10.00m,
            ProviderName: "anthropic-claude",
            WorkflowInstanceId: "wf-123"), CancellationToken.None);

        _events.Appended.Should().ContainSingle();
        var evt = _events.Appended[0];
        evt.Type.Should().Be("BUDGET.EXHAUSTED");
        evt.TenantId.Should().Be(tenantId);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["tenantId"].Should().Be(tenantId.ToString());
        tags["correlationId"].Should().Be(correlationId);
        tags["providerName"].Should().Be("anthropic-claude");
        tags["source"].Should().Be("api");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["source"].GetString().Should().Be("api");
        data["spent"].GetDecimal().Should().Be(10.50m);
        data["limit"].GetDecimal().Should().Be(10.00m);
        data["providerName"].GetString().Should().Be("anthropic-claude");
        data["workflowInstanceId"].GetString().Should().Be("wf-123");
    }

    [Test]
    public async Task EmitBudgetExhausted_Local_TagsSourceLocal()
    {
        await _emitter.EmitBudgetExhaustedAsync(new BudgetExhaustedEvent(
            TenantId: Guid.NewGuid(),
            CorrelationId: "corr-1",
            Source: "local",
            Spent: 5.0m,
            Limit: 5.0m,
            ProviderName: "local-llm",
            WorkflowInstanceId: "wf-2"), CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["source"].Should().Be("local");
    }

    // ---- AGENT.DISPATCH.FAILED -----------------------------------------

    [Test]
    public async Task EmitAgentDispatchFailed_WritesRequiredFields()
    {
        var tenantId = Guid.NewGuid();
        await _emitter.EmitAgentDispatchFailedAsync(new AgentDispatchFailedEvent(
            TenantId: tenantId,
            CorrelationId: "dispatch-corr",
            AgentHandle: "claude-code",
            Reason: "github_403",
            AttemptNumber: 3,
            LastError: "HTTP 403 — permissions missing"), CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("AGENT.DISPATCH.FAILED");
        evt.TenantId.Should().Be(tenantId);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["tenantId"].Should().Be(tenantId.ToString());
        tags["correlationId"].Should().Be("dispatch-corr");
        tags["agentHandle"].Should().Be("claude-code");
        tags["reason"].Should().Be("github_403");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["agentHandle"].GetString().Should().Be("claude-code");
        data["reason"].GetString().Should().Be("github_403");
        data["attemptNumber"].GetInt32().Should().Be(3);
        data["lastError"].GetString().Should().Contain("permissions missing");
    }

    [Test]
    public async Task EmitAgentDispatchFailed_LastErrorWithBearer_Redacted()
    {
        await _emitter.EmitAgentDispatchFailedAsync(new AgentDispatchFailedEvent(
            TenantId: Guid.NewGuid(),
            CorrelationId: "c",
            AgentHandle: "h",
            Reason: "401",
            AttemptNumber: 1,
            LastError:
                "401 Unauthorized: Authorization: Bearer FAKE_abcdef1234567890abcdef1234567890 rejected"),
            CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        var lastError = data["lastError"].GetString()!;
        lastError.Should().NotContain("FAKE_abcdef1234567890abcdef1234567890");
        lastError.Should().Contain("[REDACTED]");
    }

    // ---- WORKFLOW.RETRY_EXCEEDED ---------------------------------------

    [Test]
    public async Task EmitWorkflowRetryExceeded_WritesRequiredFields()
    {
        var tenantId = Guid.NewGuid();
        var defId = Guid.NewGuid();
        var instId = Guid.NewGuid();

        await _emitter.EmitWorkflowRetryExceededAsync(new WorkflowRetryExceededEvent(
            TenantId: tenantId,
            CorrelationId: instId.ToString("N"),
            WorkflowDefinitionId: defId,
            WorkflowInstanceId: instId,
            Attempts: 5,
            MaxAttempts: 3,
            FinalError: "HttpRequestException: Connection refused",
            ActivityId: "act-1"), CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("WORKFLOW.RETRY_EXCEEDED");
        evt.TenantId.Should().Be(tenantId);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["tenantId"].Should().Be(tenantId.ToString());
        tags["workflowDefinitionId"].Should().Be(defId.ToString());
        tags["correlationId"].Should().Be(instId.ToString("N"));

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["workflowDefinitionId"].GetString().Should().Be(defId.ToString());
        data["workflowInstanceId"].GetString().Should().Be(instId.ToString());
        data["attempts"].GetInt32().Should().Be(5);
        data["maxAttempts"].GetInt32().Should().Be(3);
        data["finalError"].GetString().Should().Contain("Connection refused");
        data["activityId"].GetString().Should().Be("act-1");
    }

    [Test]
    public async Task EmitWorkflowRetryExceeded_FinalErrorWithPassword_Redacted()
    {
        await _emitter.EmitWorkflowRetryExceededAsync(new WorkflowRetryExceededEvent(
            TenantId: Guid.NewGuid(),
            CorrelationId: "c",
            WorkflowDefinitionId: Guid.NewGuid(),
            WorkflowInstanceId: Guid.NewGuid(),
            Attempts: 3, MaxAttempts: 3,
            FinalError: "Npgsql: auth failed with Password=hunter2supersecret pooled",
            ActivityId: null), CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["finalError"].GetString().Should().NotContain("hunter2supersecret");
    }

    // ---- PLATFORM.API.UNHEALTHY ----------------------------------------

    [Test]
    public async Task EmitPlatformApiUnhealthy_RoutesToPlatformEventsNotDomainEvents()
    {
        await _emitter.EmitPlatformApiUnhealthyAsync(new PlatformApiUnhealthyEvent(
            WindowSeconds: 300,
            TotalRequests: 40,
            FailureCount: 22,
            FailureRate: 0.55m,
            TopFailureReasons: new[]
            {
                new FailureReasonCount("503", 12),
                new FailureReasonCount("HttpRequestException", 10),
            }), CancellationToken.None);

        _events.Appended.Should().BeEmpty(
            "PLATFORM.API.UNHEALTHY is fleet-wide — goes to platform_events, not domain_events");
        _platform.Appended.Should().ContainSingle();

        var evt = _platform.Appended[0];
        evt.Type.Should().Be("PLATFORM.API.UNHEALTHY");
        evt.TenantId.Should().BeNull("platform-wide event has no tenant");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["platform"].Should().Be("tamma-api");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["windowSeconds"].GetInt32().Should().Be(300);
        data["totalRequests"].GetInt32().Should().Be(40);
        data["failureCount"].GetInt32().Should().Be(22);
        data["failureRate"].GetDecimal().Should().Be(0.55m);
        data["topFailureReasons"].GetArrayLength().Should().Be(2);
    }

    // ---- SECRET.ROTATION.FAILED ----------------------------------------

    [Test]
    public async Task EmitSecretRotationFailed_WithTenant_WritesDomainEvent()
    {
        var tenantId = Guid.NewGuid();
        await _emitter.EmitSecretRotationFailedAsync(new SecretRotationFailedEvent(
            TenantId: tenantId,
            CorrelationId: "rot-1",
            TargetKind: "postgres-role",
            CabinetName: "db/app-role",
            HandlerType: "postgres",
            FailureStage: "push",
            CompensationApplied: true,
            LastError: "connection refused"), CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be("SECRET.ROTATION.FAILED");
        evt.TenantId.Should().Be(tenantId);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["tenantId"].Should().Be(tenantId.ToString());
        tags["cabinetName"].Should().Be("db/app-role");
        tags["handlerType"].Should().Be("postgres");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["targetKind"].GetString().Should().Be("postgres-role");
        data["cabinetName"].GetString().Should().Be("db/app-role");
        data["handlerType"].GetString().Should().Be("postgres");
        data["failureStage"].GetString().Should().Be("push");
        data["compensationApplied"].GetBoolean().Should().BeTrue();
        data["lastError"].GetString().Should().Contain("connection refused");
    }

    [Test]
    public async Task EmitSecretRotationFailed_WithoutTenant_RoutesToPlatformEvents()
    {
        // Platform-scoped secret (tenant null) rotates via platform_events
        // so the evaluator still sees it.
        await _emitter.EmitSecretRotationFailedAsync(new SecretRotationFailedEvent(
            TenantId: null,
            CorrelationId: "rot-2",
            TargetKind: "generic-http",
            CabinetName: "platform/slack-webhook",
            HandlerType: "generic-http",
            FailureStage: "probe",
            CompensationApplied: false,
            LastError: "timeout"), CancellationToken.None);

        _events.Appended.Should().BeEmpty();
        _platform.Appended.Should().ContainSingle();
        _platform.Appended[0].Type.Should().Be("SECRET.ROTATION.FAILED");
        _platform.Appended[0].TenantId.Should().BeNull();
    }

    [Test]
    public async Task EmitSecretRotationFailed_LastErrorWithConnectionStringPassword_Redacted()
    {
        await _emitter.EmitSecretRotationFailedAsync(new SecretRotationFailedEvent(
            TenantId: Guid.NewGuid(),
            CorrelationId: "r",
            TargetKind: "postgres-role",
            CabinetName: "db/app",
            HandlerType: "postgres",
            FailureStage: "mint",
            CompensationApplied: false,
            LastError: "Host=db;Password=deadbeefsupersecret;Database=tamma"),
            CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data)!;
        data["lastError"].GetString().Should().NotContain("deadbeefsupersecret");
        data["lastError"].GetString().Should().Contain("[REDACTED]");
    }
}

// ─── Test doubles ──────────────────────────────────────────────────────

internal sealed class RecordingEventRepository : IEventRepository
{
    public List<DomainEvent> Appended { get; } = new();

    public Task<DomainEvent> AppendAsync(DomainEvent evt)
    {
        if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
        if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;
        Appended.Add(evt);
        return Task.FromResult(evt);
    }

    public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
    public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
        => Task.FromResult(new List<DomainEvent>());
    public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
        => Task.FromResult<DomainEvent?>(null);
    public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
    public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
        Guid tenantId, string? typePrefix, int limit, int offset)
        => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
}

