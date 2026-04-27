using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.Security;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

// ═══════════════════════════════════════════════════════════════════════
// H6 / Story 28-5 AC7 — Cleanup Workflow primitives
//
// This file is the central seam for the per-step Elsa decomposition of
// what used to be CleanUpFailedTenantActivity (a 200-line hand-rolled
// mini-orchestrator inside ONE Elsa activity). The decomposition splits
// the cleanup into N+1 activities run by an outer Sequence:
//
//    Sequence:
//      EvictTenantPoolForCleanupActivity     (best-effort)
//      DropTenantDatabaseForCleanupActivity  (best-effort)
//      DropTenantRoleForCleanupActivity      (best-effort)
//      SoftDeleteTenantRowActivity           (best-effort)
//      EmitCleanupTerminalEventActivity      (terminal, reads the others)
//
// Each per-step activity:
//   • Inherits from CleanupStepActivity (this file).
//   • Catches its own exception → writes failure code + redacted detail
//     into workflow variables. Does NOT throw (so the next sibling step
//     still runs — continue-on-error semantics from the original).
//   • Emits TENANT.DELETE.STEP_STARTED / STEP_COMPLETED / STEP_FAILED so
//     the existing Story 28-5 step-dedup index still applies.
//
// EmitCleanupTerminalEventActivity reads the accumulated state, emits
// EXACTLY ONE terminal event (TENANT.DELETED.SUCCESS or
// TENANT.DELETE.FAILED), and on partial failure flips
// tenants.ProvisioningState='requires_manual_cleanup' so an operator can
// see the row needs intervention.
//
// The single-terminal-event invariant matches Story 28-5 — the
// dashboard's tenant timeline expects one and only one terminal record
// per cleanup run.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Step name constants — short kebab-case identifiers used as
/// <c>tags-&gt;&gt;'step'</c> values on the platform-event rows. Stable
/// over time so dashboard queries don't break on a rename.
/// </summary>
public static class CleanupSteps
{
    public const string EvictPool = "evict-pool";
    public const string DropDatabase = "drop-tenant-db";
    public const string DropRole = "drop-tenant-role";
    public const string SoftDeleteRow = "soft-delete-cp-row";
}

/// <summary>
/// Workflow-variable names used by the cleanup Sequence to share state
/// across sibling steps. Elsa workflow variables are persisted in the
/// workflow state, so the accumulator survives suspend/replay/cancel
/// between steps — this is the crux of the H6 fix.
/// </summary>
internal static class CleanupWorkflowVariables
{
    /// <summary>JSON list of failed step names (string[]).</summary>
    public const string FailedStepsJson = "Tenant.CleanupStep.FailedSteps";

    /// <summary>JSON dict of step-name → "errorCode: redactedMessage".</summary>
    public const string StepDetailsJson = "Tenant.CleanupStep.StepDetails";

    /// <summary>JSON list of succeeded step names (string[]).</summary>
    public const string SucceededStepsJson = "Tenant.CleanupStep.SucceededSteps";

    /// <summary>Per-step Success flag — for fast lookup. Variable name
    /// pattern: <c>Tenant.CleanupStep.{stepName}.Success</c> (bool).</summary>
    public static string SuccessFlag(string step) =>
        $"Tenant.CleanupStep.{step}.Success";

    /// <summary>Per-step FailureCode — for fast lookup. Variable name
    /// pattern: <c>Tenant.CleanupStep.{stepName}.FailureCode</c>
    /// (string — exception type name, redacted by IErrorRedactor).</summary>
    public static string FailureCode(string step) =>
        $"Tenant.CleanupStep.{step}.FailureCode";
}

/// <summary>
/// Tiny abstraction over the workflow's variable bag. Real activities
/// pass a wrapper around <see cref="ActivityExecutionContext"/>; tests
/// pass an in-memory dictionary so the pure state-machine logic in
/// <see cref="CleanupWorkflowState"/> is unit-testable without standing
/// up an Elsa runtime.
/// </summary>
public interface ICleanupStateStore
{
    string? GetString(string variable);
    void SetString(string variable, string? value);
    void SetBool(string variable, bool value);
}

