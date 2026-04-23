namespace Tamma.Data.Abstractions;

/// <summary>
/// Wave C.4 — narrow port used by emission sites (activities, HTTP
/// clients, rotation sagas) to push the five Wave-C alert-trigger events
/// into the DCB event store.
///
/// <para>Implementations route tenant-scoped events to the tenant's
/// <c>domain_events</c> via <c>IEventRepository</c> and fleet-wide events
/// (<c>PLATFORM.API.UNHEALTHY</c>, platform-scoped rotations) to
/// <c>platform_events</c> via <c>IPlatformEventPublisher</c>. The
/// <c>AlertRuleEvaluator</c> polls both tables so either destination
/// drives the downstream pipeline identically.</para>
///
/// <para>Every method must swallow persistence failures — the activities
/// that invoke it are already in their own error-handling path, and a
/// failed emission must NOT turn a graceful degradation into a crash.
/// Implementations log the failure and return.</para>
/// </summary>
public interface IAlertEventEmitter
{
    Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct);
    Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct);
    Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct);
    Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct);
    Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct);
}

// ─── Event payloads ───────────────────────────────────────────────────
//
// These are the minimal data shapes the emitter writes. Tags pulled
// from the payload end up on <c>tags</c> JSONB (indexed for rule
// lookups); the full payload goes to <c>data</c> JSONB. Tenant and
// correlation ids are mandatory for tenant-scoped events; the emitter
// enforces this.

/// <summary>
/// Data required to emit a <c>BUDGET.EXHAUSTED</c> event. Wave C.4 §1.
/// </summary>
public sealed record BudgetExhaustedEvent(
    Guid TenantId,
    string CorrelationId,
    string Source,        // "api" | "local"
    decimal Spent,
    decimal Limit,
    string ProviderName,
    string WorkflowInstanceId);

/// <summary>
/// Data required to emit a <c>AGENT.DISPATCH.FAILED</c> event.
/// Wave C.4 §2. <paramref name="LastError"/> is credential-redacted by
/// the emitter before persistence.
/// </summary>
public sealed record AgentDispatchFailedEvent(
    Guid TenantId,
    string CorrelationId,
    string AgentHandle,
    string Reason,
    int AttemptNumber,
    string? LastError);

/// <summary>
/// Data required to emit a <c>WORKFLOW.RETRY_EXCEEDED</c> event.
/// Wave C.4 §3. <paramref name="FinalError"/> is credential-redacted.
/// </summary>
public sealed record WorkflowRetryExceededEvent(
    Guid TenantId,
    string CorrelationId,
    Guid WorkflowDefinitionId,
    Guid WorkflowInstanceId,
    int Attempts,
    int MaxAttempts,
    string? FinalError,
    string? ActivityId);

/// <summary>
/// Data required to emit a <c>PLATFORM.API.UNHEALTHY</c> event.
/// Wave C.4 §4. Fleet-wide — no <c>TenantId</c> / <c>CorrelationId</c>
/// fields; the rule evaluator treats this as a platform-scoped signal.
/// </summary>
public sealed record PlatformApiUnhealthyEvent(
    int WindowSeconds,
    int TotalRequests,
    int FailureCount,
    decimal FailureRate,
    IReadOnlyList<FailureReasonCount> TopFailureReasons);

public sealed record FailureReasonCount(string Reason, int Count);

/// <summary>
/// Data required to emit a <c>SECRET.ROTATION.FAILED</c> event.
/// Wave C.4 §5. <paramref name="LastError"/> is credential-redacted.
/// <paramref name="TenantId"/> null means the rotation targeted a
/// platform-scoped secret — emitter routes to <c>platform_events</c>.
/// </summary>
public sealed record SecretRotationFailedEvent(
    Guid? TenantId,
    string CorrelationId,
    string TargetKind,         // "postgres-role" | "cranl-env" | "generic-http"
    string CabinetName,
    string HandlerType,
    string FailureStage,       // "mint" | "push" | "probe" | "activate" | "retire"
    bool CompensationApplied,
    string? LastError);
