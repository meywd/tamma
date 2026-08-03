using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Controllers;
using Tamma.Api.Models;
using Tamma.Api.Services;
using Tamma.Core.Entities;
using Tamma.Core.Interfaces;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 40-8 (AC4, D8) — the DANGLING-DISPATCH class guard.
///
/// <para>A <c>DispatchWorkflow.WorkflowDefinitionId</c> is an unchecked magic string:
/// a dispatch to a nonexistent definition compiles, seeds, and runs — and with
/// <c>WaitForCompletion = true</c> the parent suspends FOREVER on a completion that can
/// never arrive (the exact defect of
/// <c>.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md</c>:
/// <c>SingleIssueCycleWorkflow</c>'s Defer/Split branches dispatched
/// <c>"create-issues"</c> before any workflow declared that id). This fixture closes
/// the class for <c>Tamma.ElsaServer/Workflows/</c>: every literal dispatched
/// definition id must match a DECLARED workflow <c>DefinitionId</c>.</para>
///
/// <para>Layout:
/// (1) the resolution sweep — every literal dispatch target resolves;
/// (2) a named, shrink-only allowlist for the only delegate-valued (dynamic) dispatch
///     sites (<c>DocumentLifecycleWorkflow</c>'s variable-backed reviewer/delivery ids,
///     39-7 D10), with staleness checks in both directions;
/// (3) anti-no-op floors so a silently-broken extractor/walk cannot pass;
/// (4) the out-of-directory second instance (<c>MentorshipController.cs</c>'s
///     <c>"tamma-autonomous-mentorship"</c> vs the real <c>"mentorship"</c>) pinned by
///     CAPTURE against the same declared set — allowlisted, not source-scanned, per the
///     story's "out-of-directory; do not silently widen scope" instruction.</para>
/// </summary>
[TestFixture]
public class DispatchTargetStructuralTests
{
    // ── Allowlists ──────────────────────────────────────────────────────────

    /// <summary>
    /// The only dispatch sites whose <c>WorkflowDefinitionId</c> is NOT a literal —
    /// both read a workflow variable (39-7 D10: the lifecycle picks its reviewer /
    /// delivery producer at runtime). Keyed (workflow, activity id); shrink-only:
    /// an entry whose site vanishes or becomes literal fails until deleted, and a NEW
    /// dynamic site fails until justified here.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Workflow, string ActivityId), string> DynamicDispatchAllowlist =
        new Dictionary<(string, string), string>
        {
            [("DocumentLifecycleWorkflow", "DispatchReview")] =
                "39-7 D10 — reviewer producer definition id is variable-backed (reviewDefId), resolved per document type at runtime.",
            [("DocumentLifecycleWorkflow", "DispatchDelivery")] =
                "39-7 D10 — delivery producer definition id is variable-backed (deliveryDefId), resolved per document type at runtime.",
        };

    /// <summary>
    /// Out-of-directory dispatch sites pinned by CAPTURE whose id is KNOWN not to
    /// resolve — each entry is a live bug awaiting its owner's fix. Shrink-only: the
    /// day the site dispatches a declared id, its entry here goes stale and fails
    /// until deleted (see <see cref="KnownMismatchAllowlist_HasNoStaleEntries"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownControllerDispatchMismatches =
        new Dictionary<string, string>
        {
            ["tamma-autonomous-mentorship"] =
                "MentorshipController.StartMentorship dispatches \"tamma-autonomous-mentorship\" but " +
                "MentorshipWorkflow declares \"mentorship\" — the second live instance of the dangling-dispatch " +
                "bug (epic-41 README :592-597). It fails LOUD at runtime (ElsaWorkflowService.StartWorkflowAsync " +
                "throws on the 404) rather than silently suspending, and the one-word fix lives in Tamma.Api — " +
                "owned by the Api/Actions lane, out of Story 40-8's file fence (story AC4 explicitly allows " +
                "allowlist-with-reason for this out-of-directory site). Delete this entry when the controller " +
                "passes \"mentorship\".",
        };

    // Anti-no-op floors (D8). Read from the tree on 2026-08-03: the sweep saw
    // ~80+ literal dispatch sites across ~29 distinct definition ids. Pinned just
    // below the observed values so a broken extractor or graph walk (sites
    // collapsing toward zero) fails loudly instead of green-washing the
    // resolution sweep.
    private const int MinLiteralDispatchSites = 75;
    private const int MinDistinctDispatchedIds = 25;

