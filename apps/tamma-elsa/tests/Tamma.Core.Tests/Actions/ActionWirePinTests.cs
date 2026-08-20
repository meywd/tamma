using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Exact wire pins for the vocabularies Story 43-2 authors (AC4–AC8). Renaming a
/// wire is a deliberate, reviewed diff — several of these strings become
/// persisted vocabulary (platform-task wires already ARE persisted task types;
/// group wires persist at 43-5).
/// </summary>
[TestFixture]
public class ActionWirePinTests
{
    [Test]
    public void ActionNamespace_wires_are_pinned()
    {
        Enum.GetValues<ActionNamespace>().Select(n => n.ToWire()).Should().Equal(
            "agent-action", "document-type", "tool", "effect", "automation", "platform-task");
    }

    [Test]
    public void ActionRisk_wires_are_pinned()
    {
        Enum.GetValues<ActionRisk>().Select(r => r.ToWire()).Should().Equal(
            "read-only", "mutating", "command", "destructive");
    }

    [Test]
    public void ToolAction_wires_are_pinned()
    {
        Enum.GetValues<ToolAction>().Select(t => t.ToWire()).Should().Equal(
            "file_read", "file_write", "search_code", "shell_execute", "run_tests",
            "get_acceptance_rules", "git_operations.read", "git_operations.write");
    }

    [Test]
    public void ExternalEffect_wires_are_pinned()
    {
        Enum.GetValues<ExternalEffect>().Select(e => e.ToWire()).Should().Equal(
            "engine.events.append", "engine.platform-events.append", "engine.document.persist",
            "engine.document.set-status", "engine.channel-outbox.enqueue",
            "llm.call", "git.branch.create", "git.branch.delete", "git.pull-request.create",
            // 43-12 — the coarse git.pull-request.merge is RETIRED; merge splits by PR
            // base branch into the zone-ladder trio (dev 55 / qa 60 / main 65).
            "git.merge.dev", "git.merge.qa", "git.merge.main",
            "git.release.create",
            // 43-12 — RESERVED source-control-write keys (no performer in the tree):
            // git.checks.bypass (50) and git.webhook.register (85, DUAL-dormant).
            "git.checks.bypass", "git.webhook.register",
            // 31-13 — the 7 PR operation verbs (source-control-write), declared
            // between git.webhook.register and git.issue.patch; enum-order-sensitive.
            "git.pull-request.close", "git.pull-request.reopen", "git.pull-request.comment",
            "git.pull-request.review-comment", "git.pull-request.request-reviewers",
            "git.pull-request.label", "git.pull-request.set-draft",
            "git.issue.patch", "jira.ticket.patch",
            // 31-13 — the 4 formerly-ungoverned issue callbacks (issue-tracking),
            // declared immediately after jira.ticket.patch.
            "git.issue.create", "git.issue.comment", "git.issue.labels.set", "git.issue.labels.remove",
            "ci.tests.trigger", "agent-dispatch.run", "notify.slack.queue", "notify.email.send",
            // 42-10 — secret.read (level 90) is declared adjacent to secret.reveal;
            // this list is enum-order-sensitive, so it sits between reveal and spawn.
            "mcp.tool.invoke", "secret.reveal", "secret.read", "process.spawn",
            // 43-12 — the coarse deploy.promote-prod is RETIRED; deploy splits by
            // target env (dev 70 / qa 75 / uat 80 / staging 85 / prod 90). dev+staging
            // are RESERVED (no pipeline stage exists — QA->UAT->Prod only).
            "deploy.dev", "deploy.qa", "deploy.uat", "deploy.staging", "deploy.prod",
            "deploy.rollback",
            // 41-30 — the scheduled-trigger admin surface (tree-truth reconcile).
            "schedule.create", "schedule.update", "schedule.delete",
            // 44-2 — the NATIVE tracker's ten mutating routes. `tracker.` prefixed
            // so they never read as the EXTERNAL-tracker pair above
            // (git.issue.patch / jira.ticket.patch), which mutate somebody else's
            // system of record.
            "tracker.project.create", "tracker.project.update", "tracker.project.delete",
            "tracker.work-item.create", "tracker.work-item.update", "tracker.work-item.delete",
            "tracker.work-item.assign", "tracker.work-item.set-status",
            "tracker.preferences.set", "tracker.preferences.delete",
            // 43-8 (AC1 step 2) — MentorshipController's four [HttpPost] actions, the
            // repo's only attribute-routed controller and the only users of the
            // [Governs] attribute shape. `mentorship.session.` prefixed so they read
            // as one lifecycle family.
            "mentorship.session.start", "mentorship.session.pause",
            "mentorship.session.resume", "mentorship.session.cancel",
            // Story 43-17 follow-up — appended at the END of the enum so the
            // existing order is untouched.
            "ci.workflow.dispatch", "llm.task.execute");
    }

