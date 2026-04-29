using NUnit.Framework;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Story 31-10 — Forgejo integration test stub. The Forgejo driver
/// (Story 31-5) ships as a thin shim over the Gitea driver; once 31-5
/// authors its <c>ForgejoContainerFixture</c> the harness wires it in
/// here. This file is a placeholder so the project compiles and the
/// test discovery picks up the trait once 31-5 lands.
///
/// <para>Future implementation (per plan §step-2):</para>
/// <list type="bullet">
///   <item>Boot <c>codeberg.org/forgejo/forgejo:15-rootless</c>.
///         Add Docker Hub mirror <c>forgejoclone/forgejo</c> as
///         fallback for image pulls when codeberg.org is rate-limited.</item>
///   <item>Same seed flow as Gitea (Forgejo's REST is wire-compatible).</item>
///   <item>Re-uses the same <c>ContractTestSuite</c> the Gitea fixture
///         drives — only the container differs.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("Forgejo")]
public class ForgejoIntegrationTests
{
    [Test]
    [Ignore("Story 31-5 Forgejo driver not yet shipped — harness stub only.")]
    public void Forgejo_DriverNotYetShipped()
    {
        // Body intentionally empty — will be populated when 31-5
        // lands its driver + fixture.
    }
}
