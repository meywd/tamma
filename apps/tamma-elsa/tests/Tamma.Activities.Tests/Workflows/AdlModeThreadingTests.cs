using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Core.Deployment;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// IMPORTANT fix (2026-06-22) — the production approval gate never engaged from
/// the real autonomous loop because <c>mode</c> was never threaded
/// <c>AdlOrchestratorWorkflow → DispatchCycleActivity → single-issue-cycle →
/// deployment-pipeline</c>. These tests lock the threading end-to-end (structural,
/// per the codebase convention — workflows/activities are not runnable in a unit
/// test without the Elsa runtime; the runtime mode-DECISION is covered by
/// <c>DeploymentModeTests</c>):
///
/// <list type="bullet">
///   <item>the orchestrator's DispatchIssueCycle node SETS a <c>Mode</c> and a
///     <c>TenantId</c> input (previously neither was set);</item>
///   <item><see cref="DispatchCycleActivity"/> exposes a <c>Mode</c> input and
///     injects <c>IConfiguration</c> (so it can derive the real operating mode);</item>
///   <item>the cycle threads <c>mode</c> into the deployment-pipeline dispatch.</item>
/// </list>
/// </summary>
[TestFixture]
public class AdlModeThreadingTests
{
    // ── Orchestrator → DispatchCycle: Mode + TenantId are now set ──────────

    [Test]
    public void Orchestrator_DispatchIssueCycle_SetsModeAndTenantInputs()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new AdlOrchestratorWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var dispatch = flowchart.Activities
            .OfType<DispatchCycleActivity>()
            .FirstOrDefault(a => a.Id == "DispatchIssueCycle");

        dispatch.Should().NotBeNull("the orchestrator must dispatch the single-issue cycle");

