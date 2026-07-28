using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// One EXPLICIT expected set per group (Story 43-3 AC3/AC5/AC11) — not just
/// counts. This is what makes a reassignment a reviewed diff: moving
/// <c>implement-infrastructure</c> from <c>authoring</c> to <c>deploy-control</c>
/// fails two NAMED assertions rather than silently shifting two counts. The
/// four contested calls (43-3 D5) carry their rationale as comments on the
/// descriptors themselves.
/// </summary>
[TestFixture]
public class ActionGroupMembershipTests
{
    private static string[] WiresIn(ActionGroup group) =>
        ActionCatalog.ByGroup[group].Select(k => k.ToWire()).OrderBy(w => w, StringComparer.Ordinal).ToArray();

    [Test]
    public void PlanningAndAnalysis_has_the_29_expected_members()
    {
        WiresIn(ActionGroup.PlanningAndAnalysis).Should().BeEquivalentTo(new[]
        {
            "agent-action:context-scan", "agent-action:triage-intake", "agent-action:clarify-requirements",
            "agent-action:plan-scope", "agent-action:define-acceptance-criteria", "agent-action:prioritize-backlog",
            "agent-action:plan-roadmap", "agent-action:generate-assessment-questions",
            "agent-action:analyze-assessment-response", "agent-action:research", "agent-action:score-ambiguity",
            "agent-action:triage-technical", "agent-action:assess-technical-risk", "agent-action:create-tasks",
            "agent-action:debug-rootcause", "agent-action:resolve-blocker", "agent-action:decompose-issue",
            "agent-action:plan-debugging", "agent-action:triage-context-scan", "agent-action:plan-test-strategy",
            "agent-action:triage-defect", "agent-action:threat-model", "agent-action:assess-vulnerability",
            "agent-action:audit-dependencies", "agent-action:analyze-security-incident",
            "agent-action:monitor-health", "agent-action:diagnose-incident", "agent-action:plan-incident-response",
            "agent-action:assess-capacity",
        });
    }

    [Test]
    public void Authoring_has_the_19_expected_members()
    {
        // Includes implement-infrastructure (43-3 D5.1 — the assignment most
        // likely to be overruled) and the write-tests family (D5.2).
        WiresIn(ActionGroup.Authoring).Should().BeEquivalentTo(new[]
        {
            "agent-action:incorporate-answers", "agent-action:plan-system-design",
            "agent-action:design-api-contract", "agent-action:design-data-model",
            "agent-action:design-integration", "agent-action:plan-migration-strategy",
            "agent-action:propose-design", "agent-action:plan-implementation", "agent-action:plan-refactor",
            "agent-action:plan-fix", "agent-action:implement-feature", "agent-action:implement-fix",
            "agent-action:write-tests", "agent-action:refactor", "agent-action:debug",
            "agent-action:address-review-comments", "agent-action:write-test-cases",
            "agent-action:write-regression-test", "agent-action:implement-infrastructure",
        });
    }

    [Test]
    public void ReviewAndAcceptance_has_the_16_agent_plus_10_document_members()
    {
        WiresIn(ActionGroup.ReviewAndAcceptance).Should().BeEquivalentTo(new[]
        {
            "agent-action:review-acceptance", "agent-action:review-scope", "agent-action:plan-review",
            "agent-action:code-review-architecture", "agent-action:code-review", "agent-action:mentor-feedback",
            "agent-action:self-review", "agent-action:review-feasibility", "agent-action:verify-acceptance",
            "agent-action:code-review-coverage", "agent-action:review-testability",
            "agent-action:plan-review-security", "agent-action:code-review-security",
            "agent-action:review-compliance", "agent-action:review-operability", "agent-action:review-docs",
            "document-type:findings", "document-type:ambiguity-assessment", "document-type:clarification",
            "document-type:decomposition", "document-type:plan", "document-type:design",
            "document-type:review", "document-type:triage-decision", "document-type:diagnosis",
            "document-type:test-spec",
        });
    }

    [Test]
    public void Docs_has_the_10_expected_members()
    {
        WiresIn(ActionGroup.Docs).Should().BeEquivalentTo(new[]
        {
            "agent-action:summarize-stakeholder", "agent-action:write-adr", "agent-action:summarize-technical",
            "agent-action:write-postmortem", "agent-action:summarize-changes", "agent-action:write-user-docs",
            "agent-action:write-api-docs", "agent-action:write-release-notes", "agent-action:write-runbook",
            "agent-action:update-changelog",
        });
    }

    [Test]
    public void CodeRead_has_the_3_expected_members()
    {
        WiresIn(ActionGroup.CodeRead).Should().BeEquivalentTo(new[]
        {
            "tool:file_read", "tool:search_code", "tool:get_acceptance_rules",
        });
    }

    [Test]
    public void CodeWrite_has_the_1_expected_member()
    {
        WiresIn(ActionGroup.CodeWrite).Should().BeEquivalentTo(new[] { "tool:file_write" });
    }

