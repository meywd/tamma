using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-4 AC5 + AC8 (partition half) — the two shrink-only ratchets, with
/// the COUNT PIN the <c>ContractBindingTests</c> idiom lacks: growing either
/// list fails here, so an allowlist can never quietly absorb a real drift, and
/// a stale entry (its justification no longer true) fails its staleness check.
/// </summary>
[TestFixture]
public class ToolCatalogAllowlistTests
{
    /// <summary>
    /// The <c>NotDiRegisteredTools</c> pin's recorded high-water history, oldest
    /// first; strictly decreasing (asserted by
    /// <see cref="TheRatchetPin_IsMechanicallyShrinkOnly"/>). Adopted 2026-07-30 for
    /// all four of Story 43-8's ratchets, from
    /// <c>TemplateExampleConformanceTests</c>. Seeded at 1 (Story 43-4) and unmoved.
    /// </summary>
    internal static readonly int[] NotDiRegisteredToolsPinHistory = [1];

    [Test]
    public void TheRatchetPin_IsMechanicallyShrinkOnly()
    {
        // A bare literal inside HaveCount makes "shrink-only" prose an author defeats
        // by editing one character — the ContractBindingTests.cs:255-271 defect that
        // Story 43-8 D7 exists to not inherit. Bind the pin to a recorded history and
        // assert the history's DIRECTION.
        NotDiRegisteredToolsPinHistory.Should().NotBeEmpty();

        for (var i = 1; i < NotDiRegisteredToolsPinHistory.Length; i++)
        {
            NotDiRegisteredToolsPinHistory[i].Should()
                .BeLessThan(NotDiRegisteredToolsPinHistory[i - 1],
                    $"pin history entry #{i} must be strictly smaller than #{i - 1}. A tool leaves "
                    + "this allowlist by GAINING a DI-registered executor, never by the allowlist "
                    + "growing to fit a new exemption.");
        }
    }

    /// <summary>
    /// The justification classifier for <c>ToolCatalogAllowlists</c>, extracted
    /// 2026-07-30 so that both <see cref="Every_justification_is_non_empty_and_cites_a_source"/>
    /// and the AC8 meta-test <c>RatchetDisciplineTests</c> drive the SAME rule
    /// rather than two copies that can drift.
    /// </summary>
    internal static bool CitesASource(string justification) =>
        !string.IsNullOrWhiteSpace(justification)
        && System.Text.RegularExpressions.Regex.IsMatch(
            justification, "Program\\.cs|ToolCallValidator|D6");

    [Test]
    public void NotDiRegisteredTools_is_pinned_at_exactly_one_entry()
    {
        // SHRINK-ONLY: deleting an entry (because the tool became registered or
        // was deleted) is fine; ADDING one requires updating this pin in the
        // same reviewed change, with a justification citing a design decision.
        ToolCatalogAllowlists.NotDiRegisteredTools
            .Should().HaveCount(NotDiRegisteredToolsPinHistory[^1]);
        ToolCatalogAllowlists.NotDiRegisteredTools[0].Key.Should().Be("tool:get_acceptance_rules");
    }

    [Test]
    public void The_single_not_di_registered_entry_is_not_stale()
    {
        // STALENESS: the entry claims get_acceptance_rules has no DI-registered
        // executor. If the six-executor composition ever gains it, this fails
        // and the line must be DELETED (Story 39-5 D6 would also have been
        // revoked — that is a reviewed decision, not a drive-by).
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var diRegisteredNames = new IToolExecutor[]
        {
            new FileReadTool(NullLogger<FileReadTool>.Instance, config),
            new FileWriteTool(NullLogger<FileWriteTool>.Instance, config),
            new SearchCodeTool(NullLogger<SearchCodeTool>.Instance, config),
            new ShellExecuteTool(NullLogger<ShellExecuteTool>.Instance, config),
            new GitOperationsTool(NullLogger<GitOperationsTool>.Instance, config),
            new RunTestsTool(NullLogger<RunTestsTool>.Instance, config),
        }.Select(e => e.ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        diRegisteredNames.Should().NotContain("get_acceptance_rules",
            "the allowlist entry has gone STALE — get_acceptance_rules is now DI-registered; "
            + "delete the ToolCatalogAllowlists.NotDiRegisteredTools line");
    }

    [Test]
    public void KnownDefensiveAliases_is_pinned_at_exactly_twelve_entries()
    {
        ToolCatalogAllowlists.KnownDefensiveAliases.Should().HaveCount(12);
        ToolCatalogAllowlists.KnownDefensiveAliases.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            "execute_shell_command", "run_command", "shell", "exec", "bash", "terminal",
            "run_shell", "execute_command", "system_command", "run_code", "execute", "cmd",
        });
    }

    [Test]
    public void No_defensive_alias_names_a_real_executor()
    {
        // STALENESS for the defensive list: if a future story adds a real
        // executor named e.g. "exec", the defensive entry stops being defensive
        // and must be reconsidered — it would now be an execution surface.
        var executorNames = ActionCatalogStartupValidator.ValidatorInputs
            .LiveExecutorImplementations()
            .Select(t => (string)typeof(IToolExecutor).GetProperty(nameof(IToolExecutor.ToolName))!
                .GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t))!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ToolCatalogAllowlists.KnownDefensiveAliases)
        {
            executorNames.Should().NotContain(entry.Key,
                $"defensive alias '{entry.Key}' now names a REAL executor — it is no longer a "
                + "defensive-only name; remove the entry and catalogue the tool");
        }
    }

    [Test]
    public void Every_justification_is_non_empty_and_cites_a_source()
    {
        foreach (var entry in ToolCatalogAllowlists.NotDiRegisteredTools
                     .Concat(ToolCatalogAllowlists.KnownDefensiveAliases))
        {
            entry.Justification.Should().NotBeNullOrWhiteSpace(
                $"allowlist entry '{entry.Key}' must say WHY it is allowed");
            // The ContractBindingTests keyword-classification shape: every
            // justification names the file/decision it rests on. Delegated to
            // CitesASource so the AC8 meta-test drives the identical rule.
            CitesASource(entry.Justification).Should().BeTrue(
                $"allowlist entry '{entry.Key}' must cite its source");
        }
    }

    [Test]
    public void ShellToolNames_AreAllResolvableOrJustified()
    {
        // AC8's partition: the validator's third check must have no leftovers —
        // all 13 ShellToolNames members are covered by resolution ∪ the
        // justified defensive list (overlap allowed: 'bash' resolves AND is
        // recorded defensively — see the D5 note on ToolCatalogAllowlists).
        var defensive = ToolCatalogAllowlists.KnownDefensiveAliases
            .Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ToolCallValidator.KnownShellToolNames)
        {
            var resolves = ToolNameAliases.TryResolve(name, out var key)
                           && ActionCatalog.TryGet(key, out _);
            (resolves || defensive.Contains(name)).Should().BeTrue(
                $"ShellToolNames member '{name}' must resolve through the aliases or carry a "
                + "justified defensive entry");
        }
    }
}
