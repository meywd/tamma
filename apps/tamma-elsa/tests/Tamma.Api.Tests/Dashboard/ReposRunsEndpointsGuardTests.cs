using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Dashboard;

/// <summary>
/// Story 21-4 — guards + projections on the tenant-facing Repos &amp; Workflow
/// Runs read endpoints (<see cref="ReposRunsEndpoints"/>). Handlers are called
/// directly with fake repositories + a fake <see cref="ITenantContext"/> so the
/// in-handler invariants are verified without an HTTP round-trip (same pattern
/// as <see cref="Tamma.Api.Tests.Diagnostics.ProviderDiagnosticsGuardTests"/>).
///
/// <para>Coverage:
/// <list type="bullet">
///   <item><b>Fail-closed</b> — a null / empty ambient tenant returns
///     <c>404 no_active_tenant</c> BEFORE any repository call (no cross-tenant
///     fan-out).</item>
///   <item><b>Tenant-scoping</b> — a run owned by another tenant is
///     <c>404 run_not_found</c> even though the id is valid.</item>
///   <item><b>No economics leak</b> — per-run cost is summed from the run's OWN
///     recorded <c>Data.costUsd</c> events, never a platform margin.</item>
/// </list></para>
/// </summary>
[TestFixture]
public class ReposRunsEndpointsGuardTests
{
    // ── /api/v1/repos ─────────────────────────────────────────────────────

    [Test]
    public async Task ListRepos_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingInstallationRepo();