    [Test]
    public void CommandExecution_has_the_2_expected_members()
    {
        WiresIn(ActionGroup.CommandExecution).Should().BeEquivalentTo(new[]
        {
            "tool:shell_execute", "effect:process.spawn",
        });
    }

    [Test]
    public void CiAndTest_has_the_3_expected_members()
    {
        // Executing tests, not writing them (43-3 D5.2).
        WiresIn(ActionGroup.CiAndTest).Should().BeEquivalentTo(new[]
        {
            "agent-action:exploratory-test", "tool:run_tests", "effect:ci.tests.trigger",
        });
    }

    [Test]
    public void SourceControlRead_has_the_1_expected_member()
    {
        WiresIn(ActionGroup.SourceControlRead).Should().BeEquivalentTo(new[] { "tool:git_operations.read" });
    }

    [Test]
    public void SourceControlWrite_has_the_6_expected_members()
    {
        WiresIn(ActionGroup.SourceControlWrite).Should().BeEquivalentTo(new[]
        {
            "tool:git_operations.write", "effect:git.branch.create", "effect:git.branch.delete",
            "effect:git.pull-request.create", "effect:git.pull-request.merge", "effect:git.release.create",
        });
    }

    [Test]
    public void IssueTracking_has_the_2_expected_members()
    {
        WiresIn(ActionGroup.IssueTracking).Should().BeEquivalentTo(new[]
        {
            "effect:git.issue.patch", "effect:jira.ticket.patch",
        });
    }

    [Test]
    public void DeployControl_has_the_6_expected_members()
    {
        // implement-infrastructure is deliberately NOT here (43-3 D5.1).
        WiresIn(ActionGroup.DeployControl).Should().BeEquivalentTo(new[]
        {
            "agent-action:plan-deployment", "agent-action:configure-cicd", "agent-action:deploy",
            "agent-action:rollback", "effect:deploy.promote-prod", "effect:deploy.rollback",
        });
    }

    [Test]
    public void ExternalComms_has_the_2_expected_members()
    {
        WiresIn(ActionGroup.ExternalComms).Should().BeEquivalentTo(new[]
        {
            "effect:notify.slack.queue", "effect:notify.email.send",
        });
    }

    [Test]
    public void ModelInvocation_has_the_3_expected_members()
    {
        WiresIn(ActionGroup.ModelInvocation).Should().BeEquivalentTo(new[]
        {
            "effect:llm.call", "effect:mcp.tool.invoke", "effect:agent-dispatch.run",
        });
    }

    [Test]
    public void Secrets_has_the_4_expected_members()
    {
        // audit-secrets sits here, not planning-and-analysis: subject dominates
        // verb for this group (43-3 D5.4).
        WiresIn(ActionGroup.Secrets).Should().BeEquivalentTo(new[]
        {
            "agent-action:audit-secrets", "effect:secret.reveal",
            "automation:secret-auto-rotation-scheduler", "automation:retire-sweep",
        });
    }

    [Test]
    public void PlatformAutomation_has_the_37_expected_members()
    {
        var expected = new List<string>
        {
            "effect:engine.events.append", "effect:engine.platform-events.append",
            "effect:engine.document.persist", "effect:engine.document.set-status",
            "effect:engine.channel-outbox.enqueue",
        };
        // The 24 automation members outside the secrets group.
        expected.AddRange(Enum.GetValues<BackgroundActor>()
            .Where(b => b is not (BackgroundActor.SecretAutoRotationScheduler or BackgroundActor.RetireSweep))
            .Select(b => $"automation:{b.ToWire()}"));
        // All 8 platform tasks.
        expected.AddRange(Enum.GetValues<PlatformTaskKind>().Select(p => $"platform-task:{p.ToWire()}"));

        expected.Should().HaveCount(37, "5 engine effects + 24 automation + 8 platform tasks");
        WiresIn(ActionGroup.PlatformAutomation).Should().BeEquivalentTo(expected);
    }

    [Test]
    public void The_per_group_counts_sum_to_154()
    {
        var counts = new Dictionary<ActionGroup, int>
        {
            [ActionGroup.PlanningAndAnalysis] = 29,
            [ActionGroup.Authoring] = 19,
            [ActionGroup.ReviewAndAcceptance] = 26,
            [ActionGroup.Docs] = 10,
            [ActionGroup.CodeRead] = 3,
            [ActionGroup.CodeWrite] = 1,
            [ActionGroup.CommandExecution] = 2,
            [ActionGroup.CiAndTest] = 3,
            [ActionGroup.SourceControlRead] = 1,
            [ActionGroup.SourceControlWrite] = 6,
            [ActionGroup.IssueTracking] = 2,
            [ActionGroup.DeployControl] = 6,
            [ActionGroup.ExternalComms] = 2,
            [ActionGroup.ModelInvocation] = 3,
            [ActionGroup.Secrets] = 4,
            [ActionGroup.PlatformAutomation] = 37,
        };

        counts.Values.Sum().Should().Be(154);
        foreach (var (group, count) in counts)
            ActionCatalog.ByGroup[group].Should().HaveCount(count, $"group '{group.ToWire()}'");
    }
}