/// <summary>
/// Real <see cref="ICleanupStateStore"/> backed by an Elsa
/// <see cref="ActivityExecutionContext"/>. Variables are persisted via
/// the workflow state — the accumulator survives suspend/replay/cancel
/// between sibling steps, which is the crux of the H6 fix.
/// </summary>
internal sealed class ActivityContextStateStore : ICleanupStateStore
{
    private readonly ActivityExecutionContext _context;

    public ActivityContextStateStore(ActivityExecutionContext context) =>
        _context = context;

    public string? GetString(string variable) =>
        _context.GetVariable<string>(variable);

    public void SetString(string variable, string? value) =>
        _context.SetVariable(variable, value);

    public void SetBool(string variable, bool value) =>
        _context.SetVariable(variable, value);
}

/// <summary>
/// In-memory <see cref="ICleanupStateStore"/> for unit tests. Mirrors
/// the workflow-variable bag without Elsa plumbing — the
/// <see cref="CleanupWorkflowState"/> static methods round-trip
/// identically against this and the real backend.
/// </summary>
public sealed class InMemoryCleanupStateStore : ICleanupStateStore
{
    private readonly Dictionary<string, object?> _store = new();

    public IReadOnlyDictionary<string, object?> Variables => _store;

    public string? GetString(string variable) =>
        _store.TryGetValue(variable, out var v) ? v as string : null;

    public void SetString(string variable, string? value) => _store[variable] = value;
    public void SetBool(string variable, bool value) => _store[variable] = value;

    public bool? GetBool(string variable) =>
        _store.TryGetValue(variable, out var v) ? v as bool? : null;
}

