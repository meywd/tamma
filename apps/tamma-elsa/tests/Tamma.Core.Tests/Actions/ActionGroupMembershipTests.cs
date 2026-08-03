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
    public void PlanningAndAnalysis_has_the_37_expected_members()
    {
        // 29 → 37 (Story 41-1a): + triage-tech-debt, triage-pr, manage-regression,
        // incident-rootcause, facilitate-retro, track-impediments, coordinate-release,
        // audit-accessibility — all analysis/triage producers of understanding.
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
            "agent-action:triage-tech-debt", "agent-action:triage-pr", "agent-action:manage-regression",
            "agent-action:incident-rootcause", "agent-action:facilitate-retro", "agent-action:track-impediments",
            "agent-action:coordinate-release", "agent-action:audit-accessibility",
        });
    }

    [Test]
    public void Authoring_has_the_23_expected_members()
    {
        // Includes implement-infrastructure (43-3 D5.1 — the assignment most
        // likely to be overruled) and the write-tests family (D5.2).
        // 19 → 23 (Story 41-1a): + design-system, plan-sprint, draft-user-flow,
        // author-ui-spec — binding artifacts others build/execute against (D5.3).
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
            "agent-action:design-system", "agent-action:plan-sprint",
            "agent-action:draft-user-flow", "agent-action:author-ui-spec",
        });
    }

    [Test]
    public void ReviewAndAcceptance_has_the_17_agent_plus_17_document_members()
    {
        // 16 → 17 agent members (Story 41-1a: + review-design); 10 → 16 document
        // members (Story 41-1b: + acceptance-criteria, backlog-ordering,
        // sprint-plan, test-plan, threat-model, ux-spec); 16 → 17 document
        // members (Story 41-1c: + prose).
        WiresIn(ActionGroup.ReviewAndAcceptance).Should().BeEquivalentTo(new[]
        {
            "agent-action:review-acceptance", "agent-action:review-scope", "agent-action:plan-review",
            "agent-action:code-review-architecture", "agent-action:code-review", "agent-action:mentor-feedback",
            "agent-action:self-review", "agent-action:review-feasibility", "agent-action:verify-acceptance",
            "agent-action:code-review-coverage", "agent-action:review-testability",
            "agent-action:plan-review-security", "agent-action:code-review-security",
            "agent-action:review-compliance", "agent-action:review-operability", "agent-action:review-docs",
            "agent-action:review-design",
            "document-type:findings", "document-type:ambiguity-assessment", "document-type:clarification",
            "document-type:decomposition", "document-type:plan", "document-type:design",
            "document-type:review", "document-type:triage-decision", "document-type:diagnosis",
            "document-type:test-spec",
            "document-type:acceptance-criteria", "document-type:backlog-ordering", "document-type:sprint-plan",
            "document-type:test-plan", "document-type:threat-model", "document-type:ux-spec",
            "document-type:prose",
        });
    }

    [Test]
    public void Docs_has_the_13_expected_members()
    {
        // 10 → 13 (Story 41-1a): + synthesize-standup, write-retro-narrative,
        // report-status — prose/digest writing about work already done.
        WiresIn(ActionGroup.Docs).Should().BeEquivalentTo(new[]
        {
            "agent-action:summarize-stakeholder", "agent-action:write-adr", "agent-action:summarize-technical",
            "agent-action:write-postmortem", "agent-action:summarize-changes", "agent-action:write-user-docs",
            "agent-action:write-api-docs", "agent-action:write-release-notes", "agent-action:write-runbook",
            "agent-action:update-changelog",
            "agent-action:synthesize-standup", "agent-action:write-retro-narrative", "agent-action:report-status",
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
    public void SourceControlWrite_has_the_10_expected_members()
    {
        // 6 -> 10 (Story 43-12): the coarse effect:git.pull-request.merge is RETIRED
        // and replaced by the per-target trio git.merge.{dev,qa,main} (+3 net +2);
        // plus the two RESERVED source-control-write keys git.checks.bypass (50) and
        // git.webhook.register (85, DUAL-dormant) (+2). Net 6 -> 10.
        WiresIn(ActionGroup.SourceControlWrite).Should().BeEquivalentTo(new[]
        {
            "tool:git_operations.write", "effect:git.branch.create", "effect:git.branch.delete",
            "effect:git.pull-request.create",
            "effect:git.merge.dev", "effect:git.merge.qa", "effect:git.merge.main",
            "effect:git.release.create",
            "effect:git.checks.bypass", "effect:git.webhook.register",
        });
    }

    [Test]
    public void IssueTracking_has_the_12_expected_members()
    {
        // 2 → 12 (Story 44-2): the NATIVE tracker's ten mutating routes join the
        // two EXTERNAL-tracker mutations. The group's partition rule is kind of
        // consequence AT COMPLETION, and each of these completes by changing
        // what the tracker says the work is — including the preferences pair,
        // which was deliberately NOT filed under platform-automation (that
        // would bury a tenant's default project in the same lever as the outbox
        // sweeper, invisible to anyone gating the tracker).
        WiresIn(ActionGroup.IssueTracking).Should().BeEquivalentTo(new[]
        {
            "effect:git.issue.patch", "effect:jira.ticket.patch",
            "effect:tracker.project.create", "effect:tracker.project.update",
            "effect:tracker.project.delete",
            "effect:tracker.work-item.create", "effect:tracker.work-item.update",
            "effect:tracker.work-item.delete", "effect:tracker.work-item.assign",
            "effect:tracker.work-item.set-status",
            "effect:tracker.preferences.set", "effect:tracker.preferences.delete",
        });
    }

    [Test]
    public void DeployControl_has_the_10_expected_members()
    {
        // implement-infrastructure is deliberately NOT here (43-3 D5.1).
        // 6 -> 10 (Story 43-12): the coarse effect:deploy.promote-prod is RETIRED and
        // replaced by the per-target quintet deploy.{dev,qa,uat,staging,prod}
        // (dev+staging RESERVED — no pipeline stage exists). Net 6 -> 10.
        WiresIn(ActionGroup.DeployControl).Should().BeEquivalentTo(new[]
        {
            "agent-action:plan-deployment", "agent-action:configure-cicd", "agent-action:deploy",
            "agent-action:rollback",
            "effect:deploy.dev", "effect:deploy.qa", "effect:deploy.uat",
            "effect:deploy.staging", "effect:deploy.prod",
            "effect:deploy.rollback",
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
    public void ModelInvocation_has_the_7_expected_members()
    {
        // 3 -> 7 (Story 43-8, 2026-07-30): the four mentorship-session lifecycle
        // effects. The 43-3 D1 partition rule is KIND OF CONSEQUENCE AT COMPLETION,
        // and each of these completes by leaving an autonomous, LLM-driven agent run
        // started / suspended / resumed / terminated — the same consequence as
        // effect:agent-dispatch.run, which already sits here. REJECTED alternative:
        // platform-automation "because it starts a workflow", which is HOUSEKEEPING
        // (engine mediation writes, sweepers, platform tasks) and would bury an
        // agent-run control in the same admin lever as the outbox sweeper.
        WiresIn(ActionGroup.ModelInvocation).Should().BeEquivalentTo(new[]
        {
            "effect:llm.call", "effect:mcp.tool.invoke", "effect:agent-dispatch.run",
            "effect:mentorship.session.start", "effect:mentorship.session.pause",
            "effect:mentorship.session.resume", "effect:mentorship.session.cancel",
        });
    }

    [Test]
    public void Secrets_has_the_5_expected_members()
    {
        // audit-secrets sits here, not planning-and-analysis: subject dominates
        // verb for this group (43-3 D5.4). 42-10 added effect:secret.read (level
        // 90) — the LLM value-read — alongside the machinery reveal.
        WiresIn(ActionGroup.Secrets).Should().BeEquivalentTo(new[]
        {
            "agent-action:audit-secrets", "effect:secret.reveal", "effect:secret.read",
            "automation:secret-auto-rotation-scheduler", "automation:retire-sweep",
        });
    }

    [Test]
    public void PlatformAutomation_has_the_43_expected_members()
    {
        var expected = new List<string>
        {
            "effect:engine.events.append", "effect:engine.platform-events.append",
            "effect:engine.document.persist", "effect:engine.document.set-status",
            "effect:engine.channel-outbox.enqueue",
            // 41-30 — the scheduled-trigger admin trio (platform plumbing, not a
            // work-product surface).
            "effect:schedule.create", "effect:schedule.update", "effect:schedule.delete",
        };
        // The 27 automation members outside the secrets group (41-30 added
        // TenantScheduledTriggerService; 43-4 added the catalog startup
        // validator; 43-5 added the governance snapshot primer — the
        // governance machinery is itself a swept hosted service).
        expected.AddRange(Enum.GetValues<BackgroundActor>()
            .Where(b => b is not (BackgroundActor.SecretAutoRotationScheduler or BackgroundActor.RetireSweep))
            .Select(b => $"automation:{b.ToWire()}"));
        // All 8 platform tasks.
        expected.AddRange(Enum.GetValues<PlatformTaskKind>().Select(p => $"platform-task:{p.ToWire()}"));

        expected.Should().HaveCount(43, "5 engine effects + 3 schedule effects + 27 automation + 8 platform tasks");
        WiresIn(ActionGroup.PlatformAutomation).Should().BeEquivalentTo(expected);
    }

    [Test]
    public void The_per_group_counts_sum_to_206()
    {
        // 205 → 206 (Story 42-10): +1 in secrets — effect:secret.read (level 90),
        // the LLM value-read alongside the machinery reveal; nothing else moves.
        // 197 → 205 (Story 43-12): source-control-write 6 → 10 (retire the coarse
        // merge, add the merge trio + checks.bypass + webhook.register) and
        // deploy-control 6 → 10 (retire promote-prod, add the deploy quintet) —
        // nothing else moves.
        // 193 → 197: +4 mentorship-session effects, ALL in model-invocation
        // (Story 43-8) — the group goes 3 → 7 and nothing else moves.
        // 183 → 193: +10 native-tracker effects, ALL in issue-tracking
        // (Story 44-2 AC10) — the group goes 2 → 12 and nothing else moves.
        // 154 → 176: +16 agent-actions (Story 41-1a) and +6 document types
        // (Story 41-1b); 176 → 180: +3 schedule effects and +1 scheduler
        // automation member (Story 41-30); 180 → 181: +1 automation member
        // (Story 43-4's catalog startup validator); 181 → 182: +1 document type
        // (Story 41-1c's prose); 182 → 183: +1 automation member (Story 43-5's
        // governance snapshot primer) — distributed per the membership tests
        // above.
        var counts = new Dictionary<ActionGroup, int>
        {
            [ActionGroup.PlanningAndAnalysis] = 37,
            [ActionGroup.Authoring] = 23,
            [ActionGroup.ReviewAndAcceptance] = 34,
            [ActionGroup.Docs] = 13,
            [ActionGroup.CodeRead] = 3,
            [ActionGroup.CodeWrite] = 1,
            [ActionGroup.CommandExecution] = 2,
            [ActionGroup.CiAndTest] = 3,
            [ActionGroup.SourceControlRead] = 1,
            [ActionGroup.SourceControlWrite] = 10,
            [ActionGroup.IssueTracking] = 12,
            [ActionGroup.DeployControl] = 10,
            [ActionGroup.ExternalComms] = 2,
            [ActionGroup.ModelInvocation] = 7,
            [ActionGroup.Secrets] = 5,
            [ActionGroup.PlatformAutomation] = 43,
        };

        counts.Values.Sum().Should().Be(206);
        foreach (var (group, count) in counts)
            ActionCatalog.ByGroup[group].Should().HaveCount(count, $"group '{group.ToWire()}'");
    }
}
