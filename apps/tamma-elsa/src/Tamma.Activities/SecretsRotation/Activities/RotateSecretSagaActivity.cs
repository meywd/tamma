using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 — top-level saga activity that composes the seven rotation
/// steps imperatively. Expressed as a single async activity (rather
/// than an Elsa <c>Sequence</c> + per-step compensations) because:
///
/// <list type="bullet">
///   <item><description>Each step must short-circuit on the first
///     failure and walk a specific compensation path — Elsa's built-in
///     retry/compensation primitives are awkward to wire for saga-
///     shaped flows.</description></item>
///   <item><description>The state machine is small (7 steps) and
///     deterministic — a single method reads more linearly than 14
///     activity nodes + conditional edges.</description></item>
///   <item><description>Every mutating call goes through the
///     <see cref="ISecretRotationGateway"/> + handler ports, which are
///     already idempotent on <c>rotationCorrelationId</c>, so a replay
///     on a re-dispatched activity is safe.</description></item>
/// </list>
///
/// <para>The workflow (<c>RotateSecretWorkflow</c>) just wires the
/// activity's inputs from its input bag and fans out the result via
/// an output slot. Retention of this composite as an Elsa activity
/// (rather than a plain service method) keeps the rotation visible in
/// the Elsa Studio timeline.</para>
/// </summary>
public class RotateSecretSagaActivity : TammaAsyncActivity
{
    [Input(Description = "Secret id to rotate.")]
    public Input<Guid> SecretId { get; set; } = default!;

    [Input(Description = "Rotation correlation id — threaded through all events + handler calls.")]
    public Input<string> RotationCorrelationId { get; set; } = default!;

    [Input(
        Description =
            "Pre-supplied plaintext. When empty the saga generates GenerateLength bytes of CSPRNG entropy.")]
    public Input<string> NewPlaintext { get; set; } = new(string.Empty);

    [Input(Description = "Generator length (bytes) when NewPlaintext is empty. Default 32.")]
    public Input<int> GenerateLength { get; set; } = new(32);

    [Input(Description = "Operator user id (Guid.Empty for scheduled/auto rotations).")]
    public Input<Guid> OperatorUserId { get; set; } = new(Guid.Empty);

    [Input(Description = "Grace window seconds. 0 means use the default (900).")]
    public Input<long> GraceWindowSeconds { get; set; } = new(0L);

    [Output(Description = "Terminal result: activated | compensated | failed.")]
    public Output<string>? Result { get; set; }

    [Output(Description = "Minted new version number; 0 when the saga never reached mint.")]
    public Output<int>? NewVersionNumber { get; set; }

    [Output(Description = "Previous active version number (0 when first rotation).")]
    public Output<int>? OldVersionNumber { get; set; }

    [Output(Description = "Short machine-readable error reason on compensated/failed paths.")]
    public Output<string>? Error { get; set; }

    /// <summary>Tests shrink these to zero.</summary>
    public IReadOnlyList<TimeSpan> PushRetryDelays { get; set; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    };

    /// <summary>Tests shrink these to zero.</summary>
    public IReadOnlyList<TimeSpan> ProbeRetryDelays { get; set; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    };

    public override string? EventType => "SECRET.ROTATION";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var logger = context.GetService<ILogger<RotateSecretSagaActivity>>();
        var state = RotationActivityBase.GetStateStatic(context);
        state.SecretId = SecretId.Get(context);
        state.RotationCorrelationId = RotationCorrelationId.Get(context);
        if (string.IsNullOrWhiteSpace(state.RotationCorrelationId))
            throw new ArgumentException(
                "RotationCorrelationId is required.", nameof(RotationCorrelationId));
        state.OperatorUserId = OperatorUserId.Get(context);
        state.GraceWindowSeconds = GraceWindowSeconds.Get(context);

