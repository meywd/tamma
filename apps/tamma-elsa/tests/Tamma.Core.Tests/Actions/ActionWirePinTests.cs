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
            "git.pull-request.merge", "git.release.create", "git.issue.patch", "jira.ticket.patch",
            "ci.tests.trigger", "agent-dispatch.run", "notify.slack.queue", "notify.email.send",
            "mcp.tool.invoke", "secret.reveal", "process.spawn", "deploy.promote-prod", "deploy.rollback",
            // 41-30 — the scheduled-trigger admin surface (tree-truth reconcile).
            "schedule.create", "schedule.update", "schedule.delete");
    }

    [Test]
    public void BackgroundActor_wires_are_pinned()
    {
        Enum.GetValues<BackgroundActor>().Select(b => b.ToWire()).Should().Equal(
            "hourly-analytics-rollup-scheduler", "tenant-cleanup-requested-trigger",
            "tenant-delete-requested-trigger", "workflow-seeder", "agent-seeder",
            // 41-30 — the tenant-aware scheduler seam (tree-truth reconcile).
            "tenant-scheduled-trigger-service",
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
            "platform-task-worker");
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
