using System.Reflection;
using Elsa.Workflows;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-10 (AC2/AC3, Design Decision D5) — the RESUMABLE-BY-DESIGN build gate.
/// Enumerates every concrete <see cref="WorkflowBase"/> subclass in the
/// <c>Tamma.ElsaServer</c> assembly (the <c>TaxonomyDriftBuildTests</c> discovery
/// anchor) and fails — NAMING the workflow — unless it declares its resume behaviour
/// or is on the justified, ratchet-style <see cref="LegacyResumeAllowlist"/>.
///
/// <para>Clauses: (a) <c>[ResumeBehavior]</c> XOR an allowlist entry (stale entries
/// fail); (b) a <c>BookmarkSuspend</c>/<c>Both</c> workflow's built graph contains a
/// node whose type is in BOTH its declaration's <c>SuspendActivities</c> AND
/// <see cref="LifecycleBookmarks.CanonicalSuspendActivities"/> — and, inversely, a
/// canonical suspend node in an undeclared workflow fails (declaration honesty);
/// (c) a <c>LatestStateReEntry</c>/<c>Both</c> workflow's graph contains a
/// <see cref="ComputeReEntryPositionActivity"/> node (the descriptor wiring, not
/// hand-rolled guards). <see cref="DocumentLifecycleWorkflow"/> declares <c>Both</c>
/// from day one and is NEVER allowlisted — so AC2 is proven on a real workflow.</para>
///
/// <para>The allowlist is seeded with every current legacy workflow + a one-line
/// justification + the migration story that burns it down (39-12..39-15). Entries may
/// only be REMOVED — the day a workflow starts declaring, its stale entry fails the
/// build (the <c>KnownContractViolations</c> ratchet discipline).</para>
/// </summary>
[TestFixture]
public class ResumableStandardStructuralTests
{
    /// <summary>
    /// Every legacy workflow not yet migrated onto the resumable-by-design standard,
    /// with a justification + the burn-down story. RATCHET: entries may only be
    /// removed; a workflow that starts declaring <c>[ResumeBehavior]</c> makes its
    /// entry stale and fails the build until the entry is deleted.
    /// <see cref="DocumentLifecycleWorkflow"/> is deliberately ABSENT (it declares Both).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyResumeAllowlist =
        new Dictionary<string, string>
        {
            ["AdlOrchestratorWorkflow"] = "ADL orchestration composite; delegates to sub-workflows, no own lifecycle suspend/re-entry (burn-down: 39-13+).",
            ["AssessmentWorkflow"] = "assessment intake producer, no suspend gate yet (burn-down: 39-14+).",
            ["BlockerDiagnosisWorkflow"] = "diagnosis leaf, runs to completion (burn-down: 39-14+).",
            ["BranchCreationWorkflow"] = "git side-effect leaf, no suspend/re-entry (burn-down: 39-15+).",
            ["CiWithDebugRetryWorkflow"] = "CI retry loop, no human suspend gate (burn-down: 39-15+).",
            ["CleanUpFailedTenantWorkflow"] = "platform tenant-cleanup saga, no document lifecycle (burn-down: n/a — platform, revisit 39-15+).",
            ["CodeReviewWorkflow"] = "code-review leaf, runs to completion (burn-down: 39-14+).",
            ["ContextGatheringWorkflow"] = "context-scan producer, no suspend gate (burn-down: 39-12+).",
            ["CreateTenantWorkflow"] = "platform provisioning saga, no document lifecycle (burn-down: n/a — platform).",
            ["DebuggingWorkflow"] = "debug leaf, runs to completion (burn-down: 39-15+).",
            ["DeleteTenantWorkflow"] = "platform deprovisioning saga, no document lifecycle (burn-down: n/a — platform).",
            ["DeploymentPipelineWorkflow"] = "deploy pipeline, no document-decision suspend (burn-down: 39-15+).",
            ["DesignDeliveryWorkflow"] = "39-13 pre-ACCEPT delivery leaf (emit GENERATED/DELIVERED + deliver), runs to completion, no suspend/re-entry (burn-down: n/a — delivery leaf).",
            ["DocumentReviewWorkflow"] = "39-7 review producer sub-workflow, runs to completion (burn-down: 39-12+).",
            ["HourlyAnalyticsRollupWorkflow"] = "scheduled analytics rollup, no document lifecycle (burn-down: n/a — platform).",
            ["IssueTriageWorkflow"] = "triage orchestration composite, delegates to sub-workflows (burn-down: 39-14+).",
            ["LlmCallWorkflow"] = "the mediated llm-call leaf, runs to completion, holds no suspend gate (burn-down: n/a — infra leaf).",
            ["MentorshipWorkflow"] = "mentoring leaf, runs to completion (burn-down: 39-14+).",
            ["MergeApprovalWorkflow"] = "legacy merge-approval bookmark-suspend gate (not yet on the canonical builder; retired by a 39-13+ migration).",
            ["MergeWorkflow"] = "merge side-effect workflow, no document lifecycle (burn-down: 39-15+).",
            ["PanelReviewWorkflow"] = "39-7 panel-review router, delegates to reviewer sub-workflows (burn-down: 39-12+).",
            ["PlanGenerationWorkflow"] = "plan producer leaf, runs to completion (burn-down: 39-13+).",
            ["PlanReviewWorkflow"] = "plan-review panel, runs to completion (burn-down: 39-13+).",
            ["PullRequestWorkflow"] = "PR side-effect workflow, no suspend gate (burn-down: 39-15+).",
            ["ReviewFixWorkflow"] = "review-fix loop, no human suspend gate (burn-down: 39-14+).",
            ["RotateSecretWorkflow"] = "platform secret-rotation saga, no document lifecycle (burn-down: n/a — platform).",
            ["SingleIssueCycleWorkflow"] = "issue-cycle orchestration composite, delegates to sub-workflows (burn-down: 39-14+).",
            ["SingleReviewerWorkflow"] = "39-7 single-reviewer producer, runs to completion (burn-down: 39-12+).",
            ["TaskCreationWorkflow"] = "task producer leaf, runs to completion (burn-down: 39-13+).",
            ["TaskReviewWorkflow"] = "task-review panel, runs to completion (burn-down: 39-13+).",
            ["TddWithDebugRetryWorkflow"] = "TDD retry loop, no human suspend gate (burn-down: 39-15+).",
            ["TddWorkflow"] = "TDD cycle composite, delegates to sub-workflows (burn-down: 39-15+).",
            ["TestCaseCreationWorkflow"] = "test producer leaf, runs to completion (burn-down: 39-13+).",
            ["TestingWorkflow"] = "testing composite, delegates to sub-workflows (burn-down: 39-15+).",
            ["TriageContextGatheringWorkflow"] = "triage context-scan producer, no suspend gate (burn-down: 39-14+).",
            ["TriageItemCycleWorkflow"] = "triage item-cycle composite, delegates to sub-workflows (burn-down: 39-14+).",
            ["TriagePODecisionWorkflow"] = "triage PO-decision leaf, runs to completion (burn-down: 39-14+).",
            ["TriagePanelReviewWorkflow"] = "triage-review panel, runs to completion (burn-down: 39-14+).",
            ["UpdateIssueStatusWorkflow"] = "issue-status side-effect leaf, no suspend/re-entry (burn-down: 39-15+).",
        };