    // ── Discovery ───────────────────────────────────────────────────────────

    private sealed record DispatchSite(string Workflow, string ActivityId, string? LiteralId)
    {
        public bool IsLiteral => LiteralId is not null;
    }

    private static IReadOnlyList<Type> ConcreteWorkflows() =>
        typeof(LlmCallWorkflow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(WorkflowBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

    /// <summary>Every DECLARED workflow definition id in the ElsaServer assembly
    /// (instantiate every concrete <see cref="WorkflowBase"/>, build via the mock
    /// builder, read <c>builder.DefinitionId</c>).</summary>
    private static HashSet<string> DeclaredDefinitionIds()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ConcreteWorkflows())
        {
            var instance = (WorkflowBase)Activator.CreateInstance(type)!;
            var builder = WorkflowTestHelper.BuildWorkflow(instance);
            var id = builder.Object.DefinitionId;
            if (!string.IsNullOrEmpty(id))
                declared.Add(id);
        }
        return declared;
    }

    /// <summary>
    /// Every <see cref="DispatchWorkflow"/> node in every built workflow graph, via
    /// the DEEP stack walk (the <c>ResumableStandardStructuralTests</c> pattern — the
    /// shallow one-level Sequence expansion misses nested dispatches).
    /// </summary>
    private static List<DispatchSite> AllDispatchSites()
    {
        var sites = new List<DispatchSite>();
        foreach (var type in ConcreteWorkflows())
        {
            var instance = (WorkflowBase)Activator.CreateInstance(type)!;
            var builder = WorkflowTestHelper.BuildWorkflow(instance);
            var root = builder.Object.Root;
            if (root is null) continue;

            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<IActivity>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var activity = stack.Pop();
                if (activity is null || !seen.Add(activity)) continue;

                if (activity is DispatchWorkflow dispatch)
                    sites.Add(new DispatchSite(
                        type.Name, dispatch.Id ?? "<no-id>", ReadLiteralDefinitionId(dispatch)));

                foreach (var child in Children(activity))
                    stack.Push(child);
            }
        }
        return sites;
    }

    /// <summary>
    /// The literal string a dispatch targets, or <c>null</c> for a delegate-valued
    /// (dynamic) site. A literal <c>new("id")</c> carries the string on
    /// <c>Expression.Value</c>; a <c>new(ctx => …)</c> carries the delegate there.
    /// </summary>
    private static string? ReadLiteralDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expression?.Value as string;
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

    // ── (1) The resolution sweep — the bug's reproduction, inverted ─────────

    [Test]
    public void EveryDispatchedDefinitionId_ResolvesToADeclaredWorkflow()
    {
        var declared = DeclaredDefinitionIds();
        var unresolved = AllDispatchSites()
            .Where(s => s.IsLiteral && !declared.Contains(s.LiteralId!))
            .OrderBy(s => s.Workflow).ThenBy(s => s.ActivityId)
            .Select(s => $"  {s.Workflow}/{s.ActivityId} → \"{s.LiteralId}\"")
            .ToList();

        unresolved.Should().BeEmpty(
            "every literal WorkflowDefinitionId dispatched from Tamma.ElsaServer/Workflows/ must match a " +
            "declared workflow DefinitionId — a dispatch to a nonexistent definition with " +
            "WaitForCompletion=true suspends the parent FOREVER (the create-issues defer/split hang, " +
            ".dev/bugs/2026-08-02). Unresolved dispatch sites:" +
            Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    // ── (2) Dynamic-site allowlist, staleness both ways ─────────────────────

    [Test]
    public void DynamicDispatchAllowlist_HasNoStaleEntries_AndNoUnlistedDynamicSites()
    {
        var dynamicSites = AllDispatchSites()
            .Where(s => !s.IsLiteral)
            .Select(s => (s.Workflow, s.ActivityId))
            .ToHashSet();
        var listed = DynamicDispatchAllowlist.Keys.ToHashSet();

        var unlisted = dynamicSites.Except(listed)
            .Select(k => $"  {k.Workflow}/{k.ActivityId}: delegate-valued WorkflowDefinitionId not on the " +
                         "allowlist — justify it here or make the id a literal.")
            .ToList();
        var stale = listed.Except(dynamicSites)
            .Select(k => $"  {k.Workflow}/{k.ActivityId}: allowlisted but no such dynamic dispatch site " +
                         "exists any more — delete the entry (shrink-only ratchet).")
            .ToList();

        unlisted.Concat(stale).ToList().Should().BeEmpty(
            "the dynamic-dispatch allowlist must exactly track the delegate-valued sites:" +
            Environment.NewLine + string.Join(Environment.NewLine, unlisted.Concat(stale)));

        foreach (var (_, reason) in DynamicDispatchAllowlist)
            reason.Should().NotBeNullOrWhiteSpace("every allowlist entry must carry a justification");
    }

    // ── (3) Anti-no-op floors ───────────────────────────────────────────────

    [Test]
    public void Sweep_SeesTheDispatchSurface()
    {
        var sites = AllDispatchSites();
        var literalSites = sites.Where(s => s.IsLiteral).ToList();
        var distinctIds = literalSites.Select(s => s.LiteralId!).Distinct().ToList();

        literalSites.Count.Should().BeGreaterThanOrEqualTo(MinLiteralDispatchSites,
            $"the sweep saw only {literalSites.Count} literal dispatch sites (floor {MinLiteralDispatchSites}) " +
            "— the extractor or the graph walk has silently broken, which would let the resolution sweep " +
            "pass by extracting nothing");
        distinctIds.Count.Should().BeGreaterThanOrEqualTo(MinDistinctDispatchedIds,
            $"the sweep saw only {distinctIds.Count} distinct dispatched definition ids " +
            $"(floor {MinDistinctDispatchedIds}) — the extractor has silently broken");
    }

    // ── (4) The out-of-directory second instance, pinned by capture ─────────

    /// <summary>Drive <c>MentorshipController.StartMentorship</c> with mocked services
    /// and capture the definition id it hands to <c>IElsaWorkflowService</c>.</summary>
    private static async Task<string?> CaptureMentorshipDispatchIdAsync()
    {
        string? captured = null;

        var elsa = new Mock<IElsaWorkflowService>();
        elsa.Setup(s => s.StartWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
            .Callback<string, Dictionary<string, object>>((name, _) => captured = name)
            .ReturnsAsync("instance-1");

        var mentorship = new Mock<IMentorshipService>();
        mentorship.Setup(s => s.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new MentorshipSession { Id = Guid.NewGuid() });
        mentorship.Setup(s => s.UpdateSessionWorkflowAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var controller = new MentorshipController(
            mentorship.Object, elsa.Object, NullLogger<MentorshipController>.Instance);

        await controller.StartMentorship(new StartMentorshipRequest { StoryId = "story-1", JuniorId = "junior-1" });
        return captured;
    }

    [Test]
    public async Task MentorshipController_DispatchesADeclaredDefinitionId_OrIsKnownMismatch()
    {
        var captured = await CaptureMentorshipDispatchIdAsync();
        captured.Should().NotBeNullOrEmpty("StartMentorship must reach the workflow dispatch");

        var declared = DeclaredDefinitionIds();
        (declared.Contains(captured!) || KnownControllerDispatchMismatches.ContainsKey(captured!))
            .Should().BeTrue(
                $"MentorshipController dispatches \"{captured}\", which neither matches a declared workflow " +
                "DefinitionId nor is a documented known mismatch — the dangling-dispatch bug, out-of-directory " +
                "flavour. Fix the controller literal or record the mismatch with its owner.");
    }

    [Test]
    public async Task KnownMismatchAllowlist_HasNoStaleEntries()
    {
        // Shrink-only ratchet: every recorded mismatch must still BE the captured,
        // unresolvable id. The day the controller fix lands ("mentorship"), the entry
        // is stale and fails here until deleted — and may never mask a declared id.
        var captured = await CaptureMentorshipDispatchIdAsync();
        var declared = DeclaredDefinitionIds();

        var stale = KnownControllerDispatchMismatches.Keys
            .Where(id => id != captured || declared.Contains(id))
            .Select(id => $"  \"{id}\": no capture-pinned site dispatches this unresolvable id any more — " +
                          "delete the entry (shrink-only ratchet).")
            .ToList();

        stale.Should().BeEmpty(
            "KnownControllerDispatchMismatches entries may only be REMOVED (each one is a live bug " +
            "awaiting its owner's fix):" + Environment.NewLine + string.Join(Environment.NewLine, stale));

        foreach (var (_, reason) in KnownControllerDispatchMismatches)
            reason.Should().NotBeNullOrWhiteSpace("every known-mismatch entry must carry a justification");
    }
}
