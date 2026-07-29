using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-8 (AC6) — REFLECTION SWEEP over every concrete <see cref="IActivity"/>
/// class in the production assemblies, asserting each carries
/// <c>[Activity]</c> or sits in the shrink-only
/// <see cref="UnattributedActivities"/> baseline.
///
/// <para><b>Why this is the first mechanism that can see them.</b> An activity
/// without <c>[Activity]</c> still runs — Elsa executes it fine when a workflow
/// graph references the type directly — but it never appears in the activity
/// registry, so it is absent from Elsa Studio's palette, from every registry-driven
/// inventory, and from any governance surface derived from the registry. The 13
/// <c>SecretsRotation/Activities/</c> steps are exactly that: real, executing,
/// secret-mutating activities that no registry-based inventory has ever listed.</para>
///
/// <para><b>Implemented as a test, not a Roslyn analyzer</b> (43-8 D1). The property
/// is reflection-visible, so a test is strictly cheaper and equally complete. The
/// accepted cost is that the failure lands in CI rather than in a local build.</para>
///
/// <para><b>WHAT THIS SWEEP CANNOT SEE</b> — stated so a reader of a PASSING run is
/// not misled:</para>
/// <list type="bullet">
///   <item>The assembly list below is the whole blind spot. An activity declared in
///   an assembly not in <see cref="SweptAssemblies"/> is invisible; the list is
///   meta-pinned by <see cref="The_swept_assemblies_are_the_production_assemblies"/>
///   so a fourth production assembly cannot arrive unnoticed.</item>
///   <item>Carrying <c>[Activity]</c> says nothing about whether the activity is
///   GOVERNED — it means the registry can see it. Registry visibility is a
///   precondition for governance, not governance.</item>
///   <item>Nothing here checks the attribute's arguments (category, display name);
///   a mis-categorised activity passes.</item>
/// </list>
/// </summary>
[TestFixture]
public class UnattributedActivitySweepTests
{
    // ⚠ META-GUARD — kept in lockstep with BackgroundActorCatalogSweepTests and
    // ToolExecutorCatalogSweepTests. Never shrink this list to make a test pass.
    private static IReadOnlyList<Assembly> SweptAssemblies() =>
        new[]
        {
            typeof(Tamma.Activities.LlmCall.Tools.GitOperationsTool).Assembly, // Tamma.Activities
            typeof(Tamma.Api.Services.PlatformTasks.PlatformTaskWorker).Assembly, // Tamma.Api
            typeof(Tamma.ElsaServer.WorkflowSeeder).Assembly, // Tamma.ElsaServer
        }
        .Distinct()
        .ToArray();

    // ====================================================================
    // The baseline — a RATCHET: shrink-only, staleness-checked, count-pinned
    // ====================================================================