    private static IReadOnlyList<Type> ConcreteWorkflows() =>
        typeof(LlmCallWorkflow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(WorkflowBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

    private static ResumeBehaviorAttribute? Declaration(Type workflow)
        => workflow.GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);

    // ── (a) declare-or-allowlist, ratchet ──────────────────────────────

    [Test]
    public void EveryWorkflow_DeclaresResumeBehavior_XorIsAllowlisted()
    {
        var violations = new List<string>();

        foreach (var type in ConcreteWorkflows())
        {
            var declares = Declaration(type) is not null;
            var allowlisted = LegacyResumeAllowlist.ContainsKey(type.Name);

            if (declares && allowlisted)
                violations.Add(
                    $"  {type.Name}: declares [ResumeBehavior] AND is in LegacyResumeAllowlist — " +
                    "delete its allowlist entry (the ratchet only turns one way).");
            else if (!declares && !allowlisted)
                violations.Add(
                    $"  {type.Name}: neither declares [ResumeBehavior] nor is in LegacyResumeAllowlist — " +
                    "add a [ResumeBehavior(...)] declaration (the resumable-by-design standard) or " +
                    "allowlist it with a justification + burn-down story.");
        }

        violations.Should().BeEmpty(
            "every concrete workflow must EITHER declare [ResumeBehavior] or be justified-and-allowlisted " +
            "(exactly one):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void LegacyResumeAllowlist_HasNoStaleEntries()
    {
        var concreteNames = ConcreteWorkflows().Select(t => t.Name).ToHashSet();

        var orphaned = LegacyResumeAllowlist.Keys
            .Where(name => !concreteNames.Contains(name))
            .Select(name => $"  {name}: allowlisted but no such concrete workflow — delete the entry.")
            .ToList();

        var nowDeclares = ConcreteWorkflows()
            .Where(t => Declaration(t) is not null && LegacyResumeAllowlist.ContainsKey(t.Name))
            .Select(t => $"  {t.Name}: now declares [ResumeBehavior] — delete its stale allowlist entry.")
            .ToList();

        orphaned.Concat(nowDeclares).ToList().Should().BeEmpty(
            "LegacyResumeAllowlist entries may only be REMOVED; stale entries fail the build:" +
            Environment.NewLine + string.Join(Environment.NewLine, orphaned.Concat(nowDeclares)));

        foreach (var (_, reason) in LegacyResumeAllowlist)
            reason.Should().NotBeNullOrWhiteSpace("every allowlist entry must carry a justification");
    }

    // ── (b) BookmarkSuspend/Both ⇒ canonical suspend node present ───────

    [Test]
    public void EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode()
    {
        var violations = new List<string>();

        foreach (var type in ConcreteWorkflows())
        {
            var decl = Declaration(type);
            if (decl is null || decl.Mode is not (ResumeMode.BookmarkSuspend or ResumeMode.Both))
                continue;

            if (decl.SuspendActivities.Length == 0)
            {
                violations.Add(
                    $"  {type.Name}: declares {decl.Mode} but names NO SuspendActivities — a bookmark-suspend " +
                    "workflow must declare the canonical gate type(s) it registers.");
                continue;
            }

            var canonical = decl.SuspendActivities
                .Where(t => LifecycleBookmarks.CanonicalSuspendActivities.ContainsKey(t))
                .ToHashSet();
            if (canonical.Count == 0)
            {
                violations.Add(
                    $"  {type.Name}: its declared SuspendActivities are none of them in " +
                    "LifecycleBookmarks.CanonicalSuspendActivities (a non-canonical bookmark builder).");
                continue;
            }

            var nodeTypes = BuiltGraphNodeTypes(type);
            if (!nodeTypes.Any(canonical.Contains))
                violations.Add(
                    $"  {type.Name}: declares a canonical suspend but its built graph contains no node of " +
                    $"type {{{string.Join(", ", canonical.Select(t => t.Name))}}}.");
        }

        violations.Should().BeEmpty(
            "every BookmarkSuspend/Both workflow must actually register a canonical suspend activity:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── (b-inverse) declaration honesty ────────────────────────────────

    [Test]
    public void CanonicalSuspendNode_AppearsOnlyInDeclaredWorkflows()
    {
        var violations = new List<string>();

        foreach (var type in ConcreteWorkflows())
        {
            var canonicalNodes = BuiltGraphNodeTypes(type)
                .Where(LifecycleBookmarks.CanonicalSuspendActivities.ContainsKey)
                .ToHashSet();
            if (canonicalNodes.Count == 0)
                continue;

            var decl = Declaration(type);
            if (decl is null || decl.Mode is not (ResumeMode.BookmarkSuspend or ResumeMode.Both))
            {
                violations.Add(
                    $"  {type.Name}: its graph contains canonical suspend node(s) " +
                    $"{{{string.Join(", ", canonicalNodes.Select(t => t.Name))}}} but it does not declare " +
                    "BookmarkSuspend/Both — declaration honesty violated.");
                continue;
            }

            var undeclared = canonicalNodes.Where(t => !decl.SuspendActivities.Contains(t)).ToList();
            if (undeclared.Count > 0)
                violations.Add(
                    $"  {type.Name}: registers canonical suspend node(s) " +
                    $"{{{string.Join(", ", undeclared.Select(t => t.Name))}}} not listed in its declaration's " +
                    "SuspendActivities.");
        }

        violations.Should().BeEmpty(
            "a canonical suspend activity may appear only in a workflow that DECLARES it (honesty):" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── (c) LatestStateReEntry/Both ⇒ ComputeReEntryPositionActivity node ──

    [Test]
    public void EveryReEntryWorkflow_HasAComputeReEntryNode()
    {
        var violations = new List<string>();

        foreach (var type in ConcreteWorkflows())
        {
            var decl = Declaration(type);
            if (decl is null || decl.Mode is not (ResumeMode.LatestStateReEntry or ResumeMode.Both))
                continue;

            var nodeTypes = BuiltGraphNodeTypes(type);
            if (!nodeTypes.Contains(typeof(ComputeReEntryPositionActivity)))
                violations.Add(
                    $"  {type.Name}: declares {decl.Mode} (crash re-entry) but its built graph contains no " +
                    "ComputeReEntryPositionActivity node (the generic re-entry wiring, D6).");
        }

        violations.Should().BeEmpty(
            "every LatestStateReEntry/Both workflow must wire the generic ComputeReEntryPositionActivity:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── AC2 proven on a real workflow ──────────────────────────────────

    [Test]
    public void DocumentLifecycleWorkflow_DeclaresBoth_AndIsNotAllowlisted()
    {
        LegacyResumeAllowlist.Should().NotContainKey(nameof(DocumentLifecycleWorkflow),
            "the lifecycle declares its resume behaviour from day one — it is never allowlisted");

        var decl = Declaration(typeof(DocumentLifecycleWorkflow));
        decl.Should().NotBeNull("DocumentLifecycleWorkflow must carry a [ResumeBehavior] declaration (AC2)");
        decl!.Mode.Should().Be(ResumeMode.Both,
            "the lifecycle both suspends on the accept-gate bookmark AND re-enters after a crash");
        decl.SuspendActivities.Should().Contain(typeof(WaitForDocumentDecisionActivity),
            "its bookmark-suspend gate is the canonical WaitForDocumentDecisionActivity");
    }

    // ── graph walk (shared with DocumentLifecycleWorkflowStructureTests style) ──

    private static HashSet<Type> BuiltGraphNodeTypes(Type workflowType)
    {
        var instance = (WorkflowBase)Activator.CreateInstance(workflowType)!;
        var builder = WorkflowTestHelper.BuildWorkflow(instance);
        var root = builder.Object.Root;

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var types = new HashSet<Type>();
        var stack = new Stack<IActivity>();
        if (root is not null) stack.Push(root);

        while (stack.Count > 0)
        {
            var a = stack.Pop();
            if (a is null || !seen.Add(a)) continue;
            types.Add(a.GetType());
            foreach (var child in Children(a)) stack.Push(child);
        }
        return types;
    }

    private static IEnumerable<IActivity> Children(IActivity activity)
    {
        var type = activity.GetType();
        var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member switch
                {
                    PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.GetValue(activity),
                    FieldInfo f => f.GetValue(activity),
                    _ => null,
                };
            }
            catch { continue; }

            if (value is IActivity child) yield return child;
            else if (value is System.Collections.IEnumerable en and not string)
                foreach (var item in en) if (item is IActivity nested) yield return nested;
        }
    }
}
