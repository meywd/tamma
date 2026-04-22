using Elsa.Workflows;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 6 — enqueue a <c>RETIRE_SECRET_VERSION</c>
/// platform-queued-task with a future <c>RunAfter</c> (default 15 min,
/// overridable per-secret via the rotation input) that the
/// <see cref="RotationSweeper"/> picks up and retires the previous
/// version. Also emits the <c>RETIRE_SCHEDULED</c> event so operators
/// see the grace window's expiry time on the admin timeline.
///
/// <para>When <see cref="RotationWorkflowState.PreviousVersionNumber"/>
/// is 0 (first rotation — no predecessor) the activity is a no-op
/// with a <c>NoPreviousVersion</c> detail event so the audit trail
/// still tells the story.</para>
/// </summary>
public class ScheduleRetireOldActivity : RotationActivityBase
{
    public override string StepName => "schedule-retire";

    public const long DefaultGraceWindowSeconds = 900; // 15 minutes

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var state = GetState(context);

        if (state.PreviousVersionNumber <= 0)
        {
            await EmitAsync(
                context,
                RotationAuditEvents.RetireScheduled,
                detail: "no_previous_version",
                data: new Dictionary<string, object?>
                {
                    ["note"] = "First-rotation — nothing to retire.",
                }).ConfigureAwait(false);
            return;
        }

        var graceSeconds = state.GraceWindowSeconds <= 0
            ? DefaultGraceWindowSeconds
            : state.GraceWindowSeconds;
        var runAfter = DateTimeOffset.UtcNow.AddSeconds(graceSeconds);

        var scheduler = ResolveRetireScheduler(context);
        var taskId = await scheduler.ScheduleRetireAsync(
                state.SecretId,
                state.PreviousVersionNumber,
                state.Snapshot?.TenantId,
                runAfter,
                state.RotationCorrelationId,
                context.CancellationToken)
            .ConfigureAwait(false);

        await EmitAsync(
            context,
            RotationAuditEvents.RetireScheduled,
            versionNumber: state.PreviousVersionNumber,
            data: new Dictionary<string, object?>
            {
                ["runAfter"] = runAfter.ToString("O"),
                ["graceSeconds"] = graceSeconds,
                ["taskId"] = taskId,
            }).ConfigureAwait(false);
    }
}
