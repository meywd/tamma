using System.Text.Json.Serialization;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Tamma.Activities.Core;

namespace Tamma.Activities.Tests.Core;

/// <summary>
/// Minimal real <see cref="TammaActivity"/> used by
/// <see cref="EventPersistencePipelineTests"/> to prove the activity-execution
/// pipeline actually invokes activities (C1). It records a static side-effect
/// flag on run and emits <c>TEST.SIDE_EFFECT.*</c> events via the standard
/// <see cref="TammaEventEmitter"/> path — so a working pipeline both flips the
/// flag AND populates the <c>tamma:events</c> transient list for the drain.
///
/// <para>Default <see cref="ActivityKind.Action"/> (no <c>Kind = Task</c>) so
/// it runs inline through the default activity invoker — its events are in the
/// list by the time the drain middleware runs.</para>
/// </summary>
[Activity("Tamma.Tests", "Side Effect", "Test-only activity that records a side effect and emits an event")]
public class SideEffectActivity : TammaActivity
{
    private static volatile bool _ran;

    /// <summary>True once <see cref="Run"/> has executed in the current test.</summary>
    public static bool Ran => _ran;

    public static void Reset() => _ran = false;

    public override string? EventType => "TEST.SIDE_EFFECT";

    [JsonConstructor]
    public SideEffectActivity() { }

    protected override void Run(ActivityExecutionContext context) => _ran = true;
}
