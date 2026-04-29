using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-1 AC6: every model is immutable, value-equality, and
/// supports `with` projection.
/// </summary>
[TestFixture]
public sealed class ModelRecordsTests
{
    [Test]
    public void Repo_is_value_equal()
    {
        var a = new Repo("github.com", "octocat", "hello", "main", false, null,
            "https://x", "https://y");
        var b = new Repo("github.com", "octocat", "hello", "main", false, null,
            "https://x", "https://y");
        a.Should().Be(b);
    }

    [Test]
    public void Repo_with_expression_creates_copy()
    {
        var a = new Repo("github.com", "octocat", "hello", "main", false, null,
            "https://x", "https://y");
        var b = a with { IsPrivate = true };

        b.IsPrivate.Should().BeTrue();
        a.IsPrivate.Should().BeFalse("with-expression is non-mutating");
    }

    [Test]
    public void PullRequest_state_and_draft_are_independent()
    {
        var open = new PullRequest("1", "t", null, "f", "m",
            PullRequestState.Open, IsDraft: true,
            "u", "u", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        open.State.Should().Be(PullRequestState.Open);
        open.IsDraft.Should().BeTrue();

        var openNotDraft = open with { IsDraft = false };
        openNotDraft.State.Should().Be(PullRequestState.Open);
        openNotDraft.IsDraft.Should().BeFalse();
    }

    [Test]
    public void Issue_labels_are_readonly_list()
    {
        var labels = new[] { "bug", "p1" };
        var issue = new Issue("12", "title", null, IssueState.Open, "url", labels);
        issue.Labels.Should().BeAssignableTo<IReadOnlyList<string>>();
        issue.Labels.Should().Equal(labels);
    }

    [Test]
    public void WorkflowDispatchRequest_default_variables_is_null()
    {
        var req = new WorkflowDispatchRequest(
            Ref: "main",
            WorkflowFileName: "ci.yml",
            Inputs: new Dictionary<string, string>());
        req.Variables.Should().BeNull();
    }

    [Test]
    public void WorkflowDispatchRequest_carries_optional_variables()
    {
        var req = new WorkflowDispatchRequest(
            Ref: "main",
            WorkflowFileName: null,
            Inputs: new Dictionary<string, string> { ["k"] = "v" },
            Variables: new Dictionary<string, string> { ["env"] = "prod" });
        req.Variables.Should().NotBeNull();
        req.Variables!["env"].Should().Be("prod");
    }

    [Test]
    public void PrFileStatus_has_Other_catchall()
    {
        // Drivers map exotic platform values to Other rather than
        // crashing — sanity check the enum includes it.
        Enum.IsDefined(typeof(PrFileStatus), PrFileStatus.Other)
            .Should().BeTrue();
    }

    [Test]
    public void PullRequestState_does_not_include_Draft_state()
    {
        // Locked design: Draft is a flag on PullRequest, not a state
        // value. This test fails loudly if someone re-introduces it.
        Enum.GetNames(typeof(PullRequestState))
            .Should().NotContain("Draft");
    }

    [Test]
    public void All_model_records_are_sealed()
    {
        var modelTypes = typeof(Repo).Assembly.GetTypes()
            .Where(t => t.Namespace == "Tamma.Platforms.Abstractions.Models")
            .Where(t => t.IsClass && !t.IsAbstract);

        foreach (var t in modelTypes)
        {
            t.IsSealed.Should().BeTrue($"{t.Name} should be sealed");
        }
    }

    [Test]
    public void PlatformInstallation_carries_tenant_binding()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var inst = new PlatformInstallation(
            id, tenantId, PlatformKind.GitLab,
            "https://gitlab.example.com", "42");

        inst.Id.Should().Be(id);
        inst.TenantId.Should().Be(tenantId);
        inst.Kind.Should().Be(PlatformKind.GitLab);
        inst.BaseUrl.Should().Be("https://gitlab.example.com");
        inst.InstallationExternalId.Should().Be("42");
    }
}
