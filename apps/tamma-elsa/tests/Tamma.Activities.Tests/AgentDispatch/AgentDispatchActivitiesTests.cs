using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Sanity-level tests for the ELSA activity wrappers. Elsa's
/// ActivityExecutionContext can't be easily instantiated in isolation,
/// so we focus on the constructor contract + JSON round-trip (how Elsa
/// actually materialises activities from a workflow definition).
///
/// The business logic is covered by the <c>*ServiceTests</c> counterparts
/// where the pure services are exercised directly.
/// </summary>
[TestFixture]
public class AgentDispatchActivitiesTests
{
    [Test]
    public void DispatchAgentWorkflowActivity_JsonConstructor_DoesNotThrow()
    {
        Action act = () => new DispatchAgentWorkflowActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void DispatchAgentWorkflowActivity_WithDependencies_DoesNotThrow()
    {
        var logger = new Mock<ILogger<DispatchAgentWorkflowActivity>>();
        var svc = new Mock<IAgentDispatchService>();

        Action act = () => new DispatchAgentWorkflowActivity(logger.Object, svc.Object);
        act.Should().NotThrow();
    }

    [Test]
    public void MonitorAgentWorkflowActivity_JsonConstructor_DoesNotThrow()
    {
        Action act = () => new MonitorAgentWorkflowActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void MonitorAgentWorkflowActivity_WithDependencies_DoesNotThrow()
    {
        var logger = new Mock<ILogger<MonitorAgentWorkflowActivity>>();
        var svc = new Mock<IAgentMonitorService>();

        Action act = () => new MonitorAgentWorkflowActivity(logger.Object, svc.Object);
        act.Should().NotThrow();
    }

    [Test]
    public void CollectAgentResultsActivity_JsonConstructor_DoesNotThrow()
    {
        Action act = () => new CollectAgentResultsActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void CollectAgentResultsActivity_WithDependencies_DoesNotThrow()
    {
        var logger = new Mock<ILogger<CollectAgentResultsActivity>>();
        var svc = new Mock<IAgentResultCollectorService>();

        Action act = () => new CollectAgentResultsActivity(logger.Object, svc.Object);
        act.Should().NotThrow();
    }

    [Test]
    public void ExecuteAgentActivity_JsonConstructor_DoesNotThrow()
    {
        Action act = () => new ExecuteAgentActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void DispatchAgentWorkflowActivity_EventType_IsStable()
    {
        new DispatchAgentWorkflowActivity().EventType.Should().Be("AGENT.DISPATCH");
    }

    [Test]
    public void MonitorAgentWorkflowActivity_EventType_IsStable()
    {
        new MonitorAgentWorkflowActivity().EventType.Should().Be("AGENT.MONITOR");
    }

    [Test]
    public void CollectAgentResultsActivity_EventType_IsStable()
    {
        new CollectAgentResultsActivity().EventType.Should().Be("AGENT.RESULTS");
    }

    [Test]
    public void ExecuteAgentActivity_EventType_IsStable()
    {
        new ExecuteAgentActivity().EventType.Should().Be("AGENT.EXECUTION");
    }
}