        // Mode + TenantId must be configured (non-null Input with an expression) so
        // a real value flows downstream into the deployment pipeline's gate. Before
        // the fix neither was set → mode was always "" → prod auto-deployed ungated.
        dispatch!.Mode.Should().NotBeNull("the cycle dispatch must thread a Mode input");
        dispatch.Mode.Expression.Should().NotBeNull(
            "Mode must be wired to an expression (pass-through input, derived from config when empty)");
        dispatch.TenantId.Should().NotBeNull("the cycle dispatch must thread a TenantId input");
        dispatch.TenantId.Expression.Should().NotBeNull("TenantId must be wired to an expression");
    }

    // ── DispatchCycleActivity exposes Mode + injects IConfiguration ────────

    [Test]
    public void DispatchCycleActivity_ExposesModeInput()
    {
        // Regression guard — the Mode input is the seam the orchestrator sets and
        // the activity reads (falling back to config-derived mode when empty).
        typeof(DispatchCycleActivity).GetProperty("Mode").Should().NotBeNull(
            "DispatchCycleActivity must expose a Mode input so the orchestrator can thread it");
    }

    [Test]
    public void DispatchCycleActivity_InjectsConfiguration_ToDeriveTheRealMode()
    {
        // The activity derives the real single-vs-SaaS mode from IConfiguration
        // (mirrors TammaModeProvider) when the Mode input is empty. The DI ctor must
        // therefore take IConfiguration.
        var ctor = typeof(DispatchCycleActivity).GetConstructors()
            .FirstOrDefault(c => c.GetParameters()
                .Any(p => p.ParameterType == typeof(Microsoft.Extensions.Configuration.IConfiguration)));
        ctor.Should().NotBeNull(
            "DispatchCycleActivity must inject IConfiguration to derive the operating mode for the prod gate");
    }

    // ── Cycle → deployment-pipeline: mode is threaded ─────────────────────

    [Test]
    public void Cycle_DeploymentPipelineDispatch_Exists_AndCycleReadsModeFromInput()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        // The deployment pipeline dispatch must exist (the leg that threads mode).
        var pipeline = flowchart.Activities
            .OfType<Elsa.Workflows.Runtime.Activities.DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DeploymentPipeline");
        pipeline.Should().NotBeNull("the cycle must dispatch the deployment-pipeline");
        ReadDefinitionId(pipeline!).Should().Be("deployment-pipeline");

        // The cycle declares a Mode variable it reads from its own `mode` input and
        // forwards to the pipeline (so a real value — not always "" — reaches the gate).
        builder.Object.Variables.Any(v => v.Name == "Mode").Should().BeTrue(
            "the cycle must carry a Mode variable threaded into the pipeline gate");

        // It also threads the operator force-flag so Deployment:RequireProdApproval
        // can force the gate even in dev mode.
        builder.Object.Variables.Any(v => v.Name == "RequireProdApproval").Should().BeTrue(
            "the cycle must carry RequireProdApproval threaded into the pipeline gate");
    }

    // ── DispatchCycleActivity.ResolveMode: the value that reaches the gate ──

    [Test]
    public void ResolveMode_SaaSConfig_ProducesBusiness_GateEngages()
    {
        // A SaaS deployment (TenantSharedSecret present) must produce "business" so
        // the deployment pipeline's ProdApprovalNeeded gate (mode == "business")
        // engages — the whole point of the fix.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:TenantSharedSecret"] = "hmac-secret",
            }).Build();

        DispatchCycleActivity.ResolveMode(explicitInput: "", cfg)
            .Should().Be(DeploymentMode.Business,
                "a SaaS deployment must thread mode=business so the prod approval gate engages");
    }

    [Test]
    public void ResolveMode_ControlPlaneConfig_ProducesBusiness()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = "Host=cp;Database=tamma",
            }).Build();

        DispatchCycleActivity.ResolveMode(null, cfg).Should().Be(DeploymentMode.Business);
    }

    [Test]
    public void ResolveMode_SingleUserConfig_ProducesDev()
    {
        // No SaaS signals → single-user → dev (deploys without a human gate unless
        // an operator forces it via Deployment:RequireProdApproval).
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        DispatchCycleActivity.ResolveMode(null, cfg).Should().Be(DeploymentMode.Dev);
    }

    [Test]
    public void ResolveMode_NoConfigAtAll_FailsSafeToBusiness_GateOn()
    {
        // 2026-08-18 — this assertion used to read `.Be(DeploymentMode.Dev)` under
        // the name "FailsSafeToDev", which is a contradiction: dev is the arm that
        // deploys to production with NO human. An UNREADABLE configuration is not
        // evidence of a self-hosted deployment — it is the absence of evidence, and
        // it is the state a store-rehydrated DispatchCycleActivity is actually in
        // inside the deployed engine ([JsonConstructor] leaves _configuration null).
        // So the deployment most likely to hit this arm was a SaaS one, and it got
        // the gate switched off. Unknown now means gate ON.
        DispatchCycleActivity.ResolveMode(null, configuration: null)
            .Should().Be(DeploymentMode.Business,
                "an unreadable configuration must not be read as 'no SaaS signal' — gate ON");

        // An explicit "saas" input engages the gate regardless of config.
        DispatchCycleActivity.ResolveMode("saas", configuration: null)
            .Should().Be(DeploymentMode.Business, "an explicit saas mode must engage the gate even without config");

        // An explicit single-user input is still honoured — this is a fail-safe on
        // ABSENT information, not a refusal to believe an operator.
        DispatchCycleActivity.ResolveMode("single-user", configuration: null)
            .Should().Be(DeploymentMode.Dev);
    }

    /// <summary>
    /// The gate's two enable terms both read <c>IConfiguration</c>, and in the
    /// deployed engine the activity instance is store-rehydrated with every
    /// DI field null (findings 27/28). Pin that the activity does not read the
    /// ctor field directly for either term: it must fall back to
    /// <c>context.GetService&lt;IConfiguration&gt;()</c> first, and the pure
    /// resolver must fail safe when even that yields nothing.
    /// </summary>
    [Test]
    public void ResolveMode_RehydratedInstance_DoesNotSilentlyDisableTheProdGate()
    {
        // The rehydration ctor is the one Elsa uses; it takes no configuration.
        var rehydrated = new DispatchCycleActivity();

        var configField = typeof(DispatchCycleActivity)
            .GetField("_configuration",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        configField.Should().NotBeNull();
        configField!.GetValue(rehydrated).Should().BeNull(
            "the [JsonConstructor] path is exactly the state that used to resolve mode=dev");

        // With nothing readable, the resolver the activity calls must gate.
        DispatchCycleActivity.ResolveMode(null, (IConfiguration?)null)
            .Should().Be(DeploymentMode.Business);
    }

    [Test]
    public void ResolveMode_ExplicitTammaModeSaaS_ProducesBusiness()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:Mode"] = "saas",
            }).Build();

        DispatchCycleActivity.ResolveMode(null, cfg).Should().Be(DeploymentMode.Business);
    }

    private static string? ReadDefinitionId(Elsa.Workflows.Runtime.Activities.DispatchWorkflow dispatch)
    {
        var prop = typeof(Elsa.Workflows.Runtime.Activities.DispatchWorkflow)
            .GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}