        var runner = new SagaRunner(
            context.GetRequiredService<ISecretRotationGateway>(),
            context.GetRequiredService<IRotationHandlerRegistry>(),
            context.GetRequiredService<IRotationAuditEmitter>(),
            context.GetRequiredService<IRetireScheduler>(),
            PushRetryDelays,
            ProbeRetryDelays,
            logger,
            // Wave C.4 §5 — optional alert emitter. When wired by
            // AddTammaAlerts the saga fires SECRET.ROTATION.FAILED with
            // the Wave-C alert-shaped payload (targetKind, cabinetName,
            // handlerType, failureStage, compensationApplied, lastError).
            // In unit-test harnesses the emitter is usually absent and
            // the runner degrades to RotationAuditEmitter-only.
            context.GetService<IAlertEventEmitter>());
        var outcome = await runner.ExecuteAsync(
            state,
            suppliedPlaintext: NewPlaintext.Get(context),
            generateLength: GenerateLength.Get(context),
            ct: context.CancellationToken).ConfigureAwait(false);

        Result?.Set(context, OutcomeLabel(outcome));
        NewVersionNumber?.Set(context, state.NewVersionNumber);
        OldVersionNumber?.Set(context, state.PreviousVersionNumber);
        Error?.Set(context, state.Error ?? string.Empty);
    }

    internal static string OutcomeLabel(SagaOutcome outcome) => outcome switch
    {
        SagaOutcome.Activated => "activated",
        SagaOutcome.Compensated => "compensated",
        _ => "failed",
    };
}

/// <summary>
/// Runner extracted so the saga body is unit-testable without hosting
/// Elsa. Tests construct a <c>SagaRunner</c> directly, pass stubs for
/// every port, and assert the outcome + emitted events.
/// </summary>
internal sealed class SagaRunner
{
    private readonly ISecretRotationGateway _gateway;
    private readonly IRotationHandlerRegistry _registry;
    private readonly IRotationAuditEmitter _auditor;
    private readonly IRetireScheduler _scheduler;
    private readonly IReadOnlyList<TimeSpan> _pushDelays;
    private readonly IReadOnlyList<TimeSpan> _probeDelays;
    private readonly ILogger<RotateSecretSagaActivity>? _logger;
    private readonly IAlertEventEmitter? _alertEmitter;

    // Per-run state — populated in ExecuteAsync so the runner is
    // stateless across different rotation sagas.
    private RotationWorkflowState _state = new();
    private CancellationToken _ct;

    public SagaRunner(
        ISecretRotationGateway gateway,
        IRotationHandlerRegistry registry,
        IRotationAuditEmitter auditor,
        IRetireScheduler scheduler,
        IReadOnlyList<TimeSpan> pushDelays,
        IReadOnlyList<TimeSpan> probeDelays,
        ILogger<RotateSecretSagaActivity>? logger,
        IAlertEventEmitter? alertEmitter = null)
    {
        _gateway = gateway;
        _registry = registry;
        _auditor = auditor;
        _scheduler = scheduler;
        _pushDelays = pushDelays;
        _probeDelays = probeDelays;
        _logger = logger;
        _alertEmitter = alertEmitter;
    }

    public async Task<SagaOutcome> ExecuteAsync(
        RotationWorkflowState state,
        string suppliedPlaintext,
        int generateLength,
        CancellationToken ct)
    {
        _state = state;
        _ct = ct;
        var gateway = _gateway;
        var registry = _registry;
        var auditor = _auditor;
        var scheduler = _scheduler;

        // Step 1 — mint
        var snapshot = await gateway.GetSnapshotAsync(_state.SecretId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            _state.Error = "secret_not_found";
            _state.Result = "failed";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error).ConfigureAwait(false);
            await EmitAlertFailedAsync("mint", compensationApplied: false).ConfigureAwait(false);
            return SagaOutcome.Failed;
        }

        _state.Snapshot = snapshot;
        _state.PreviousVersionNumber = snapshot.ActiveVersionNumber;
        _state.HandlerSystem = snapshot.ConsumerSystem;