        var result = await ReposRunsEndpoints.ListRepos(
            repo, new FakeTenantContext(null), NewHttpContext());

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.Called.Should().BeFalse("the guard must reject before enumerating installations");
    }

    [Test]
    public async Task ListRepos_EmptyTenant_FailsClosed()
    {
        var repo = new RecordingInstallationRepo();

        var result = await ReposRunsEndpoints.ListRepos(
            repo, new FakeTenantContext(Guid.Empty), NewHttpContext());

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.Called.Should().BeFalse();
    }

    [Test]
    public async Task ListRepos_ConcreteTenant_ReturnsOnlyThatTenantsInstallations()
    {
        var tenant = Guid.NewGuid();
        var repo = new RecordingInstallationRepo
        {
            Rows =
            {
                new TenantPlatformInstallation
                {
                    Id = Guid.NewGuid(), TenantId = tenant, PlatformKind = "github",
                    BaseUrl = "https://api.github.com", InstallationExternalId = "42",
                    Status = "connected", IsPrimary = true,
                    MetadataJson = "{\"name\":\"acme/widgets\"}",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                },
            },
        };

        var result = await ReposRunsEndpoints.ListRepos(
            repo, new FakeTenantContext(tenant), NewHttpContext());

        StatusOf(result).Should().Be(200);
        repo.RequestedTenant.Should().Be(tenant);
        var root = await CaptureJson(result);
        root.GetProperty("count").GetInt32().Should().Be(1);
        var first = root.GetProperty("repos")[0];
        first.GetProperty("name").GetString().Should().Be("acme/widgets");
        first.GetProperty("platform").GetString().Should().Be("github");
        first.GetProperty("status").GetString().Should().Be("connected");
    }

    // ── /api/v1/runs ──────────────────────────────────────────────────────

    [Test]
    public async Task ListRuns_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingWorkflowRepo();

        var result = await ReposRunsEndpoints.ListRuns(
            repo, new FakeTenantContext(null), limit: null, page: null);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.ListCalled.Should().BeFalse("the guard must reject before listing instances");
    }

    [Test]
    public async Task ListRuns_ConcreteTenant_ProjectsRunSummaries()
    {
        var tenant = Guid.NewGuid();
        var started = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
        var repo = new RecordingWorkflowRepo
        {
            Instances =
            {
                new WorkflowInstance
                {
                    Id = Guid.NewGuid(), DefinitionId = Guid.NewGuid(), TenantId = tenant,
                    Status = "completed", CurrentActivity = "done",
                    CreatedAt = started, StartedAt = started, CompletedAt = started.AddMinutes(2),
                },
            },
        };

        var result = await ReposRunsEndpoints.ListRuns(
            repo, new FakeTenantContext(tenant), limit: null, page: null);

        StatusOf(result).Should().Be(200);
        repo.RequestedTenant.Should().Be(tenant);
        var root = await CaptureJson(result);
        root.GetProperty("total").GetInt32().Should().Be(1);
        var run = root.GetProperty("runs")[0];
        run.GetProperty("status").GetString().Should().Be("completed");
        run.GetProperty("durationMs").GetDouble().Should().Be(TimeSpan.FromMinutes(2).TotalMilliseconds);
    }

    [Test]
    public async Task ListRuns_ClampsLimitTo100()
    {
        var tenant = Guid.NewGuid();
        var repo = new RecordingWorkflowRepo();

        await ReposRunsEndpoints.ListRuns(repo, new FakeTenantContext(tenant), limit: 500, page: 3);

        repo.RequestedPageSize.Should().Be(100);
        repo.RequestedPage.Should().Be(3);
    }

    // ── /api/v1/runs/{runId} ──────────────────────────────────────────────

    [Test]
    public async Task GetRunDetail_NullTenant_FailsClosed_WithoutCallingRepos()
    {
        var workflows = new RecordingWorkflowRepo();
        var events = new RecordingEventRepo();

        var result = await ReposRunsEndpoints.GetRunDetail(
            Guid.NewGuid(), workflows, events, new FakeTenantContext(null));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        workflows.GetCalled.Should().BeFalse();
        events.Called.Should().BeFalse();
    }

    [Test]
    public async Task GetRunDetail_ForeignTenantInstance_Returns404_WithoutReadingEvents()
    {
        var tenant = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var workflows = new RecordingWorkflowRepo
        {
            // The bare-id read surfaces an instance owned by ANOTHER tenant.
            InstanceById = new WorkflowInstance
            {
                Id = runId, DefinitionId = Guid.NewGuid(), TenantId = foreign, Status = "completed",
            },
        };
        var events = new RecordingEventRepo();

        var result = await ReposRunsEndpoints.GetRunDetail(
            runId, workflows, events, new FakeTenantContext(tenant));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("run_not_found");
        events.Called.Should().BeFalse("a cross-tenant run must never read the event timeline");
    }

    [Test]
    public async Task GetRunDetail_UnknownRun_Returns404()
    {
        var tenant = Guid.NewGuid();
        var workflows = new RecordingWorkflowRepo { InstanceById = null };
        var events = new RecordingEventRepo();

        var result = await ReposRunsEndpoints.GetRunDetail(
            Guid.NewGuid(), workflows, events, new FakeTenantContext(tenant));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("run_not_found");
    }

    [Test]
    public async Task GetRunDetail_OwnedRun_SumsOwnCostAndBuildsTimeline()
    {
        var tenant = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
        var workflows = new RecordingWorkflowRepo
        {
            InstanceById = new WorkflowInstance
            {
                Id = runId, DefinitionId = Guid.NewGuid(), TenantId = tenant,
                Status = "completed", CreatedAt = t, StartedAt = t, CompletedAt = t.AddMinutes(3),
            },
        };
        var events = new RecordingEventRepo
        {
            Timeline =
            {
                Event(runId, tenant, "AGENT.TASK.STARTED", t, provider: "anthropic-claude", costUsd: null),
                Event(runId, tenant, "AGENT.TASK.SUCCESS", t.AddMinutes(1),
                    provider: "anthropic-claude", costUsd: 1.25m, prUrl: "https://github.com/acme/w/pull/7"),
                Event(runId, tenant, "AGENT.TASK.SUCCESS", t.AddMinutes(2),
                    provider: "anthropic-claude", costUsd: 0.75m),
            },
        };

        var result = await ReposRunsEndpoints.GetRunDetail(
            runId, workflows, events, new FakeTenantContext(tenant));

        StatusOf(result).Should().Be(200);
        events.RequestedTenant.Should().Be(tenant);
        events.RequestedCorrelationId.Should().Be(runId.ToString());

        var root = await CaptureJson(result);
        root.GetProperty("id").GetGuid().Should().Be(runId);
        root.GetProperty("totalCostUsd").GetDecimal().Should().Be(2.0m); // tenant's OWN 1.25 + 0.75
        root.GetProperty("provider").GetString().Should().Be("anthropic-claude");
        root.GetProperty("prUrl").GetString().Should().Be("https://github.com/acme/w/pull/7");
        root.GetProperty("eventCount").GetInt32().Should().Be(3);
        root.GetProperty("events").GetArrayLength().Should().Be(3);
        root.GetProperty("logs").GetArrayLength().Should().Be(3);
        root.GetProperty("durationMs").GetDouble().Should().Be(TimeSpan.FromMinutes(3).TotalMilliseconds);
        root.GetProperty("truncated").GetBoolean().Should().BeFalse(
            "a run under the fetch cap reports truncated:false");
    }

    [Test]
    public async Task GetRunDetail_TruncatedTimeline_SetsTruncatedFlag()
    {
        var tenant = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
        var workflows = new RecordingWorkflowRepo
        {
            InstanceById = new WorkflowInstance
            {
                Id = runId, DefinitionId = Guid.NewGuid(), TenantId = tenant, Status = "running",
            },
        };
        // The bounded fetch reports Truncated == true when the run exceeds the cap. The
        // endpoint must SURFACE that flag (no silent drop) — proven here via the fake.
        var events = new RecordingEventRepo
        {
            ForceTruncated = true,
            Timeline = { Event(runId, tenant, "AGENT.TASK.STARTED", t) },
        };

        var result = await ReposRunsEndpoints.GetRunDetail(
            runId, workflows, events, new FakeTenantContext(tenant));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("truncated").GetBoolean().Should().BeTrue(
            "a run over the fetch cap must be signalled as truncated, not silently dropped");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HttpContext NewHttpContext() => new DefaultHttpContext();

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static async Task<JsonElement> CaptureJson(IResult result)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private static DomainEvent Event(
        Guid runId, Guid tenant, string type, DateTime createdAt,
        string? provider = null, decimal? costUsd = null, string? prUrl = null)
    {
        var tags = new Dictionary<string, object?> { ["correlationId"] = runId.ToString() };
        if (provider is not null) tags["provider"] = provider;
        var data = new Dictionary<string, object?>();
        if (costUsd is decimal c) data["costUsd"] = c;
        if (prUrl is not null) data["prUrl"] = prUrl;
        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenant,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = "{}",
            Data = JsonSerializer.Serialize(data),
            CreatedAt = createdAt,
            SequenceNumber = createdAt.Ticks,
        };
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class RecordingInstallationRepo : ITenantPlatformInstallationRepository
    {
        public bool Called { get; private set; }
        public Guid? RequestedTenant { get; private set; }
        public List<TenantPlatformInstallation> Rows { get; } = new();

        public Task<IReadOnlyList<TenantPlatformInstallation>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            Called = true;
            RequestedTenant = tenantId;
            return Task.FromResult<IReadOnlyList<TenantPlatformInstallation>>(
                Rows.Where(r => r.TenantId == tenantId).ToList());
        }

        public Task<TenantPlatformInstallation?> GetByTenantPrimaryAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantPlatformInstallation?> GetByTenantKindAsync(Guid tenantId, string platformKind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantPlatformInstallation?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantPlatformInstallation?> GetByExternalIdAsync(string platformKind, string installationExternalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantPlatformInstallation> CreateAsync(TenantPlatformInstallation row, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantPlatformInstallation> UpdateAsync(TenantPlatformInstallation row, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingWorkflowRepo : IWorkflowRepository
    {
        public bool ListCalled { get; private set; }
        public bool GetCalled { get; private set; }
        public Guid? RequestedTenant { get; private set; }
        public int RequestedPage { get; private set; }
        public int RequestedPageSize { get; private set; }
        public List<WorkflowInstance> Instances { get; } = new();
        public WorkflowInstance? InstanceById { get; set; }

        public Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(
            Guid? definitionId, Guid? tenantId, int page, int pageSize)
        {
            ListCalled = true;
            RequestedTenant = tenantId;
            RequestedPage = page;
            RequestedPageSize = pageSize;
            return Task.FromResult((Instances.ToList(), Instances.Count));
        }

        public Task<WorkflowInstance?> GetInstanceAsync(Guid id)
        {
            GetCalled = true;
            return Task.FromResult(InstanceById);
        }

        public Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def) => throw new NotSupportedException();
        public Task<WorkflowDefinition?> GetDefinitionAsync(Guid id) => throw new NotSupportedException();
        public Task<List<WorkflowDefinition>> ListDefinitionsAsync() => throw new NotSupportedException();
        public Task<WorkflowInstance> CreateInstanceAsync(WorkflowInstance instance) => throw new NotSupportedException();
        public Task<WorkflowInstance?> UpdateInstanceAsync(Guid id, Action<WorkflowInstance> update) => throw new NotSupportedException();
        public Task<bool> DeleteInstanceAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class RecordingEventRepo : IEventRepository
    {
        public bool Called { get; private set; }
        public Guid? RequestedTenant { get; private set; }
        public string? RequestedCorrelationId { get; private set; }
        public bool ForceTruncated { get; set; }
        public List<DomainEvent> Timeline { get; } = new();

        // GetRunDetail now reads via the BOUNDED overload. Record the same call metadata
        // and surface a Truncated flag (forced for the truncation-plumbing test, else
        // derived from the requested cap).
        public Task<(IReadOnlyList<DomainEvent> Events, bool Truncated)> ListByCorrelationIdAsync(
            Guid tenantId, string correlationId, int maxEvents)
        {
            Called = true;
            RequestedTenant = tenantId;
            RequestedCorrelationId = correlationId;
            var truncated = ForceTruncated || Timeline.Count > maxEvents;
            var events = (IReadOnlyList<DomainEvent>)Timeline.Take(maxEvents).ToList();
            return Task.FromResult((events, truncated));
        }

        public Task<DomainEvent> AppendAsync(DomainEvent evt) => throw new NotSupportedException();
        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }
}
