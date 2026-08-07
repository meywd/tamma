using NUnit.Framework;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Story 31-10 — GitLab integration test stub. The driver is in-flight
/// in Story 31-6; this file is a placeholder so the project compiles
/// and the test discovery picks up the trait once the driver lands.
///
/// <para>Future implementation (per plan §step-3):</para>
/// <list type="bullet">
///   <item>Boot <c>gitlab/gitlab-ce:latest</c> (heavy ~3 GB image).
///         Pin a specific tag once 31-6 settles a baseline.</item>
///   <item>Healthcheck poll <c>GET /-/readiness</c>; timeout 10 min
///         (cold boot is slow even on a beefy runner).</item>
///   <item>Use <c>gitlab-rails runner</c> exec to seed root password,
///         then mint admin PAT, bot user + project-access-token, +
///         fixture project with a sample <c>.gitlab-ci.yml</c>.</item>
///   <item>CI: gated to <c>workflow_dispatch</c> and the nightly
///         schedule, NOT run on every PR.</item>
/// </list>
///
/// <para>Tag this fixture with <c>Category("Nightly")</c> when wired so
/// the per-PR job filter <c>"Category!=Nightly"</c> doesn't pick it up.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("GitLab")]
[Category("Nightly")]
public class GitLabIntegrationTests
{
    [Test]
    [Ignore("GitLab driver shipped (31-6); this suite stays a stub until the " +
            "Epic 31 EXECUTION-PLAN P6 nightly harness populates and un-ignores it.")]
    public void GitLab_DriverNotYetShipped()
    {
        // Body intentionally empty — will be populated when 31-6
        // lands.
    }
}