/// <summary>
/// Helpers for reading/writing the cleanup workflow accumulator state.
/// All methods take an <see cref="ICleanupStateStore"/> so the JSON
/// serialization round-trip is unit-testable in isolation.
///
/// <para>Lists/dicts are persisted as JSON in string variables. Elsa's
/// default variable storage is fine for primitives but unreliable for
/// arbitrary collection shapes across workflow checkpoints — JSON gives
/// us deterministic round-trip behaviour for free.</para>
/// </summary>
public static class CleanupWorkflowState
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    // ── ActivityExecutionContext-bound entry points (used by the
    //    real activities at runtime) ──────────────────────────────────

    public static void RecordSuccess(ActivityExecutionContext context, string step) =>
        RecordSuccess(new ActivityContextStateStore(context), step);

    public static void RecordFailure(
        ActivityExecutionContext context,
        string step,
        string failureCode,
        string redactedDetail) =>
        RecordFailure(new ActivityContextStateStore(context), step, failureCode, redactedDetail);

    public static IReadOnlyList<string> GetFailedSteps(ActivityExecutionContext context) =>
        GetFailedSteps(new ActivityContextStateStore(context));

    public static IReadOnlyList<string> GetSucceededSteps(ActivityExecutionContext context) =>
        GetSucceededSteps(new ActivityContextStateStore(context));

    public static IReadOnlyDictionary<string, string> GetStepDetails(
        ActivityExecutionContext context) =>
        GetStepDetails(new ActivityContextStateStore(context));

    // ── Pure store-bound implementations (testable) ──────────────────

    /// <summary>Mark a step as succeeded — appends to the in-flight
    /// "succeeded" list and sets the per-step Success flag.</summary>
    public static void RecordSuccess(ICleanupStateStore store, string step)
    {
        var succeeded = ReadStepList(store, CleanupWorkflowVariables.SucceededStepsJson);
        if (!succeeded.Contains(step))
            succeeded.Add(step);
        WriteStepList(store, CleanupWorkflowVariables.SucceededStepsJson, succeeded);
        store.SetBool(CleanupWorkflowVariables.SuccessFlag(step), true);
    }

    /// <summary>Mark a step as failed — appends to the in-flight "failed"
    /// list, sets the FailureCode variable, and records redacted detail
    /// in the per-run dictionary.</summary>
    public static void RecordFailure(
        ICleanupStateStore store,
        string step,
        string failureCode,
        string redactedDetail)
    {
        var failed = ReadStepList(store, CleanupWorkflowVariables.FailedStepsJson);
        if (!failed.Contains(step))
            failed.Add(step);
        WriteStepList(store, CleanupWorkflowVariables.FailedStepsJson, failed);

        store.SetBool(CleanupWorkflowVariables.SuccessFlag(step), false);
        store.SetString(CleanupWorkflowVariables.FailureCode(step), failureCode);

        var details = ReadStepDetails(store);
        details[step] = $"{failureCode}: {redactedDetail}";
        WriteStepDetails(store, details);
    }

    public static IReadOnlyList<string> GetFailedSteps(ICleanupStateStore store) =>
        ReadStepList(store, CleanupWorkflowVariables.FailedStepsJson);

    public static IReadOnlyList<string> GetSucceededSteps(ICleanupStateStore store) =>
        ReadStepList(store, CleanupWorkflowVariables.SucceededStepsJson);

    public static IReadOnlyDictionary<string, string> GetStepDetails(ICleanupStateStore store) =>
        ReadStepDetails(store);

    private static List<string> ReadStepList(ICleanupStateStore store, string variable)
    {
        var raw = store.GetString(variable);
        if (string.IsNullOrEmpty(raw)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOpts)
                   ?? new List<string>();
        }
        catch
        {
            // Corrupt accumulator state (shouldn't happen — same writer is
            // the only producer). Reset rather than crash the workflow.
            return new List<string>();
        }
    }

    private static void WriteStepList(
        ICleanupStateStore store,
        string variable,
        List<string> list) =>
        store.SetString(variable, JsonSerializer.Serialize(list, JsonOpts));

    private static Dictionary<string, string> ReadStepDetails(ICleanupStateStore store)
    {
        var raw = store.GetString(CleanupWorkflowVariables.StepDetailsJson);
        if (string.IsNullOrEmpty(raw)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw, JsonOpts)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static void WriteStepDetails(
        ICleanupStateStore store,
        Dictionary<string, string> details) =>
        store.SetString(
            CleanupWorkflowVariables.StepDetailsJson,
            JsonSerializer.Serialize(details, JsonOpts));
}

/// <summary>
/// Base class for the four per-step cleanup activities. Wraps
/// <see cref="DoStepAsync"/> with continue-on-error semantics:
///
/// <list type="bullet">
///   <item><description>Reads <c>tenantId</c> from the workflow variable
///     (or from the <see cref="TenantId"/> input — whichever is bound).
///     A missing/empty tenantId is treated as a step failure rather than
///     a hard throw, since the surrounding <c>Sequence</c> still has
///     work to do (the terminal event will report the bad input).</description></item>
///   <item><description>Emits <c>TENANT.DELETE.STEP_STARTED</c> before
///     the work, then either <c>STEP_COMPLETED</c> on success or
///     <c>STEP_FAILED</c> with a redacted error payload on exception.
///     Same partial-unique <c>(tenant_id, type, step, attempt)</c>
///     dedup index as the regular delete workflow.</description></item>
///   <item><description>On exception: caught, redacted via
///     <see cref="IErrorRedactor"/>, recorded into
///     <see cref="CleanupWorkflowState"/>, then the activity returns
///     normally — Elsa's <c>Sequence</c> continues with the next sibling
///     step. <b>Never throws.</b></description></item>
/// </list>
///
/// <para>Sibling to <see cref="TenantLifecycleActivity"/> — same
/// per-step event taxonomy, opposite failure semantics. The lifecycle
/// base throws so <c>DeleteTenantWorkflow</c> can abort cleanly on a
/// retryable failure; this base swallows so cleanup pushes through every
/// step regardless.</para>
/// </summary>
public abstract class CleanupStepActivity : TammaAsyncActivity
{
    [Input(Description = "Tenant id this cleanup step targets. If unbound, the activity reads the workflow variable 'TenantId'.")]
    public Input<Guid> TenantId { get; set; } = new(Guid.Empty);

