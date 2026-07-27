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
    /// <summary>
    /// Every non-abstract <see cref="IToolExecutor"/> implementation across the
    /// assemblies that declare them (Tamma.Activities + Tamma.Api — the
    /// deliberately-unregistered GetAcceptanceRulesTool lives in Tamma.Api).
    /// </summary>
    private static IReadOnlyList<Type> ExecutorTypes() =>
        new[]
        {
            typeof(GitOperationsTool).Assembly,                                    // Tamma.Activities
            typeof(Tamma.Api.Services.AcceptanceRules.GetAcceptanceRulesTool).Assembly, // Tamma.Api
        }
        .Distinct()
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IToolExecutor).IsAssignableFrom(t))
        .ToArray();

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
