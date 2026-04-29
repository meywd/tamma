using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-1 §6 — abstract base class that 31-3..31-6 driver
/// stories will subclass to share happy-path contract tests across
/// every driver. Concrete fixtures (one per driver) provide a wired
/// driver via <see cref="CreateDriver"/>; the base methods exercise
/// the interface in a platform-neutral way.
///
/// <para>31-1 ships ONE concrete subclass —
/// <see cref="NullDriverContractTests"/> — to verify the base class
/// itself works against the null seam. Real-driver subclasses come
/// later.</para>
/// </summary>
public abstract class GitPlatformClientContractTests
{
    /// <summary>
    /// Implementer hook — return a fully wired driver bound to the
    /// fixture's installation context.
    /// </summary>
    protected abstract IGitPlatformDriver CreateDriver();

    [Test]
    public void Driver_kind_matches_expected()
    {
        var driver = CreateDriver();
        Enum.IsDefined(driver.Kind).Should().BeTrue();
    }

    [Test]
    public void Capabilities_are_a_subset_of_kind_defaults()
    {
        // Effective capabilities may NARROW the matrix defaults
        // (e.g. PAT-mode driver dropping PerAppInstallationAuth) but
        // MUST NOT add capabilities outside the matrix.
        var driver = CreateDriver();
        var defaults = PlatformKindCapabilityMatrix.DefaultsFor(driver.Kind);
        driver.Capabilities.Should().BeSubsetOf(defaults);
    }

    [Test]
    public void Actions_presence_matches_capability()
    {
        var driver = CreateDriver();
        var hasActions = driver.Capabilities.Contains(PlatformCapability.Actions);
        if (hasActions)
        {
            driver.Actions.Should().NotBeNull(
                "driver advertises Actions capability so Actions surface must be wired");
        }
        // Drivers without Actions cap MAY still expose Actions (e.g. a
        // partially-wired driver) — we don't enforce the inverse.
    }

    [Test]
    public async Task GetRepo_returns_a_PlatformResult()
    {
        var driver = CreateDriver();
        var result = await driver.Client.GetRepoAsync("owner", "repo");
        result.Should().NotBeNull();
        // Concrete drivers add assertions about the actual value.
    }
}

/// <summary>
/// Story 31-1: contract base validated against the null seam.
/// Real drivers (31-3 GitHub, 31-4 Gitea, 31-5 Forgejo, 31-6 GitLab)
/// will add their own subclass.
/// </summary>
[TestFixture]
public sealed class NullDriverContractTests : GitPlatformClientContractTests
{
    protected override IGitPlatformDriver CreateDriver() =>
        new NullGitPlatformDriver { Kind = PlatformKind.GitHub };
}