    [Input(Description = "Retry attempt number, defaults to 1.")]
    public Input<int> Attempt { get; set; } = new(1);

    /// <summary>Short kebab-case step name (e.g. <c>evict-pool</c>).
    /// Used as the <c>tags-&gt;&gt;'step'</c> value on emitted events
    /// and as the workflow-variable key for per-step success/failure
    /// flags.</summary>
    public abstract string StepName { get; }

    public override string? EventType => $"TENANT.CLEANUP.{StepName.ToUpperInvariant().Replace('-', '_')}";

    protected sealed override async Task RunAsync(ActivityExecutionContext context)
    {
        Logger ??= context.GetService<ILogger<CleanupStepActivity>>();

        var tenantId = ResolveTenantId(context);
        var attempt = ResolveAttempt(context);
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        if (tenantId == Guid.Empty)
        {
            // Defensive: input never bound + no workflow variable set.
            // Record the step as failed and return — the terminal step
            // will surface this as a partial-failure outcome.
            CleanupWorkflowState.RecordFailure(
                context,
                StepName,
                "InvalidInput",
                "tenantId workflow variable was empty/unset.");
            await SafePublish(publisher, BuildStepEvent(
                TenantLifecycleEvents.DeleteStepFailed,
                tenantId, attempt,
                errorType: "InvalidInput",
                redactedMessage: "tenantId workflow variable was empty/unset."));
            return;
        }

        await SafePublish(publisher, BuildStepEvent(
            TenantLifecycleEvents.DeleteStepStarted, tenantId, attempt));

        try
        {
            await DoStepAsync(context, tenantId).ConfigureAwait(false);

            CleanupWorkflowState.RecordSuccess(context, StepName);
            await SafePublish(publisher, BuildStepEvent(
                TenantLifecycleEvents.DeleteStepCompleted, tenantId, attempt));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Cooperative cancellation — propagate so the workflow
            // engine can suspend cleanly. NOT a step failure.
            throw;
        }
        catch (Exception ex)
        {
            // PF — re-port of the per-step failure classifier. Restores
            // the rich, fixed-vocabulary failure codes
            // (drop_database_failed / drop_role_failed / network_error /
            // permission_denied / evict_pool_failed / cancelled /
            // step_failed) lost when the original
            // CleanUpFailedTenantActivity classifier was deleted during
            // the H6 decomposition merge. Dashboards + alerting group
            // on these codes; reverting to ex.GetType().Name regressed
            // the operator UX from "permission_denied" to
            // "InvalidOperationException".
            var redactor = context.GetService<IErrorRedactor>();
            var (failureCode, redactedSnippet) = CleanupFailureClassifier
                .ClassifyFailure(StepName, ex, redactor);

            CleanupWorkflowState.RecordFailure(context, StepName, failureCode, redactedSnippet);

            Logger?.LogWarning(
                ex,
                "tenant.cleanup.step_failed step={Step} tenantId={TenantId} failureCode={FailureCode}",
                StepName, tenantId, failureCode);

            await SafePublish(publisher, BuildStepEvent(
                TenantLifecycleEvents.DeleteStepFailed,
                tenantId, attempt,
                errorType: failureCode,
                redactedMessage: redactedSnippet));
            // SWALLOW — continue-on-error contract.
        }
    }

    /// <summary>The actual cleanup work. Concrete activities implement
    /// the destructive op and may freely throw — this base catches.</summary>
    protected abstract Task DoStepAsync(ActivityExecutionContext context, Guid tenantId);

