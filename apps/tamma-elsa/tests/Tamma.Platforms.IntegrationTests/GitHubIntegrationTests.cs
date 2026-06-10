using NUnit.Framework;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Story 31-10 — GitHub integration test stub. GitHub doesn't ship a
/// Docker image, so the harness pattern here will be different from
/// the Gitea/GitLab/Forgejo containers:
///
/// <list type="bullet">
///   <item>Recorded-cassette pattern (WireMock.Net or VCR.NET style):
///         record real GitHub API responses once, replay them on every
///         test run.</item>
///   <item>OR a dedicated GitHub test org owned by Tamma with a bot
///         account + per-repo cleanup. Heavier setup, but exercises
///         real API drift the same way the container fixtures do.</item>
/// </list>
///
/// <para>Decision deferred to a follow-up story — Story 31-3 ships the
/// GitHub driver refactor with WireMock unit tests that already cover
/// the response-shape contract. The integration test value-add for
/// GitHub is "auth-token lifecycle quirks + webhook delivery retries"
/// — both of which are easier to exercise against a recorded cassette
/// than a live test org.</para>
///
/// <para>This stub keeps the file present so test discovery and CI
/// gating recognize the GitHub category from day one.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("GitHub")]
public class GitHubIntegrationTests
{
    [Test]
    [Ignore("GitHub integration harness pending: pick recorded-cassette " +
            "vs live-test-org pattern in a follow-up story.")]
    public void GitHub_HarnessNotYetWired()
    {
        // Body intentionally empty — will be populated once the
        // harness pattern is decided.
    }
}