        var handler = registry.Resolve(snapshot.ConsumerSystem) ?? registry.Resolve("generic-http");
        if (handler is null)
        {
            _state.Error = "handler_not_registered";
            _state.Result = "failed";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error).ConfigureAwait(false);
            await EmitAlertFailedAsync("mint", compensationApplied: false).ConfigureAwait(false);
            return SagaOutcome.Failed;
        }
        _state.HandlerSystem = handler.System;

        var plaintext = suppliedPlaintext;
        if (string.IsNullOrWhiteSpace(plaintext))
            plaintext = GenerateRandom(Math.Max(16, Math.Min(256, generateLength)));
        _state.NewPlaintext = plaintext;

        await EmitAsync(auditor, RotationAuditEvents.Started, data: new Dictionary<string, object?>
        {
            ["handlerSystem"] = handler.System,
            ["previousVersion"] = snapshot.ActiveVersionNumber,
        }).ConfigureAwait(false);

        int newVersion;
        try
        {
            newVersion = await gateway.MintPendingVersionAsync(
                    _state.SecretId,
                    plaintext,
                    _state.RotationCorrelationId,
                    _state.OperatorUserId,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _state.Error = $"mint_failed:{ex.GetType().Name}";
            _state.Result = "failed";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error).ConfigureAwait(false);
            await EmitAlertFailedAsync("mint", compensationApplied: false).ConfigureAwait(false);
            return SagaOutcome.Failed;
        }

        _state.NewVersionNumber = newVersion;

        await EmitAsync(auditor, RotationAuditEvents.Staged, versionNumber: newVersion,
            data: new Dictionary<string, object?> { ["previousVersion"] = _state.PreviousVersionNumber })
            .ConfigureAwait(false);

        // Step 3 — push (retry)
        var rotationContext = BuildRotationContext();
        var target = BuildTarget();

        var pushError = await TryPushAsync(handler, target, rotationContext, auditor).ConfigureAwait(false);
        if (pushError is not null)
        {
            _state.Error = $"push_failed:{pushError.GetType().Name}";
            await CompensateAsync(handler, target, rotationContext, gateway, auditor).ConfigureAwait(false);
            _state.Result = "compensated";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error,
                versionNumber: newVersion).ConfigureAwait(false);
            await EmitAlertFailedAsync("push", compensationApplied: true).ConfigureAwait(false);
            return SagaOutcome.Compensated;
        }

        // Step 4 — probe (retry)
        var probeFailure = await TryProbeAsync(handler, target, rotationContext, auditor).ConfigureAwait(false);
        if (probeFailure is not null)
        {
            _state.Error = $"probe_failed:{probeFailure}";
            await CompensateAsync(handler, target, rotationContext, gateway, auditor).ConfigureAwait(false);
            _state.Result = "compensated";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error,
                versionNumber: newVersion).ConfigureAwait(false);
            await EmitAlertFailedAsync("probe", compensationApplied: true).ConfigureAwait(false);
            return SagaOutcome.Compensated;
        }

        // Step 5 — activate
        try
        {
            await gateway.ActivateVersionAsync(
                    _state.SecretId,
                    _state.NewVersionNumber,
                    _state.PreviousVersionNumber,
                    ct)
                .ConfigureAwait(false);
            _state.Activated = true;
        }
        catch (Exception ex)
        {
            _state.Error = $"activate_failed:{ex.GetType().Name}";
            await CompensateAsync(handler, target, rotationContext, gateway, auditor).ConfigureAwait(false);
            _state.Result = "compensated";
            await EmitAsync(auditor, RotationAuditEvents.Failed, detail: _state.Error,
                versionNumber: newVersion).ConfigureAwait(false);
            await EmitAlertFailedAsync("activate", compensationApplied: true).ConfigureAwait(false);
            return SagaOutcome.Compensated;
        }

        await EmitAsync(auditor, RotationAuditEvents.Switched, versionNumber: newVersion,
            data: new Dictionary<string, object?> { ["previousVersion"] = _state.PreviousVersionNumber })
            .ConfigureAwait(false);
        await EmitAsync(auditor, RotationAuditEvents.Activated, versionNumber: newVersion,
            data: new Dictionary<string, object?> { ["previousVersion"] = _state.PreviousVersionNumber })
            .ConfigureAwait(false);

        // Step 6 — schedule retire
        if (_state.PreviousVersionNumber > 0)
        {
            var graceSeconds = _state.GraceWindowSeconds <= 0
                ? ScheduleRetireOldActivity.DefaultGraceWindowSeconds
                : _state.GraceWindowSeconds;
            var runAfter = DateTimeOffset.UtcNow.AddSeconds(graceSeconds);
            try
            {
                var taskId = await scheduler.ScheduleRetireAsync(
                        _state.SecretId,
                        _state.PreviousVersionNumber,
                        _state.Snapshot!.TenantId,
                        runAfter,
                        _state.RotationCorrelationId,
                        ct)
                    .ConfigureAwait(false);
                await EmitAsync(auditor, RotationAuditEvents.RetireScheduled,
                    versionNumber: _state.PreviousVersionNumber,
                    data: new Dictionary<string, object?>
                    {
                        ["runAfter"] = runAfter.ToString("O"),
                        ["graceSeconds"] = graceSeconds,
                        ["taskId"] = taskId,
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Activation already happened; scheduling failure is
                // logged but does NOT unwind the rotation — operators
                // can retry the sweeper manually.
                _logger?.LogWarning(ex,
                    "Failed to schedule retire-old task for secret {SecretId} v{Version}",
                    _state.SecretId, _state.PreviousVersionNumber);
                await EmitAsync(auditor, RotationAuditEvents.RetireScheduled,
                    versionNumber: _state.PreviousVersionNumber,
                    detail: "schedule_failed",
                    data: new Dictionary<string, object?>
                    {
                        ["errorType"] = ex.GetType().Name,
                    }).ConfigureAwait(false);
            }
        }
        else
        {
            await EmitAsync(auditor, RotationAuditEvents.RetireScheduled,
                detail: "no_previous_version").ConfigureAwait(false);
        }

        _state.Result = "activated";
        await EmitAsync(auditor, RotationAuditEvents.Completed, versionNumber: newVersion,
            data: new Dictionary<string, object?>
            {
                ["previousVersion"] = _state.PreviousVersionNumber,
            }).ConfigureAwait(false);
        return SagaOutcome.Activated;
    }

    private async Task<Exception?> TryPushAsync(
        IRotationHandler handler,
        RotationTarget target,
        RotationContext rotationContext,
        IRotationAuditEmitter auditor)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _pushDelays.Count; attempt++)
        {
            try
            {
                await handler.PushAsync(target, _state.NewPlaintext, rotationContext, _ct)
                    .ConfigureAwait(false);
                _state.Pushed = true;
                await EmitAsync(auditor, RotationAuditEvents.PushSuccess,
                    versionNumber: _state.NewVersionNumber,
                    data: new Dictionary<string, object?> { ["attempt"] = attempt + 1 })
                    .ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= _pushDelays.Count) break;
                try
                {
                    await Task.Delay(_pushDelays[attempt], _ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }
        }
        await EmitAsync(auditor, RotationAuditEvents.PushFailed,
            versionNumber: _state.NewVersionNumber,
            detail: last?.GetType().Name,
            data: new Dictionary<string, object?>
            {
                ["attempts"] = _pushDelays.Count + 1,
                ["message"] = Truncate(last?.Message, 240),
            }).ConfigureAwait(false);
        return last;
    }

    private async Task<string?> TryProbeAsync(
        IRotationHandler handler,
        RotationTarget target,
        RotationContext rotationContext,
        IRotationAuditEmitter auditor)
    {
        ProbeResult? last = null;
        for (var attempt = 0; attempt <= _probeDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(_probeDelays[attempt - 1], _ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }
            last = await handler.ProbeAsync(target, rotationContext, _ct)
                .ConfigureAwait(false);
            if (last.IsHealthy)
            {
                await EmitAsync(auditor, RotationAuditEvents.ProbeSuccess,
                    versionNumber: _state.NewVersionNumber,
                    data: new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt + 1,
                        ["durationMs"] = last.DurationMs,
                    }).ConfigureAwait(false);
                return null;
            }
        }
        await EmitAsync(auditor, RotationAuditEvents.ProbeFailed,
            versionNumber: _state.NewVersionNumber,
            detail: last?.Reason,
            data: new Dictionary<string, object?>
            {
                ["attempts"] = _probeDelays.Count + 1,
                ["durationMs"] = last?.DurationMs ?? 0,
            }).ConfigureAwait(false);
        return last?.Reason ?? "unknown";
    }

    private async Task CompensateAsync(
        IRotationHandler handler,
        RotationTarget target,
        RotationContext rotationContext,
        ISecretRotationGateway gateway,
        IRotationAuditEmitter auditor)
    {
        await EmitAsync(auditor, RotationAuditEvents.CompensationStarted,
            versionNumber: _state.NewVersionNumber,
            detail: _state.Error ?? "rollback").ConfigureAwait(false);

        try
        {
            // Roll back the downstream push FIRST (while the old
            // version is still Active) so the consumer is never
            // left orphaned.
            if (_state.Pushed)
            {
                await handler.RollbackAsync(
                        target,
                        _state.NewPlaintext,
                        rotationContext,
                        _ct)
                    .ConfigureAwait(false);
            }

            // Delete the pending version row.
            if (_state.NewVersionNumber > 0)
            {
                await gateway.DeleteVersionAsync(
                        _state.SecretId,
                        _state.NewVersionNumber,
                        _ct)
                    .ConfigureAwait(false);
            }

            await EmitAsync(auditor, RotationAuditEvents.CompensationSuccess,
                versionNumber: _state.NewVersionNumber).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await EmitAsync(auditor, RotationAuditEvents.CompensationFailed,
                versionNumber: _state.NewVersionNumber,
                detail: ex.GetType().Name,
                data: new Dictionary<string, object?>
                {
                    ["message"] = Truncate(ex.Message, 240),
                }).ConfigureAwait(false);
            // Do not rethrow — compensation-failed is terminal + emits
            // the alert event; the workflow still returns
            // "compensated" so callers see the saga didn't activate.
        }
    }

    private RotationContext BuildRotationContext() =>
        new(_state.RotationCorrelationId, _state.OperatorUserId, DryRun: false, _state.HandlerOptions);

    private RotationTarget BuildTarget() =>
        new(
            _state.Snapshot!.SecretId,
            _state.Snapshot.Name,
            _state.Snapshot.TenantId,
            _state.Snapshot.ConsumerSystem,
            _state.Snapshot.ConsumerIdentifier,
            _state.NewVersionNumber,
            _state.PreviousVersionNumber);

    private Task EmitAsync(
        IRotationAuditEmitter auditor,
        string eventType,
        int? versionNumber = null,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        auditor.EmitAsync(
            RotationAuditEvent.Create(
                eventType,
                _state.SecretId,
                _state.Snapshot?.TenantId,
                _state.RotationCorrelationId,
                versionNumber,
                detail,
                data),
            _ct);

    /// <summary>
    /// Wave C.4 §5 — emit the alert-shaped SECRET.ROTATION.FAILED event
    /// alongside the rotation-audit event. The audit event is consumed
    /// by Story 29-6 admin tooling (rotation timeline); the alert event
    /// is consumed by the Wave-C AlertRuleEvaluator. They share a source
    /// of truth (the saga's failure state) but serve different consumers
    /// so the shapes can diverge.
    ///
    /// <para>Invoked only when the saga is in a terminal failure state.
    /// <paramref name="failureStage"/> is the short machine-readable
    /// stage name ("mint" | "push" | "probe" | "activate" | "retire").
    /// </para>
    /// </summary>
    private async Task EmitAlertFailedAsync(
        string failureStage, bool compensationApplied)
    {
        if (_alertEmitter is null) return;

        // targetKind maps 1:1 with the RotationHandler.System key in
        // today's registry. Default to "unknown" pre-snapshot (when
        // the saga failed before it could resolve the handler).
        var targetKind = _state.HandlerSystem ?? "unknown";
        var cabinetName = _state.Snapshot?.Name ?? "(unresolved)";
        var handlerType = _state.HandlerSystem ?? "unknown";

        await _alertEmitter.EmitSecretRotationFailedAsync(
            new SecretRotationFailedEvent(
                TenantId: _state.Snapshot?.TenantId,
                CorrelationId: _state.RotationCorrelationId,
                TargetKind: targetKind,
                CabinetName: cabinetName,
                HandlerType: handlerType,
                FailureStage: failureStage,
                CompensationApplied: compensationApplied,
                LastError: _state.Error), _ct).ConfigureAwait(false);
    }

    private static string GenerateRandom(int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        var b64 = Convert.ToBase64String(buffer);
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }
}

internal enum SagaOutcome
{
    Activated,
    Compensated,
    Failed
}