    private Guid ResolveTenantId(ActivityExecutionContext context)
    {
        var bound = TenantId.Get(context);
        if (bound != Guid.Empty) return bound;

        // Fall back to the workflow variable for sequences that don't
        // bind the input on every activity (the user task explicitly
        // says "Takes the tenantId from a workflow variable").
        var raw = context.GetVariable<object?>("TenantId");
        return raw switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var p) => p,
            _ => Guid.Empty,
        };
    }

    private int ResolveAttempt(ActivityExecutionContext context)
    {
        var raw = Attempt.Get(context);
        return raw <= 0 ? 1 : raw;
    }

    private PlatformEventPayload BuildStepEvent(
        string type,
        Guid tenantId,
        int attempt,
        string? errorType = null,
        string? redactedMessage = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["source"] = "cleanup-workflow",
        };
        if (errorType is not null) data["errorType"] = errorType;
        if (redactedMessage is not null) data["message"] = redactedMessage;

        return new PlatformEventPayload(
            type,
            tenantId,
            StepName,
            attempt,
            data);
    }

    private static async Task SafePublish(
        IPlatformEventPublisher publisher,
        PlatformEventPayload payload)
    {
        try
        {
            var evt = TenantLifecycleEvents.BuildEvent(
                payload.Type,
                payload.TenantId,
                step: payload.Step,
                attempt: payload.Attempt,
                data: payload.Data);
            await publisher.AppendAndPublishAsync(evt).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: publisher failures during per-step emission
            // must NOT cascade. The accumulator state in the workflow
            // variables is already authoritative; the terminal event
            // will report the run regardless.
        }
    }

    /// <summary>Internal value-tuple-style payload used by the base —
    /// keeps <see cref="BuildStepEvent"/> signatures terse without
    /// leaking shape into the public API.</summary>
    private readonly record struct PlatformEventPayload(
        string Type,
        Guid TenantId,
        string Step,
        int Attempt,
        IReadOnlyDictionary<string, object?>? Data);
}

/// <summary>
/// Terminal step of the cleanup <see cref="CleanupStepActivity"/>
/// sequence. Reads the accumulator state, picks the right outcome:
///
/// <list type="bullet">
///   <item><description>All 4 prior steps recorded success →
///     <c>TENANT.DELETED.SUCCESS</c> + the row stays in the soft-deleted
///     state set by <see cref="SoftDeleteTenantRowActivity"/>.</description></item>
///   <item><description>Any step failed → <c>TENANT.DELETE.FAILED</c>
///     with the failed-step list + redacted per-step detail in the
///     event payload + <c>tenants.ProvisioningState =
///     'requires_manual_cleanup'</c> on the row so an operator sees the
///     run needs intervention.</description></item>
/// </list>
///
/// <para>This is the SINGLE terminal event the cleanup workflow emits —
/// per the Story 28-5 contract the dashboard's tenant timeline relies
/// on.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Emit Cleanup Terminal Event",
    "Read accumulated step state, emit one terminal TENANT.DELETED.* event, mark row for manual review on partial failure.",
    Kind = ActivityKind.Task)]
public sealed class EmitCleanupTerminalEventActivity : TammaAsyncActivity
{
    private const int MaxSummaryChars = 1900;

    [Input(Description = "Tenant id whose cleanup is concluding. If unbound, reads the workflow variable 'TenantId'.")]
    public Input<Guid> TenantId { get; set; } = new(Guid.Empty);

    [Input(Description = "Optional operator note attached to the terminal event.")]
    public Input<string?> Note { get; set; } = new(default(string));

    public override string? EventType => "TENANT.CLEANUP.TERMINAL";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        Logger ??= context.GetService<ILogger<EmitCleanupTerminalEventActivity>>();

        var tenantId = ResolveTenantId(context);
        var note = Note.Get(context);

        var failedSteps = CleanupWorkflowState.GetFailedSteps(context);
        var succeededSteps = CleanupWorkflowState.GetSucceededSteps(context);
        var stepDetails = CleanupWorkflowState.GetStepDetails(context);

