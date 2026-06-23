using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.SecretsRotation.Activities;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.Tests.SecretsRotation;

/// <summary>
/// Engine-side rotation-audit fix — <see cref="DrainRotationAuditEmitter"/>
/// is the implementation of <see cref="IRotationAuditEmitter"/> registered in
/// <c>Tamma.ElsaServer</c> (the API's <c>RotationAuditEmitter</c> can't be
/// referenced there). It maps each <see cref="RotationAuditEvent"/> to a
/// <see cref="TammaEvent"/> and appends it to the workflow's
/// <c>tamma:events</c> list so the DCB-event drain
/// (<see cref="EventPersistenceMiddleware"/>) persists it to
/// <c>domain_events</c> — instead of throwing
/// <c>No service for type IRotationAuditEmitter</c> at runtime.
///
/// <para>The ambient <c>tamma:events</c> list is set per-saga-run by
/// <see cref="RotateSecretSagaActivity"/> (which holds the
/// <c>ActivityExecutionContext</c>). These tests drive the emitter through
/// the same ambient seam without standing up an Elsa runtime — mirroring the
/// "Elsa's context can't be cheaply mocked, test the extracted seam" pattern
/// used by <c>CheckBudgetActivityEmissionTests</c>.</para>
/// </summary>
[TestFixture]
public class DrainRotationAuditEmitterTests
{
    private static readonly Guid SecretId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Test]
    public async Task EmitAsync_AppendsMappedTammaEventToAmbientDrainList()
    {
        var events = new List<TammaEvent>();
        var emitter = new DrainRotationAuditEmitter();

        using (RotationAuditDrainScope.Begin(events, activityId: "act-1",
                   activityName: "Rotate Secret Saga", workflowInstanceId: "wf-99"))
        {
            await emitter.EmitAsync(
                RotationAuditEvent.Create(
                    RotationAuditEvents.Staged,
                    SecretId,
                    TenantId,
                    rotationCorrelationId: "rot_abc",
                    versionNumber: 3,
                    detail: null,
                    data: new Dictionary<string, object?> { ["previousVersion"] = 2 }),
                ct: default);
        }

        events.Should().ContainSingle();
        var evt = events[0];
        evt.EventType.Should().Be(RotationAuditEvents.Staged);
        evt.Status.Should().Be("success");
        // Activity/workflow identifiers ride through so the persisted row is
        // joinable to the saga run.
        evt.ActivityId.Should().Be("act-1");
        evt.ActivityName.Should().Be("Rotate Secret Saga");
        evt.WorkflowInstanceId.Should().Be("wf-99");

        // Tags carry the same queryable index keys the Api-side emitter writes.
        evt.Tags.Should().NotBeNull();
        evt.Tags!["secretId"].Should().Be(SecretId);
        evt.Tags["tenantId"].Should().Be(TenantId);
        evt.Tags["rotationCorrelationId"].Should().Be("rot_abc");
        evt.Tags["versionNumber"].Should().Be(3);

        // Data carries the structured payload (+ detail when present).
        evt.Data.Should().ContainKey("previousVersion");
        evt.Data["previousVersion"].Should().Be(2);
    }

    [Test]
    public async Task EmitAsync_FailedEvent_MapsErrorStatusAndDetail()
    {
        var events = new List<TammaEvent>();
        var emitter = new DrainRotationAuditEmitter();

        using (RotationAuditDrainScope.Begin(events, null, null, "wf-1"))
        {
            await emitter.EmitAsync(
                RotationAuditEvent.Create(
                    RotationAuditEvents.Failed,
                    SecretId,
                    tenantId: null,
                    rotationCorrelationId: "rot_x",
                    detail: "probe_failed:timeout"),
                ct: default);
        }

        var evt = events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(RotationAuditEvents.Failed);
        evt.Status.Should().Be("error");
        evt.Error.Should().Be("probe_failed:timeout");
        evt.Data["detail"].Should().Be("probe_failed:timeout");
        // Null tenant rotations still emit (platform-level secret) — tenantId
        // tag is present but null, exactly like the Api-side emitter.
        evt.Tags!["tenantId"].Should().BeNull();
    }

    [Test]
    public async Task EmitAsync_WithNoAmbientScope_DoesNotThrowAndDropsSilently()
    {
        // The interface contract: EmitAsync must NOT throw on a persistence
        // gap (the caller has already mutated state). With no ambient drain
        // list (e.g. resolved outside a saga run) the emit is a logged no-op,
        // never a crash — the whole point of the fix.
        var emitter = new DrainRotationAuditEmitter();

        var act = async () => await emitter.EmitAsync(
            RotationAuditEvent.Create(
                RotationAuditEvents.Completed, SecretId, TenantId, "rot_y"),
            ct: default);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task EmitAsync_ResolvedFromActivityScope_StyleSaga_DoesNotThrow()
    {
        // Regression for the engine crash: in production the saga resolves the
        // emitter and emits ~8 events. Emitting a full sequence through the
        // ambient seam must persist all of them without throwing.
        var events = new List<TammaEvent>();
        var emitter = new DrainRotationAuditEmitter();

        using (RotationAuditDrainScope.Begin(events, "act", "saga", "wf"))
        {
            foreach (var type in new[]
                     {
                         RotationAuditEvents.Started, RotationAuditEvents.Staged,
                         RotationAuditEvents.PushSuccess, RotationAuditEvents.ProbeSuccess,
                         RotationAuditEvents.Switched, RotationAuditEvents.Activated,
                         RotationAuditEvents.RetireScheduled, RotationAuditEvents.Completed,
                     })
            {
                await emitter.EmitAsync(
                    RotationAuditEvent.Create(type, SecretId, TenantId, "rot_seq"),
                    ct: default);
            }
        }

        events.Should().HaveCount(8);
        events.Select(e => e.EventType).Should().Contain(RotationAuditEvents.Completed);
    }

    [Test]
    public void DrainScope_RestoresPreviousAmbientOnDispose()
    {
        var outer = new List<TammaEvent>();
        var inner = new List<TammaEvent>();

        using (RotationAuditDrainScope.Begin(outer, null, null, "wf-outer"))
        {
            RotationAuditDrainScope.Current.Should().NotBeNull();
            RotationAuditDrainScope.Current!.Events.Should().BeSameAs(outer);

            using (RotationAuditDrainScope.Begin(inner, null, null, "wf-inner"))
            {
                RotationAuditDrainScope.Current!.Events.Should().BeSameAs(inner);
            }

            // Inner scope disposed → outer restored.
            RotationAuditDrainScope.Current!.Events.Should().BeSameAs(outer);
        }

        RotationAuditDrainScope.Current.Should().BeNull();
    }
}
