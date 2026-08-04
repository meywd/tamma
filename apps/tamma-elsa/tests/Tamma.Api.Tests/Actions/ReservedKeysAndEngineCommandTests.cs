using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-12 AC5/AC6 — the reserved rows report zero enforcement sites (they are
/// declarative: real catalog rows with no live seam), and the deleted
/// <c>POST /api/engine/command</c> route is gone from the host.
/// </summary>
[TestFixture]
public class ReservedKeysAndEngineCommandTests
{
    private static IActionEnforcementSites Sites =>
        GovernanceHostFixture.Services.GetRequiredService<IActionEnforcementSites>();

    private static ActionKey Effect(string wire) => new(ActionNamespace.Effect, wire);

    /// <summary>The four RESERVED keys (AC5): a real catalog row, no performer.</summary>
    private static readonly string[] ReservedWires =
    [
        "deploy.dev", "deploy.staging", "git.checks.bypass", "git.webhook.register",
    ];

    [Test]
    public void ReservedKeys_AreCatalogued_ButEnforcedNowhere()
    {
        foreach (var wire in ReservedWires)
        {
            // The row exists (a real catalog descriptor at its zone level)...
            ActionCatalog.ByKey.Should().ContainKey(Effect(wire), $"effect:{wire} is a real catalog row");
            // ...and reports an EMPTY enforcementSites array — AC5's "declarative"
            // state the policy view renders as "not enforced anywhere yet".
            Sites.For(Effect(wire)).Should().BeEmpty(
                $"effect:{wire} is RESERVED — no route binds it and no [PerformsEffect] method performs it");
        }
    }

    [Test]
    public void MergeKeys_AreEnforced_TheContrastToTheReservedRows()
    {
        // The discrimination: the reserved rows are empty because they are reserved,
        // not because enforcementSites is broken. The per-target merge keys, bound on
        // the merge route, DO report a site.
        foreach (var wire in new[] { "git.merge.dev", "git.merge.qa", "git.merge.main" })
            Sites.For(Effect(wire)).Should().NotBeEmpty(
                $"effect:{wire} binds the merge route (multi-binding) and MergePullRequestAsync");
    }

    [Test]
    public void EngineCommandRoute_IsGone()
    {
        GovernanceHostFixture.Endpoints
            .Should().NotContain(f => f.SiteKey == "POST /api/engine/command",
                "Story 43-12 deleted the POST /api/engine/command route (a 200 'accepted' no-op)");

        ActionCatalog.All.Should().NotContain(d => d.Key.ToWire().Contains("engine.command"),
            "no catalog key was minted for the deleted engine-command stub");
    }
}