    [Test]
    public void BackgroundActor_wires_are_pinned()
    {
        Enum.GetValues<BackgroundActor>().Select(b => b.ToWire()).Should().Equal(
            "hourly-analytics-rollup-scheduler", "tenant-cleanup-requested-trigger",
            "tenant-delete-requested-trigger", "workflow-seeder", "agent-seeder",
            // 41-30 — the tenant-aware scheduler seam (tree-truth reconcile).
            "tenant-scheduled-trigger-service",
            // 2026-08-18 — the autonomous-loop watchdog and the orphaned-cycle
            // sweeper, declared with the other Tamma.ElsaServer hosted services.
            // Order is pinned but not persisted: the wire converter serializes by
            // the [Wire] string, so a mid-enum insert does not renumber stored data.
            "adl-loop-watchdog-service", "orphaned-cycle-recovery-service",
            "pool-warmup-service", "workflow-sync-service", "channel-outbox-sweeper",
            "secret-auto-rotation-scheduler", "retire-sweep", "engine-registry-heartbeat-service",
            "tenant-status-invalidation-listener", "provider-settings-store-priming-service",
            "entitlement-cache-invalidation-listener",
            "convention-store-seeder", "provider-session-cleanup-service", "task-queue-processor",
            "outbox-slack-sender", "outbox-smtp-sender", "audit-chain-checkpoint-scheduler",
            "reveal-token-sweeper", "notification-dispatcher", "built-in-alert-rule-seeder",
            "alert-rule-evaluator", "audit-projector",
            // 43-4 — the boot-time tool-vocabulary check (itself an IHostedService,
            // so the hosted-service sweep demands it be catalogued).
            "action-catalog-startup-validator",
            // 43-5 — the policy-snapshot cold-start primer (the same rule:
            // every IHostedService class is catalogued, governance included).
            "governance-policy-snapshot-priming-service",
            "platform-task-worker",
            // Epic 31 P2 — the platform-plane subscriber + the seam-14 backfill
            // (same rule: every IHostedService class is catalogued).
            "platform-driver-cache-invalidator",
            "github-installation-bridge-backfill",
            // Epic 31 P3 (2026-08-08, DG-5) — the CI completion poller: the
            // durable resumer for suspended CI-result waits.
            "ci-completion-poller",
            // Epic 31 P4 M3 (2026-08-08) — the single-user startup webhook
            // registration pass (git.webhook.register's config-tier caller).
            "webhook-registration-startup");
    }

    [Test]
    public void PlatformTaskKind_wires_are_pinned()
    {
        // These are the EXISTING persisted task-type strings; byte-parity with the
        // real Tamma.Api constants is asserted by PlatformTaskCatalogSweepTests
        // (Tamma.Activities.Tests), which can reference that assembly.
        Enum.GetValues<PlatformTaskKind>().Select(p => p.ToWire()).Should().Equal(
            "RETIRE_SECRET_VERSION", "plan.activate_scheduled", "tenant.move",
            "provisioning.tenant", "provisioning.tenant.v2", "provisioning.tenant.deprovision",
            "billing.webhook.followup", "billing.customer.create");
    }

    [Test]
    public void GitSubcommand_wires_are_pinned()
    {
        Enum.GetValues<GitSubcommand>().Select(g => g.ToWire()).Should().Equal(
            "status", "diff", "log", "add", "commit", "push", "branch", "checkout",
            "stash", "show", "fetch", "pull", "rev-parse", "ls-files");
    }

    [Test]
    public void ActionGroup_wires_are_pinned()
    {
        Enum.GetValues<ActionGroup>().Select(g => g.ToWire()).Should().Equal(
            "planning-and-analysis", "authoring", "review-and-acceptance", "docs",
            "code-read", "code-write", "command-execution", "ci-and-test",
            "source-control-read", "source-control-write", "issue-tracking",
            "deploy-control", "external-comms", "model-invocation", "secrets",
            "platform-automation");
    }
}
