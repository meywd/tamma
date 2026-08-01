using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>TEMPORARY adversarial probe — delete after the review.</summary>
[TestFixture]
public class ZzAdversarialProbeTests
{
    private static bool EvaluateProdApprovalNeeded(
        string mode, bool requireProdApproval, string gateOutcome)
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);
        var decision = flowchart.Activities.OfType<FlowDecision>()
            .Single(d => d.Id == "ProdApprovalNeeded");

        var input = typeof(FlowDecision).GetProperty("Condition",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(decision);
        var expression = input!.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(input) as Expression;
        if (expression?.Value is not Delegate del)
            throw new InvalidOperationException("Condition is not delegate-backed.");

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var ctx = new ExpressionExecutionContext(
            NullSp.Instance, memory, null, null, null, default);

        var counter = 0;
        foreach (var v in builder.Object.Variables)
        {
            if (string.IsNullOrEmpty(v.Id)) v.Id = $"probe-{counter++}";
            try { memory.Declare(v); } catch { /* dup */ }
        }

        foreach (var v in builder.Object.Variables)
        {
            switch (v.Name)
            {
                case "Mode": v.Set(ctx, mode); break;
                case "RequireProdApproval": v.Set(ctx, requireProdApproval); break;
                case "ProdGateOutcome": v.Set(ctx, gateOutcome); break;
            }
        }

        var raw = del.DynamicInvoke(ctx);
        if (raw is bool b) return b;
        if (raw is ValueTask<bool> vt) return vt.GetAwaiter().GetResult();
        if (raw is Task<bool> t) return t.GetAwaiter().GetResult();
        if (raw is not null && raw.GetType().Name.StartsWith("ValueTask", StringComparison.Ordinal))
        {
            var res = raw.GetType().GetProperty("Result")!.GetValue(raw);
            return (bool)res!;
        }
        throw new InvalidOperationException($"got {raw} of type {raw?.GetType().FullName}");
    }

    [Test]
    public void ProbeVariableNames()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        TestContext.Out.WriteLine("VARS: " + string.Join(", ",
            builder.Object.Variables.Select(v => $"{v.Name}:{v.GetType().Name}")));
        Assert.Pass();
    }

    [Test]
    public void REAL_predicate_denied_outcome()
    {
        TestContext.Out.WriteLine($"dev/false/'automated'      -> {EvaluateProdApprovalNeeded("dev", false, "automated")}");
        TestContext.Out.WriteLine($"dev/false/'requires-human' -> {EvaluateProdApprovalNeeded("dev", false, "requires-human")}");
        TestContext.Out.WriteLine($"dev/false/'denied'         -> {EvaluateProdApprovalNeeded("dev", false, "denied")}");
        TestContext.Out.WriteLine($"dev/false/'unavailable'    -> {EvaluateProdApprovalNeeded("dev", false, "unavailable")}");
        TestContext.Out.WriteLine($"dev/false/''               -> {EvaluateProdApprovalNeeded("dev", false, "")}");
        TestContext.Out.WriteLine($"business/false/'denied'    -> {EvaluateProdApprovalNeeded("business", false, "denied")}");
        Assert.Pass();
    }

    private sealed class NullSp : IServiceProvider
    {
        public static readonly NullSp Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