    /// <summary>
    /// One baselined activity class: the full type name and why it ships without
    /// <c>[Activity]</c> today. Entries may only ever be REMOVED — adding one
    /// breaks <see cref="Baseline_countIsPinned"/>, which is the whole point
    /// (<c>ContractBindingTests.cs:255-271</c> documents shrink-only as prose with
    /// no assertion behind it; that defect is not inherited here).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UnattributedActivities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // SEEDED 2026-07-29 from the sweep's own output. All nine are steps of
            // the Story 29-6 secret-rotation saga: RotateSecretWorkflow.cs:110
            // constructs a single RotateSecretSagaActivity IN CODE, and that saga
            // composes the other eight itself. None is ever picked from Elsa
            // Studio's palette, which is why none was ever given [Activity].
            //
            // NOTE on the count: Story 43-8 AC6 says "13 files in
            // SecretsRotation/Activities/". Thirteen FILES, nine ACTIVITY CLASSES —
            // the other four (RotationActivityBase (abstract), DrainRotationAuditEmitter,
            // RotationAuditDrainScope, RotationWorkflowState) are not IActivity
            // implementations. The sweep counts classes, which is the thing that
            // can be attributed.
            ["Tamma.Activities.SecretsRotation.Activities.RotateSecretSagaActivity"] =
                "saga step: the rotation saga root, constructed in code by RotateSecretWorkflow.cs:110; "
                + "not registry-visible and never palette-authored",
            ["Tamma.Activities.SecretsRotation.Activities.MintPendingVersionActivity"] =
                "internal step of the rotation saga (mint pending version); composed by the saga root, "
                + "not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.PushNewValueActivity"] =
                "internal step of the rotation saga (push new value); composed by the saga root, "
                + "not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.ProbeActivity"] =
                "internal step of the rotation saga (probe the push landed); composed by the saga root, "
                + "not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.ActivateNewVersionActivity"] =
                "internal step of the rotation saga (activate the new version); composed by the saga "
                + "root, not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.ScheduleRetireOldActivity"] =
                "internal step of the rotation saga (schedule retirement of the old version); composed "
                + "by the saga root, not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.RollbackPushActivity"] =
                "internal step of the rotation saga (compensation branch); composed by the saga root, "
                + "not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.DeleteVersionActivity"] =
                "internal step of the rotation saga (compensation branch); composed by the saga root, "
                + "not registry-visible",
            ["Tamma.Activities.SecretsRotation.Activities.ResolveHandlerActivity"] =
                "internal step of the rotation saga (resolve the rotation handler); composed by the "
                + "saga root, not registry-visible",
        };

    /// <summary>
    /// Keyword classifier (the <c>ContractBindingTests.UniversalPin_*</c> idiom): a
    /// baseline justification must read as one of these classes, so
    /// "TODO" / "legacy" / "" cannot buy an entry into the ratchet.
    /// </summary>
    private static readonly string[] JustificationKeywords =
    [
        "saga step",            // an internal step of a code-composed saga, never palette-authored
        "internal step",
        "base helper",          // a shared base/helper that is not itself a workflow node
        "test scaffold",
        "not registry-visible", // explicit: the author knows and accepted it
    ];

    // ====================================================================
    // Discovery
    // ====================================================================

    /// <summary>Every concrete activity class across the swept assemblies.</summary>
    public static IReadOnlyList<Type> ActivityTypes() =>
        SweptAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                        && typeof(IActivity).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>
    /// THE SWEEP, as a pure function over its inputs, so the negative tests below
    /// can drive the REAL classifier with synthetic input instead of asserting on a
    /// re-implementation of it.
    /// </summary>
    /// <param name="types">Candidate activity classes.</param>
    /// <param name="baseline">The shrink-only allowlist.</param>
    /// <returns>(violations, staleEntries).</returns>
    internal static (List<string> Violations, List<string> Stale) Classify(
        IReadOnlyList<Type> types,
        IReadOnlyDictionary<string, string> baseline)
    {
        var violations = new List<string>();

        // inherit: false — each concrete activity must declare its OWN [Activity].
        // With inherit:true a single attributed base class would silently vouch for
        // every subclass, which is the failure mode this sweep exists to catch.
        var unattributed = types
            .Where(t => t.GetCustomAttribute<ActivityAttribute>(inherit: false) is null)
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        violations.AddRange(unattributed
            .Where(name => !baseline.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
                $"  {name}: implements IActivity but carries no [Activity] attribute, so the Elsa " +
                "activity registry cannot see it. Add [Activity(category, displayName, description)] " +
                "— or, if it is deliberately registry-invisible, add a justified entry to " +
                "UnattributedActivities AND bump Baseline_countIsPinned in the same commit."));

        var stale = baseline.Keys
            .Where(name => !unattributed.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name => types.Any(t => t.FullName == name)
                ? $"  {name}: now carries [Activity] — DELETE its UnattributedActivities entry " +
                  "(the ratchet only turns one way)."
                : $"  {name}: no such activity class exists any more — DELETE its " +
                  "UnattributedActivities entry.")
            .ToList();

        return (violations, stale);
    }

    // ====================================================================
    // The sweep against reality
    // ====================================================================

    [Test]
    public void The_swept_assemblies_are_the_production_assemblies()
    {
        SweptAssemblies().Select(a => a.GetName().Name)
            .Should().BeEquivalentTo(new[] { "Tamma.Activities", "Tamma.Api", "Tamma.ElsaServer" });
    }

    [Test]
    public void The_sweep_actually_sees_activities()
    {
        // ANTI-NO-OP TRIPWIRE. If the discovery ever returns nothing (an assembly
        // reference dropped, Elsa's IActivity moved namespace), every other
        // assertion in this fixture would pass vacuously. Fail loudly instead.
        ActivityTypes().Should().HaveCountGreaterThan(50,
            "the repo ships well over 50 Elsa activity classes; an empty or tiny sweep means the "
            + "discovery broke, not that the codebase changed");
    }

    [Test]
    public void EveryActivityClass_CarriesTheAttribute_OrIsBaselined()
    {
        var (violations, stale) = Classify(ActivityTypes(), UnattributedActivities);

        violations.Should().BeEmpty(
            "every concrete IActivity class must be visible to the Elsa activity registry:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));

        stale.Should().BeEmpty(
            "UnattributedActivities must list ONLY classes that still lack [Activity]:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void Baseline_countIsPinned()
    {
        // (c) of the three ratchet properties — WITHOUT this, an ADDITION to the
        // baseline is undetectable and the ratchet is decorative.
        UnattributedActivities.Should().HaveCount(
            9,
            "9 activity CLASSES in SecretsRotation/Activities/ (AC6's '13 files' counts four non-activity "
            + "files too — see the seeding note on UnattributedActivities). "
            + "The ungoverned backlog may only SHRINK. If this fails because you added an entry, that "
            + "is the ratchet working: the new activity should carry [Activity] instead.");
    }

    [Test]
    public void Baseline_justificationsAreClassified()
    {
        // (b) of the three — a non-empty string is not a justification; it must read
        // as one of the recognised classes.
        var unclassified = UnattributedActivities
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value)
                         || !JustificationKeywords.Any(k => kv.Value.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"  {kv.Key}: {kv.Value}")
            .ToList();

        unclassified.Should().BeEmpty(
            "every UnattributedActivities justification must classify as one of ["
            + string.Join(", ", JustificationKeywords) + "]:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — the sweep must FAIL on ungoverned input
    // ====================================================================

    /// <summary>
    /// A deliberately unattributed activity, declared in the TEST assembly so it can
    /// never reach production. It is the input that proves the sweep is not a no-op.
    /// </summary>
    private sealed class UngovernedFixtureActivity : CodeActivity
    {
        protected override void Execute(ActivityExecutionContext context) { }
    }

    /// <summary>An attributed one, to prove the classifier does not just fail everything.</summary>
    [Activity("Tamma.Tests", "Governed Fixture", "discrimination-proof fixture")]
    private sealed class GovernedFixtureActivity : CodeActivity
    {
        protected override void Execute(ActivityExecutionContext context) { }
    }

    [Test]
    public void Discrimination_anUnattributedActivityIsReported()
    {
        var (violations, _) = Classify(
            [typeof(UngovernedFixtureActivity)],
            new Dictionary<string, string>(StringComparer.Ordinal));

        violations.Should().ContainSingle()
            .Which.Should().Contain(nameof(UngovernedFixtureActivity),
                "feeding the REAL classifier an unattributed activity must produce a violation — "
                + "if this passes, the sweep is inert and its green runs mean nothing");
    }

    [Test]
    public void Discrimination_anAttributedActivityIsNotReported()
    {
        var (violations, _) = Classify(
            [typeof(GovernedFixtureActivity)],
            new Dictionary<string, string>(StringComparer.Ordinal));

        violations.Should().BeEmpty("an attributed activity is governed and must not be flagged");
    }

    [Test]
    public void Discrimination_aStaleBaselineEntryIsReported()
    {
        var (_, stale) = Classify(
            [typeof(GovernedFixtureActivity)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [typeof(GovernedFixtureActivity).FullName!] = "saga step",
            });

        stale.Should().ContainSingle()
            .Which.Should().Contain("DELETE",
                "a baselined class that now carries [Activity] must fail as STALE, so the baseline drains");
    }

    [Test]
    public void Discrimination_aBaselinedActivityIsSuppressed()
    {
        var (violations, stale) = Classify(
            [typeof(UngovernedFixtureActivity)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [typeof(UngovernedFixtureActivity).FullName!] = "test scaffold",
            });

        violations.Should().BeEmpty();
        stale.Should().BeEmpty();
    }
}
