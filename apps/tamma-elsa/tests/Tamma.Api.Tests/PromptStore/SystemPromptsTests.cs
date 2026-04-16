using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Tests for the <see cref="SystemPrompts"/> static registry.
/// Verifies that every role/action combination is populated and that
/// system prompt / action default lookups work.
/// </summary>
[TestFixture]
public class SystemPromptsTests
{
    private static readonly string[] Roles =
    [
        "developer",
        "tester",
        "security",
        "devops",
        "architect",
        "product_owner",
        "senior_developer",
        "tech_writer",
    ];

    private static readonly string[] Actions =
    [
        "context-scan",
        "plan",
        "plan-review",
        "implement",
        "write-tests",
        "refactor",
        "code-review",
        "triage",
        "summarize",
        "debug",
    ];

    [Test]
    public void RoleActionTemplates_ContainsAllEightyCombinations()
    {
        SystemPrompts.RoleActionTemplates.Should().HaveCount(80);
    }

    [Test]
    public void RoleSystemPrompts_ContainsAllEightRoles()
    {
        SystemPrompts.RoleSystemPrompts.Should().HaveCount(8);
        foreach (var role in Roles)
        {
            SystemPrompts.RoleSystemPrompts.Should().ContainKey(role);
            SystemPrompts.RoleSystemPrompts[role].Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void ActionDefaults_ContainsAllTenActions()
    {
        SystemPrompts.ActionDefaults.Should().HaveCount(10);
        foreach (var action in Actions)
        {
            SystemPrompts.ActionDefaults.Should().ContainKey(action);
            SystemPrompts.ActionDefaults[action].Template.Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestCaseSource(nameof(AllRoleActionPairs))]
    public void GetRoleAction_ReturnsTemplateForEveryRoleActionPair(string role, string action)
    {
        var template = SystemPrompts.GetRoleAction(role, action);

        template.Should().NotBeNull();
        template!.Role.Should().Be(role);
        template.Action.Should().Be(action);
        template.Template.Should().NotBeNullOrWhiteSpace();
        template.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        template.Variables.Should().NotBeEmpty();
    }

    [Test]
    public void GetRoleAction_ReturnsNullForUnknownRole()
    {
        SystemPrompts.GetRoleAction("unknown-role", "plan").Should().BeNull();
    }

    [Test]
    public void GetRoleAction_ReturnsNullForUnknownAction()
    {
        SystemPrompts.GetRoleAction("developer", "unknown-action").Should().BeNull();
    }

    [Test]
    public void GetActionDefault_ReturnsTemplateForEveryAction()
    {
        foreach (var action in Actions)
        {
            var template = SystemPrompts.GetActionDefault(action);
            template.Should().NotBeNull($"action default for '{action}' should exist");
            template!.Action.Should().Be(action);
            template.Template.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void GetActionDefault_ReturnsNullForUnknownAction()
    {
        SystemPrompts.GetActionDefault("not-a-real-action").Should().BeNull();
    }

    [Test]
    public void RoleActionTemplates_AllHaveNonEmptyVariables()
    {
        foreach (var template in SystemPrompts.RoleActionTemplates)
        {
            template.Variables.Should().NotBeEmpty(
                $"template for {template.Role}/{template.Action} should declare variables");
        }
    }

    [Test]
    public void RoleActionTemplates_EachHasSystemPromptMatchingItsRole()
    {
        foreach (var template in SystemPrompts.RoleActionTemplates)
        {
            template.SystemPrompt.Should().Be(
                SystemPrompts.RoleSystemPrompts[template.Role],
                $"template for {template.Role}/{template.Action} should use role system prompt");
        }
    }

    [Test]
    public void Developer_ImplementPrompt_HasExpectedVariables()
    {
        var template = SystemPrompts.GetRoleAction("developer", "implement");
        template.Should().NotBeNull();
        template!.Variables.Should().Contain("workItemJson")
                            .And.Contain("planJson")
                            .And.Contain("currentTask");
    }

    [Test]
    public void ToolEnablement_ReviewStyleActions_AreDisabledForTools()
    {
        // Review/triage/summarize style actions should not need tools
        SystemPrompts.GetRoleAction("developer", "plan-review")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("developer", "code-review")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("developer", "triage")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("developer", "summarize")!.EnableTools.Should().BeFalse();
    }

    [Test]
    public void ToolEnablement_ImplementAction_EnablesTools()
    {
        SystemPrompts.GetRoleAction("developer", "implement")!.EnableTools.Should().BeTrue();
    }

    public static IEnumerable<TestCaseData> AllRoleActionPairs()
    {
        foreach (var role in Roles)
        {
            foreach (var action in Actions)
            {
                yield return new TestCaseData(role, action).SetName($"{role}/{action}");
            }
        }
    }
}
