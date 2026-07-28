using System.Runtime.CompilerServices;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// REFLECTION SWEEP binding the catalog's <c>tool:*</c> plane to the real
/// <see cref="IToolExecutor"/> implementations (Story 43-2 AC12's real-world
/// counterpart for the tool plane; the Core-side keyset test is self-referential
/// and says so). THE SWEEP IS THE SOURCE OF TRUTH: a new executor class fails
/// this test until it is catalogued, and a catalogued tool with no executor
/// fails it until the entry is deleted — bidirectional, per the epic's drift
/// rule ("the list is read from code").
/// </summary>
[TestFixture]
public class ToolExecutorCatalogSweepTests
{
    // ⚠ META-GUARD — THE ASSEMBLY LIST BELOW IS THE SWEEP'S BLIND SPOT. A
    // reflection sweep only binds the catalog to code it actually scans: an
    // IToolExecutor declared in an assembly missing from this list is invisible
    // here and ships uncatalogued. The list must cover EVERY production
    // assembly — today the three are Tamma.Activities, Tamma.Api and
    // Tamma.ElsaServer (kept in lockstep with BackgroundActorCatalogSweepTests,
    // and pinned by The_swept_assemblies_are_the_three_production_assemblies) —
    // and MUST GROW the day a fourth production assembly is added to
    // Tamma.sln, even if it declares no executors yet.
    private static IReadOnlyList<System.Reflection.Assembly> SweptAssemblies() =>
        new[]
        {
            typeof(GitOperationsTool).Assembly,                                    // Tamma.Activities
            typeof(Tamma.Api.Services.AcceptanceRules.GetAcceptanceRulesTool).Assembly, // Tamma.Api (declares the deliberately-unregistered GetAcceptanceRulesTool)
            typeof(Tamma.ElsaServer.WorkflowSeeder).Assembly,                      // Tamma.ElsaServer (declares none today — swept so one added there cannot ship uncatalogued)
        }
        .Distinct()
        .ToArray();

    /// <summary>
    /// Every non-abstract <see cref="IToolExecutor"/> implementation across the
    /// production assemblies (see the meta-guard note on
    /// <see cref="SweptAssemblies"/>).
    /// </summary>
    private static IReadOnlyList<Type> ExecutorTypes() =>
        SweptAssemblies()
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IToolExecutor).IsAssignableFrom(t))
        .ToArray();

    [Test]
    public void The_swept_assemblies_are_the_three_production_assemblies()
    {
        // Meta-assertion for the sweep's own blind spot (see the note above):
        // if this fails because a production assembly was added or renamed,
        // grow the list here AND in BackgroundActorCatalogSweepTests — never
        // shrink a sweep to make it pass.
        SweptAssemblies().Select(a => a.GetName().Name)
            .Should().BeEquivalentTo(new[] { "Tamma.Activities", "Tamma.Api", "Tamma.ElsaServer" });
    }

    /// <summary>
    /// Reads <c>ToolName</c> without running a constructor: every executor's
    /// <c>ToolName</c> is an expression-bodied constant, so an uninitialized
    /// instance answers correctly and the sweep needs no DI graph.
    /// </summary>
    private static string ToolNameOf(Type executor) =>
        (string)typeof(IToolExecutor).GetProperty(nameof(IToolExecutor.ToolName))!
            .GetValue(RuntimeHelpers.GetUninitializedObject(executor))!;

    [Test]
    public void Every_executor_and_every_catalogued_tool_match_bidirectionally()
    {
        var executorNames = ExecutorTypes().Select(ToolNameOf).ToArray();

        // The catalog's tool keys, with the one deliberate argument-bound split
        // collapsed: git_operations.read/write are both performed by the single
        // git_operations executor (43-2 AC8 — the only such split in the epic).
        var catalogued = ActionCatalog.ByKey.Keys
            .Where(k => k.Ns == ActionNamespace.Tool)
            .Select(k => k.Key.StartsWith("git_operations.", StringComparison.Ordinal) ? "git_operations" : k.Key)
            .Distinct()
            .ToArray();

        executorNames.Should().OnlyHaveUniqueItems("two executors sharing a ToolName would be un-addressable");
        catalogued.Should().BeEquivalentTo(executorNames,
            "the catalog is derived from the code: a new IToolExecutor must be catalogued, "
            + "and a catalogued tool whose executor was deleted must be removed");
    }

    [Test]
    public void The_executor_count_is_pinned_at_7()
    {
        // 6 DI-registered (Program.cs AddSingleton<IToolExecutor, …>) + the
        // deliberately-unregistered GetAcceptanceRulesTool (Story 39-5 D6:
        // principal-bound instances minted per session — a singleton registration
        // would be the bug; Story 43-4's startup validator allowlists it).
        ExecutorTypes().Should().HaveCount(7);
    }
}
