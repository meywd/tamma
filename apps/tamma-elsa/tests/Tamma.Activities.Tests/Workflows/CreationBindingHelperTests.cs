using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-15 — pins the PURE, fail-closed projections of <see cref="CreationBindingHelper"/>
/// (the task-creation / test-case-creation binding cores). Every projection returns the
/// conservative empty-array on garbage (never a throw), and the failure detail names the typed
/// outcome wire.
/// </summary>
[TestFixture]
public class CreationBindingHelperTests
{
    [Test]
    public void ProjectTasksArray_ReadsTasksArrayFromPlanObject()
        => CreationBindingHelper.ProjectTasksArray("{\"tasks\":[{\"id\":\"T-1\"}]}")
            .Should().Be("[{\"id\":\"T-1\"}]");

    [Test]
    public void ProjectTasksArray_PassesThroughBareArray()
        => CreationBindingHelper.ProjectTasksArray("[{\"id\":\"T-1\"}]")
            .Should().Be("[{\"id\":\"T-1\"}]");

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("{\"tasks\":[]}")]
    [TestCase("{\"nope\":1}")]
    [TestCase("[]")]
    public void ProjectTasksArray_FailsClosedToEmptyArray(string input)
        => CreationBindingHelper.ProjectTasksArray(input).Should().Be("[]");

    [Test]
    public void ProjectTestCasesArray_ReadsTestCasesOrTestsAlias()
    {
        CreationBindingHelper.ProjectTestCasesArray("{\"testCases\":[{\"id\":\"TC-1\"}]}").Should().Be("[{\"id\":\"TC-1\"}]");
        CreationBindingHelper.ProjectTestCasesArray("{\"tests\":[{\"id\":\"TC-2\"}]}").Should().Be("[{\"id\":\"TC-2\"}]");
    }

    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("{\"testCases\":[]}")]
    public void ProjectTestCasesArray_FailsClosedToEmptyArray(string input)
        => CreationBindingHelper.ProjectTestCasesArray(input).Should().Be("[]");

    [Test]
    public void BuildTaskIdContext_WrapsBareTasksArrayIntoPlanObject()
        => CreationBindingHelper.BuildTaskIdContext("[{\"id\":\"T-1\"}]")
            .Should().Be("{\"tasks\":[{\"id\":\"T-1\"}]}");

    [Test]
    public void BuildTaskIdContext_PassesThroughPlanObject()
        => CreationBindingHelper.BuildTaskIdContext("{\"tasks\":[{\"id\":\"T-1\"}]}")
            .Should().Be("{\"tasks\":[{\"id\":\"T-1\"}]}");

    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("[]")]
    [TestCase("{\"tasks\":[]}")]
    public void BuildTaskIdContext_FailsClosedToEmpty(string input)
        => CreationBindingHelper.BuildTaskIdContext(input).Should().BeEmpty();

    [Test]
    public void BuildFailureDetail_NamesOutcomeWireWhenPresent()
    {
        var exit = new LifecycleBindingHelper.LifecycleExit(
            DocumentLifecycleResult.StatusEscalated,
            DocumentLifecycleOutcome.ValidationExhausted.ToWire(), null, "{}", "");
        CreationBindingHelper.BuildFailureDetail(exit)
            .Should().Contain("escalated").And.Contain(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
    }

    [Test]
    public void ScopeIssueId_SuffixesProducer()
        => CreationBindingHelper.ScopeIssueId("owner/repo#7", "task-creation")
            .Should().Be("owner/repo#7#task-creation");

    [Test]
    public void DeriveIssueId_MatchesRepoHashNumber()
        => CreationBindingHelper.DeriveIssueId("owner/repo", 7).Should().Be("owner/repo#7");
}
