using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-12 AC2 — the two coarse keys are RETIRED. The grep-over-src clause is
/// enforced by review; this pins the catalog half mechanically: no
/// <see cref="ExternalEffect"/> wire and no catalog descriptor equals
/// <c>git.pull-request.merge</c> or <c>deploy.promote-prod</c> any more.
/// </summary>
[TestFixture]
public class RetiredCoarseKeysTests
{
    private static readonly string[] RetiredWires =
    [
        "git.pull-request.merge",
        "deploy.promote-prod",
    ];

    [Test]
    public void RetiredWires_AreGoneFromTheEffectPlane()
    {
        var effectWires = Enum.GetValues<ExternalEffect>().Select(e => e.ToWire()).ToHashSet();
        foreach (var wire in RetiredWires)
            effectWires.Should().NotContain(wire,
                $"'{wire}' is a coarse key Story 43-12 retired — the per-target keys replace it");
    }

    [Test]
    public void RetiredWires_HaveNoCatalogDescriptor()
    {
        foreach (var wire in RetiredWires)
            ActionCatalog.ByKey.Keys.Should().NotContain(new ActionKey(ActionNamespace.Effect, wire),
                $"'effect:{wire}' must have no descriptor after Story 43-12 retired it");
    }

    [Test]
    public void PerTargetKeys_ReplacedTheCoarseOnes()
    {
        // The replacements exist, so retirement did not just delete capability.
        string[] minted =
        [
            "git.merge.dev", "git.merge.qa", "git.merge.main",
            "deploy.dev", "deploy.qa", "deploy.uat", "deploy.staging", "deploy.prod",
            "git.checks.bypass", "git.webhook.register",
        ];
        foreach (var wire in minted)
            ActionCatalog.ByKey.Should().ContainKey(new ActionKey(ActionNamespace.Effect, wire),
                $"Story 43-12 mints 'effect:{wire}'");
    }
}
