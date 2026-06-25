using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Blocker;
using Tamma.Activities.Blocker.Models;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness build-out 2026-06-22 (<c>BlockerDiagnosis.md</c>, 7-1G AC2/AC6/AC9) —
/// structural verification of the corrected <c>blocker-diagnosis</c> graph:
///   - a <c>tenantId</c> string variable is captured (Epic 32 tenant-scoping),
///   - DCB audit emits are wired (a Diagnosed emit + a Terminal emit on the flowchart;
///     per-level RESOLUTION_ATTEMPTED / PROGRESS emits inside the level Sequences),
///   - the terminal BLOCKER emit runs immediately BEFORE SetOutput,
///   - every LLM interaction still routes through the <c>llm-call</c> mediation seam
///     (no direct provider call — pivot rule #1),
///   - the per-level DetectProgress + escalation bookmark activities expose the new
///     <c>TimedOut</c> output wiring.
///
/// A self-contained mock <see cref="IWorkflowBuilder"/> is used (the shared
/// WorkflowStructureTests harness only sets up the Epic-13 variable types).
/// </summary>
[TestFixture]
public class BlockerDiagnosisWorkflowStructureTests
{
    private static Flowchart BuildFlowchart()
    {
        var mockBuilder = new Mock<IWorkflowBuilder>();
        IActivity? root = null;
        var variables = new List<Variable>();

        mockBuilder.SetupSet(b => b.Name = It.IsAny<string>());
        mockBuilder.SetupSet(b => b.DefinitionId = It.IsAny<string>());
        mockBuilder.SetupSet(b => b.Description = It.IsAny<string>());
        mockBuilder.SetupSet(b => b.Version = It.IsAny<int>());
        mockBuilder.SetupSet(b => b.Root = It.IsAny<IActivity>()).Callback<IActivity>(v => root = v);
        mockBuilder.SetupGet(b => b.Root).Returns(() => root!);
        mockBuilder.SetupGet(b => b.Variables).Returns(variables);

        // Generic WithVariable<T>() — return a fresh typed Variable for every type the
        // workflow declares.
        SetupVar<Guid>(mockBuilder, variables);
        SetupVar<string>(mockBuilder, variables);
        SetupVar<string?>(mockBuilder, variables);
        SetupVar<int>(mockBuilder, variables);
        SetupVar<bool>(mockBuilder, variables);
        SetupVar<GitActivitySignal>(mockBuilder, variables);
        SetupVar<CIStatusSignal>(mockBuilder, variables);
        SetupVar<InactivitySignal>(mockBuilder, variables);
        SetupVar<CommunicationSignal>(mockBuilder, variables);
        SetupVar<AggregatedSignals>(mockBuilder, variables);
        SetupVar<BlockerDiagnosisResult>(mockBuilder, variables);
        SetupVar<ProgressDetectionResult>(mockBuilder, variables);
        SetupVar<IDictionary<string, object>?>(mockBuilder, variables);

        // WithVariable<T>(name, default) overloads used (string / int / bool).
        mockBuilder.Setup(b => b.WithVariable<string>(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string n, string d) => { var v = new Variable<string>(n, d); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<int>(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int d) => { var v = new Variable<int>(n, d); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<bool>(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string n, bool d) => { var v = new Variable<bool>(n, d); variables.Add(v); return v; });

        var build = typeof(BlockerDiagnosisWorkflow).GetMethod(
            "Build", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(IWorkflowBuilder) }, null);
        build!.Invoke(new BlockerDiagnosisWorkflow(), new object[] { mockBuilder.Object });

        root.Should().BeOfType<Flowchart>();
        return (Flowchart)root!;
    }

    private static void SetupVar<T>(Mock<IWorkflowBuilder> mockBuilder, List<Variable> variables)
        => mockBuilder.Setup(b => b.WithVariable<T>())
            .Returns(() => { var v = new Variable<T>(); variables.Add(v); return v; });

    private static IEnumerable<IActivity> Flatten(IActivity activity)
    {
        yield return activity;
        switch (activity)
        {
            case Sequence seq:
                foreach (var child in seq.Activities)
                    foreach (var d in Flatten(child)) yield return d;
                break;
            case Elsa.Workflows.Activities.Parallel par:
                foreach (var child in par.Activities)
                    foreach (var d in Flatten(child)) yield return d;
                break;
            case If iff:
                if (iff.Then is not null)
                    foreach (var d in Flatten(iff.Then)) yield return d;
                break;
            case Flowchart fc:
                foreach (var child in fc.Activities)
                    foreach (var d in Flatten(child)) yield return d;
                break;
        }
    }

    [Test]
    public void Workflow_CapturesInputsAndThreadsTenantIntoEmits()
    {
        var fc = BuildFlowchart();
        var capture = fc.Activities.OfType<SetVariable>().FirstOrDefault(a => a.Id == "CaptureInputs");
        capture.Should().NotBeNull("CaptureInputs must run");

        // Every BLOCKER emit carries a TenantId input bound to the captured tenant variable
        // (Epic 32 tenant-scoping). Inspecting the DispatchWorkflow Input dictionary is not
        // statically possible (it is a runtime delegate), so the emit TenantId wiring is the
        // observable proxy that tenantId is threaded through.
        var emits = Flatten(fc).OfType<EmitBlockerEventActivity>().ToList();
        emits.Should().NotBeEmpty();
        emits.Should().OnlyContain(e => e.TenantId != null,
            "every BLOCKER.* event must be tenant-tagged");
    }

    [Test]
    public void Workflow_EmitsDiagnosedAndTerminalBlockerEvents()
    {
        var fc = BuildFlowchart();
        var allEmits = Flatten(fc).OfType<EmitBlockerEventActivity>().ToList();

        allEmits.Should().NotBeEmpty("the corrected graph must emit BLOCKER.* DCB events (AC9)");
        allEmits.Select(e => e.Id).Should().Contain("EmitDiagnosed");
        allEmits.Select(e => e.Id).Should().Contain("EmitTerminal");
        allEmits.Select(e => e.Id).Should().Contain("EmitEscalated");
        // Per-level attempt + progress emits are present inside the level Sequences.
        allEmits.Select(e => e.Id).Should().Contain("HintEmitAttempt");
        allEmits.Select(e => e.Id).Should().Contain("HintEmitProgress");
    }

    [Test]
    public void Workflow_TerminalEmitRunsImmediatelyBeforeSetOutput()
    {
        var fc = BuildFlowchart();
        // The flowchart has a connection EmitTerminal → SetBlockerOutput.
        var hasEdge = fc.Connections.Any(c =>
            c.Source.Activity.Id == "EmitTerminal" && c.Target.Activity.Id == "SetBlockerOutput");
        hasEdge.Should().BeTrue("the terminal BLOCKER event must be emitted right before SetOutput");
    }

    [Test]
    public void Workflow_AllLlmCallsRouteThroughLlmCallMediationSeam()
    {
        var fc = BuildFlowchart();
        var dispatches = Flatten(fc).OfType<DispatchWorkflow>().ToList();

        dispatches.Should().NotBeEmpty();
        foreach (var d in dispatches)
        {
            var defId = d.WorkflowDefinitionId.Expression?.Value?.ToString();
            defId.Should().Be("llm-call",
                "every LLM interaction must route through the llm-call mediation seam (pivot rule #1)");
        }
    }

    [Test]
    public void Workflow_DetectProgressActivities_WireTimedOutOutput()
    {
        var fc = BuildFlowchart();
        var detects = Flatten(fc).OfType<DetectProgressActivity>().ToList();

        detects.Should().HaveCountGreaterThanOrEqualTo(3, "Hint/Guidance/Assistance each wait for progress");
        detects.Should().OnlyContain(d => d.TimedOut != null,
            "each per-level wait must surface the durable TimedOut output");
    }

    [Test]
    public void Workflow_EscalationActivity_WiresResolvedAndTimedOutOutputs()
    {
        var fc = BuildFlowchart();
        var escalate = Flatten(fc).OfType<EscalateToSeniorActivity>().Single();

        escalate.Resolved.Should().NotBeNull("a senior-resolved escalation must flip isResolved");
        escalate.TimedOut.Should().NotBeNull("an expired SLA must flip the Timeout terminal");
    }
}