        if (tenantId == Guid.Empty)
        {
            Logger?.LogError(
                "tenant.cleanup.terminal_event_skipped reason=empty_tenant_id failedSteps={FailedSteps}",
                string.Join(",", failedSteps));
            return;
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        // Stamp the row state — partial failure flags it for manual
        // review; full success keeps the soft-delete state set by
        // SoftDeleteTenantRowActivity.
        await UpdateTenantRowAsync(context, tenantId, note, failedSteps, stepDetails)
            .ConfigureAwait(false);

        // Single terminal event.
        if (failedSteps.Count == 0)
        {
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    TenantLifecycleEvents.DeletedSuccess,
                    tenantId,
                    data: new Dictionary<string, object?>
                    {
                        ["source"] = "cleanup-workflow",
                        ["note"] = note,
                        ["succeededSteps"] = succeededSteps,
                    }),
                context.CancellationToken).ConfigureAwait(false);

            Logger?.LogInformation(
                "tenant.cleanup.success tenantId={TenantId} succeededSteps={SucceededSteps}",
                tenantId, string.Join(",", succeededSteps));
        }
        else
        {
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    "TENANT.DELETE.FAILED",
                    tenantId,
                    data: new Dictionary<string, object?>
                    {
                        ["source"] = "cleanup-workflow",
                        ["failedSteps"] = failedSteps,
                        ["succeededSteps"] = succeededSteps,
                        ["stepDetails"] = stepDetails,
                        ["note"] = note,
                        ["requiresManualCleanup"] = true,
                    }),
                context.CancellationToken).ConfigureAwait(false);

            Logger?.LogWarning(
                "tenant.cleanup.partial tenantId={TenantId} failedSteps={FailedSteps} succeededSteps={SucceededSteps}",
                tenantId,
                string.Join(",", failedSteps),
                string.Join(",", succeededSteps));
        }
    }

    private Guid ResolveTenantId(ActivityExecutionContext context)
    {
        var bound = TenantId.Get(context);
        if (bound != Guid.Empty) return bound;
        var raw = context.GetVariable<object?>("TenantId");
        return raw switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var p) => p,
            _ => Guid.Empty,
        };
    }

    private async Task UpdateTenantRowAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        string? note,
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> stepDetails)
    {
        try
        {
            var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
            await using var db = await factory
                .CreateDbContextAsync(context.CancellationToken)
                .ConfigureAwait(false);

            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
                .ConfigureAwait(false);
            if (tenant is null)
            {
                Logger?.LogWarning(
                    "tenant.cleanup.terminal_row_not_found tenantId={TenantId}",
                    tenantId);
                return;
            }

            tenant.UpdatedAt = DateTime.UtcNow;
            if (failedSteps.Count == 0)
            {
                tenant.ProvisioningState = "none";
                tenant.ProvisioningDetail = note ??
                    "Cleaned up via /api/admin/tenants/{id}/cleanup.";
            }
            else
            {
                tenant.ProvisioningState = "requires_manual_cleanup";
                tenant.ProvisioningDetail = BuildFailureSummary(failedSteps, stepDetails);
            }
            tenant.ProvisioningUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Even the row-stamp is best-effort — if the CP DB is
            // unhealthy we still want to fire the terminal event so the
            // dashboard sees the run completed (with a partial mark in
            // the event payload). The operator's next signal is the
            // event, not the row.
            Logger?.LogError(
                ex,
                "tenant.cleanup.terminal_row_update_failed tenantId={TenantId}",
                tenantId);
        }
    }

    private static string BuildFailureSummary(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> details)
    {
        var summary = $"Cleanup partial — {failedSteps.Count} step(s) failed: " +
            string.Join("; ",
                failedSteps.Select(s => $"{s}: {(details.TryGetValue(s, out var d) ? d : "(no detail)")}"));
        return summary.Length > MaxSummaryChars ? summary[..MaxSummaryChars] : summary;
    }

    /// <summary>Exposed for test callers — same truncation contract used by
    /// the activity at runtime.</summary>
    public static string BuildFailureSummaryForTesting(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> details) =>
        BuildFailureSummary(failedSteps, details);
}
