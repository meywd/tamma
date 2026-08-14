using System.Diagnostics;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Story 31-10 — skip-when-no-docker gate. Mirrors the wave-A
/// chromadb.integration.test.ts pattern: probe the docker daemon ONCE
/// at type init, cache the result, and skip every test in the harness
/// if the daemon is unreachable.
///
/// <para>Rationale: developers without docker (laptops on a flight,
/// tightly-locked corp images) MUST NOT see permanent CI red just
/// because they ran <c>dotnet test</c> locally. CI envs always have
/// docker, so the skip is invisible there.</para>
///
/// <para>On CI, the env var <c>PLATFORMS_REQUIRE_DOCKER=true</c>
/// converts the skip into a failure — that way an accidentally-broken
/// daemon doesn't quietly skip the entire harness in CI.</para>
/// </summary>
internal static class DockerAvailability
{
    /// <summary>
    /// True if <c>docker info</c> returned exit code 0 within 5s at
    /// type init. Cached for the test run lifetime.
    /// </summary>
    public static bool IsAvailable { get; }

    /// <summary>
    /// Reason string surfaced in skip messages — empty when
    /// <see cref="IsAvailable"/> is true.
    /// </summary>
    public static string SkipReason { get; }

    /// <summary>
    /// True when CI has set <c>PLATFORMS_REQUIRE_DOCKER=true</c>. In
    /// that mode an unreachable daemon converts the harness's skip
    /// into a hard failure — a CI runner that lost docker is a bug,
    /// not a "skip silently" condition.
    /// </summary>
    public static bool RequireDocker =>
        string.Equals(
            Environment.GetEnvironmentVariable("PLATFORMS_REQUIRE_DOCKER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    static DockerAvailability()
    {
        // 2026-08-14: one 5s probe made the per-PR job flaky — a COLD CI runner's
        // first `docker info` regularly exceeds 5s while the daemon warms, and the
        // whole fixture then failed OneTimeSetUp (23 tests lost to one
        // infrastructure hiccup, PR #512). Where docker is REQUIRED (CI sets
        // PLATFORMS_REQUIRE_DOCKER=true) retry within a 60s budget; on a laptop
        // keep the single fast probe so a misconfigured machine never stalls test
        // discovery.
        var deadline = DateTime.UtcNow + (RequireDocker ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
        while (!Probe() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// One <c>docker info</c> attempt; sets <see cref="IsAvailable"/> and
    /// <see cref="SkipReason"/>, and returns whether docker answered.
    /// </summary>
    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                IsAvailable = false;
                SkipReason = "docker: failed to start `docker info` process";
                return false;
            }
            // 5s — docker info on a healthy daemon is sub-second; on a
            // broken one it sits forever. We don't want to block test
            // discovery for minutes on a misconfigured laptop.
            if (!proc.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                IsAvailable = false;
                SkipReason = "docker: `docker info` timed out after 5s";
                return false;
            }
            if (proc.ExitCode != 0)
            {
                IsAvailable = false;
                SkipReason = $"docker: `docker info` exited {proc.ExitCode}";
                return false;
            }
            IsAvailable = true;
            SkipReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            SkipReason = $"docker: probe threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Call from <c>[OneTimeSetUp]</c>. Skips the fixture if docker is
    /// unavailable (or fails it under
    /// <see cref="RequireDocker"/>). NUnit's <see cref="Assert.Ignore"/>
    /// short-circuits all tests in the fixture.
    /// </summary>
    public static void RequireOrSkip()
    {
        if (IsAvailable) return;
        if (RequireDocker)
        {
            throw new InvalidOperationException(
                $"PLATFORMS_REQUIRE_DOCKER=true but docker is unavailable: {SkipReason}");
        }
        // NUnit ignore — surfaces in test output, doesn't fail the run.
        NUnit.Framework.Assert.Ignore(
            $"Skipped: docker is required for platform integration tests. {SkipReason}");
    }
}
