using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services;

namespace Tamma.Api.Tests.Monitoring;

/// <summary>
/// Story 23-8 — pins the lightweight infra-metrics contract for
/// <see cref="InfrastructureMetricsService"/>:
///   • the live process/runtime/memory/disk snapshot is sane (positive core
///     count, non-negative uptime, CPU% in [0,100], memory limit resolved);
///   • dependency status is composed from the admin health probes; a DOWN probe
///     is surfaced (not thrown); and
///   • the LEAK DEFENCE — a raw probe detail that embeds a host / user / password
///     is allowlist-sanitized so it never reaches the serialized response.
/// The PlatformOwnerAccess 403 gate is pinned separately by
/// <c>PlatformOwnerAccessPolicyTests</c> (which lists the route).
/// </summary>
[TestFixture]
public class InfrastructureMetricsServiceTests
{
    /// <summary>A stand-in that returns a scripted set of probe results.</summary>
    private sealed class FakeAdminHealthService : IAdminHealthService
    {
        private readonly AdminHealthResponse _response;
        public FakeAdminHealthService(AdminHealthResponse response) => _response = response;
        public Task<AdminHealthResponse> GetHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(_response);
    }

    private static InfrastructureMetricsService NewService(params ServiceCheck[] services)
    {
        var response = new AdminHealthResponse(services, DateTime.UtcNow.ToString("o"));
        return new InfrastructureMetricsService(new FakeAdminHealthService(response));
    }

    [Test]
    public async Task GetMetricsAsync_ReturnsSaneLiveSnapshot()
    {
        var svc = NewService(
            new ServiceCheck("Tamma API", "healthy", 0, DateTime.UtcNow.ToString("o")));

        var result = await svc.GetMetricsAsync();

        // Runtime tier — always populated from the live process.
        result.Runtime.ProcessorCount.Should().BeGreaterThan(0);
        result.Runtime.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        result.Runtime.CpuUsagePercent.Should().BeInRange(0, 100);
        result.Runtime.FrameworkDescription.Should().NotBeNullOrEmpty();
        result.Runtime.StartedAt.Should().NotBeNullOrEmpty();

        // Process tier — thread + GC counters.
        result.Process.ThreadCount.Should().BeGreaterThanOrEqualTo(0);
        result.Process.Gen0Collections.Should().BeGreaterThanOrEqualTo(0);

        // Memory tier — working set + a resolved limit (cgroup or GC view).
        result.Memory.WorkingSetBytes.Should().BeGreaterThan(0);
        result.Memory.MemoryLimitBytes.Should().BeGreaterThanOrEqualTo(0);
        result.Memory.MemoryUsagePercent.Should().BeInRange(0, 100);
        result.Memory.MemoryLimitSource.Should().BeOneOf("cgroup", "gc");

        result.CollectedAt.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task GetMetricsAsync_ComposesDependencyStatus_FromHealthProbes()
    {
        var svc = NewService(
            new ServiceCheck("PostgreSQL", "healthy", 3, DateTime.UtcNow.ToString("o")),
            new ServiceCheck("RabbitMQ", "unknown", 0, DateTime.UtcNow.ToString("o"), "URL not configured"),
            new ServiceCheck("ELSA Server", "unhealthy", 5000, DateTime.UtcNow.ToString("o"), "HTTP 503"));

        var result = await svc.GetMetricsAsync();

        result.Dependencies.Should().HaveCount(3);
        result.Dependencies.Should().ContainSingle(d => d.Name == "PostgreSQL" && d.Status == "healthy");
        // A DOWN dependency is surfaced, not thrown.
        result.Dependencies.Should().ContainSingle(d => d.Name == "ELSA Server" && d.Status == "unhealthy");
        // Allowlisted details survive verbatim.
        result.Dependencies.Single(d => d.Name == "ELSA Server").Detail.Should().Be("HTTP 503");
        result.Dependencies.Single(d => d.Name == "RabbitMQ").Detail.Should().Be("URL not configured");
        // A healthy probe carries no detail.
        result.Dependencies.Single(d => d.Name == "PostgreSQL").Detail.Should().BeNull();
    }

    [Test]
    public async Task GetMetricsAsync_NeverLeaksConnectionDetailsFromProbeException()
    {
        // A realistic Npgsql-style failure message embeds a host + user + password.
        const string leak =
            "Npgsql.NpgsqlException: Failed to connect to db.internal:5432 "
            + "(user=tamma password=sup3r-s3cret-p@ss)";

        var svc = NewService(
            new ServiceCheck("PostgreSQL", "unhealthy", 12, DateTime.UtcNow.ToString("o"), leak));

        var result = await svc.GetMetricsAsync();

        var pg = result.Dependencies.Single(d => d.Name == "PostgreSQL");
        pg.Status.Should().Be("unhealthy");
        // The raw exception text is collapsed to a coarse, leak-free category.
        pg.Detail.Should().Be("unreachable");

        // Belt-and-braces: the leaked host / user / password fragments must be
        // absent from the WHOLE serialized DTO. (A bare "password" word is NOT
        // checked — a legitimate disk mount path can contain it, e.g. a
        // /snap/1password mount on a dev host.)
        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("db.internal");
        json.Should().NotContain("user=tamma");
        json.Should().NotContain("sup3r-s3cret-p@ss");
    }

    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("URL not configured", "URL not configured")]
    [TestCase("HTTP 500", "HTTP 500")]
    [TestCase("Timed out after 5s", "Timed out after 5s")]
    [TestCase("Host=db.internal;Username=tamma;Password=secret", "unreachable")]
    [TestCase("Connection refused (10.0.0.5:5672)", "unreachable")]
    public void SanitizeDetail_AppliesAllowlist(string? input, string? expected)
    {
        InfrastructureMetricsService.SanitizeDetail(input).Should().Be(expected);
    }
}
