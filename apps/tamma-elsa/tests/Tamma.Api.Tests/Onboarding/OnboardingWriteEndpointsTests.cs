using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Onboarding;

/// <summary>
/// Story 18-4 — direct-handler guards + behaviour for the NON-MIGRATION write
/// slices on <see cref="OnboardingEndpoints"/>:
/// <list type="bullet">
///   <item><see cref="OnboardingEndpoints.SetRepoActive"/> (AC4) — flip the
///     EXISTING <c>IsActive</c> flag on one connected repo.</item>
///   <item><see cref="OnboardingEndpoints.CompleteOnboarding"/> (AC6/AC7) —
///     emit the <c>ONBOARDING.COMPLETED.SUCCESS</c> milestone event.</item>
/// </list>
///
/// <para>Handlers are called directly with fake repositories + a fake
/// <see cref="ITenantContext"/> (same style as
/// <see cref="Tamma.Api.Tests.Dashboard.ReposRunsEndpointsGuardTests"/>), so the
/// in-handler invariants are verified without an HTTP round-trip. Coverage:
/// null-tenant fail-closed (no repo fan-out), foreign-installation 404 (no
/// IDOR), unknown-repo 404, idempotent flip (no duplicate DCB event), and the
/// ONBOARDING.COMPLETED emission + idempotency.</para>
/// </summary>
[TestFixture]
public class OnboardingWriteEndpointsTests
{
    private const long InstallationId = 11111L;
    private const long RepoIdApi = 9001L;
    private const long RepoIdWeb = 9002L;

    // ── SetRepoActive: guards ─────────────────────────────────────────────

    [Test]
    public async Task SetRepoActive_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var installs = new RecordingInstallationRepo();
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(false),
            installs, events, new FakeTenantContext(null), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        installs.GetByInstallationIdCalled.Should().BeFalse("the guard must reject before touching installations");
        events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task SetRepoActive_EmptyTenant_FailsClosed()
    {
        var installs = new RecordingInstallationRepo();
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(true),
            installs, events, new FakeTenantContext(Guid.Empty), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        installs.GetByInstallationIdCalled.Should().BeFalse();
    }

    [Test]
    public async Task SetRepoActive_MissingBody_Returns400()
    {
        var tenant = Guid.NewGuid();
        var installs = new RecordingInstallationRepo();
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, request: null,
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("missing_body");
    }

    [Test]
    public async Task SetRepoActive_UnknownInstallation_Returns404()
    {
        var tenant = Guid.NewGuid();
        var installs = new RecordingInstallationRepo(); // empty
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(false),
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("installation_not_found");
        events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task SetRepoActive_ForeignInstallation_Returns404_NoIDOR()
    {
        var tenant = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var installs = new RecordingInstallationRepo();
        // Installation belongs to a DIFFERENT tenant — must be indistinguishable
        // from a non-existent one.
        installs.Installs.Add(Install(foreign, InstallationId, (RepoIdApi, "acme/api", true)));
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(false),
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("installation_not_found");
        installs.Removed.Should().BeEmpty("a foreign installation's repo must never be flipped");
        events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task SetRepoActive_UnknownRepo_Returns404()
    {
        var tenant = Guid.NewGuid();
        var installs = new RecordingInstallationRepo();
        installs.Installs.Add(Install(tenant, InstallationId, (RepoIdApi, "acme/api", true)));
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, repoId: 424242L, new SetRepoActiveRequest(false),
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("repo_not_found");
        events.Appended.Should().BeEmpty();
    }

    // ── SetRepoActive: behaviour ──────────────────────────────────────────

    [Test]
    public async Task SetRepoActive_Deactivate_FlipsFlag_AndEmitsDeactivatedEvent()
    {
        var tenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var install = Install(tenant, InstallationId, (RepoIdApi, "acme/api", true));
        var installs = new RecordingInstallationRepo();
        installs.Installs.Add(install);
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(false),
            installs, events, new FakeTenantContext(tenant), Principal(userId));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("active").GetBoolean().Should().BeFalse();
        root.GetProperty("changed").GetBoolean().Should().BeTrue();
        root.GetProperty("repoFullName").GetString().Should().Be("acme/api");

        installs.Removed.Should().ContainSingle().Which.Should().Be((install.Id, RepoIdApi));
        installs.Added.Should().BeEmpty();

        events.Appended.Should().ContainSingle();
        var evt = events.Appended[0];
        evt.Type.Should().Be("REPO.DEACTIVATED.SUCCESS");
        evt.TenantId.Should().Be(tenant);
        var tags = JsonDocument.Parse(evt.Tags).RootElement;
        tags.GetProperty("tenantId").GetString().Should().Be(tenant.ToString());
        tags.GetProperty("userId").GetString().Should().Be(userId.ToString());
        var data = JsonDocument.Parse(evt.Data).RootElement;
        data.GetProperty("installationId").GetInt64().Should().Be(InstallationId);
        data.GetProperty("repoId").GetInt64().Should().Be(RepoIdApi);
        data.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task SetRepoActive_Activate_FromInactive_FlipsAndEmitsActivatedEvent()
    {
        var tenant = Guid.NewGuid();
        var install = Install(tenant, InstallationId, (RepoIdWeb, "acme/web", false));
        var installs = new RecordingInstallationRepo();
        installs.Installs.Add(install);
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdWeb, new SetRepoActiveRequest(true),
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("active").GetBoolean().Should().BeTrue();
        root.GetProperty("changed").GetBoolean().Should().BeTrue();

        installs.Added.Should().ContainSingle().Which.Should().Be((install.Id, RepoIdWeb, "acme/web"));
        installs.Removed.Should().BeEmpty();
        events.Appended.Should().ContainSingle();
        events.Appended[0].Type.Should().Be("REPO.ACTIVATED.SUCCESS");
    }

    [Test]
    public async Task SetRepoActive_AlreadyInRequestedState_IsNoOp_NoEvent()
    {
        var tenant = Guid.NewGuid();
        var install = Install(tenant, InstallationId, (RepoIdApi, "acme/api", true));
        var installs = new RecordingInstallationRepo();
        installs.Installs.Add(install);
        var events = new RecordingEventRepo();

        // Activating an already-active repo — idempotent no-op.
        var result = await OnboardingEndpoints.SetRepoActive(
            InstallationId, RepoIdApi, new SetRepoActiveRequest(true),
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("active").GetBoolean().Should().BeTrue();
        root.GetProperty("changed").GetBoolean().Should().BeFalse();

        installs.Added.Should().BeEmpty("no flip means no repository mutation");
        installs.Removed.Should().BeEmpty();
        events.Appended.Should().BeEmpty("an idempotent no-op must NOT emit a duplicate DCB event");
    }

    // ── CompleteOnboarding ────────────────────────────────────────────────

    [Test]
    public async Task CompleteOnboarding_NullTenant_FailsClosed_WithoutReadingEvents()
    {
        var installs = new RecordingInstallationRepo();
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.CompleteOnboarding(
            installs, events, new FakeTenantContext(null), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        events.GetLastCalled.Should().BeFalse();
        events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task CompleteOnboarding_FirstTime_EmitsCompletedEvent_WithSetupSummary()
    {
        var tenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var installs = new RecordingInstallationRepo();
        // One live installation with 2 active + 1 inactive repo → activeRepoCount = 2.
        installs.Installs.Add(Install(tenant, InstallationId,
            (RepoIdApi, "acme/api", true), (RepoIdWeb, "acme/web", true), (9003L, "acme/infra", false)));
        var events = new RecordingEventRepo();

        var result = await OnboardingEndpoints.CompleteOnboarding(
            installs, events, new FakeTenantContext(tenant), Principal(userId));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("completed").GetBoolean().Should().BeTrue();
        root.GetProperty("alreadyCompleted").GetBoolean().Should().BeFalse();
        root.GetProperty("installationCount").GetInt32().Should().Be(1);
        root.GetProperty("activeRepoCount").GetInt32().Should().Be(2);

        events.Appended.Should().ContainSingle();
        var evt = events.Appended[0];
        evt.Type.Should().Be(OnboardingEndpoints.OnboardingCompletedEventType);
        evt.Type.Should().Be("ONBOARDING.COMPLETED.SUCCESS");
        evt.TenantId.Should().Be(tenant);
        var tags = JsonDocument.Parse(evt.Tags).RootElement;
        tags.GetProperty("tenantId").GetString().Should().Be(tenant.ToString());
        tags.GetProperty("userId").GetString().Should().Be(userId.ToString());
        var data = JsonDocument.Parse(evt.Data).RootElement;
        data.GetProperty("installationCount").GetInt32().Should().Be(1);
        data.GetProperty("activeRepoCount").GetInt32().Should().Be(2);
    }

    [Test]
    public async Task CompleteOnboarding_AlreadyCompleted_IsIdempotent_NoDuplicateEvent()
    {
        var tenant = Guid.NewGuid();
        var priorAt = new DateTime(2026, 5, 1, 9, 30, 0, DateTimeKind.Utc);
        var installs = new RecordingInstallationRepo();
        var events = new RecordingEventRepo
        {
            Preset = new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = OnboardingEndpoints.OnboardingCompletedEventType,
                TenantId = tenant,
                CreatedAt = priorAt,
            },
        };

        var result = await OnboardingEndpoints.CompleteOnboarding(
            installs, events, new FakeTenantContext(tenant), Principal(Guid.NewGuid()));

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("completed").GetBoolean().Should().BeTrue();
        root.GetProperty("alreadyCompleted").GetBoolean().Should().BeTrue();
        root.GetProperty("completedAt").GetDateTime().Should().Be(priorAt);

        events.Appended.Should().BeEmpty("a second completion must NOT append a duplicate milestone");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static GitHubInstallation Install(
        Guid? tenantId, long installationId, params (long RepoId, string FullName, bool IsActive)[] repos)
    {
        var install = new GitHubInstallation
        {
            Id = Guid.NewGuid(),
            InstallationId = installationId,
            AccountLogin = "acme-corp",
            AccountType = "Organization",
            AppId = 42,
            TenantId = tenantId,
            Permissions = "{}",
        };
        foreach (var (repoId, fullName, isActive) in repos)
        {
            var slash = fullName.IndexOf('/');
            install.Repos.Add(new GitHubInstallationRepo
            {
                Id = Guid.NewGuid(),
                InstallationEntityId = install.Id,
                RepoId = repoId,
                Owner = slash > 0 ? fullName[..slash] : fullName,
                Name = slash > 0 ? fullName[(slash + 1)..] : fullName,
                RepoFullName = fullName,
                IsActive = isActive,
            });
        }
        return install;
    }

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

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

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class RecordingInstallationRepo : IInstallationRepository
    {
        public List<GitHubInstallation> Installs { get; } = new();
        public bool GetByInstallationIdCalled { get; private set; }
        public List<(Guid EntityId, long RepoId, string FullName)> Added { get; } = new();
        public List<(Guid EntityId, long RepoId)> Removed { get; } = new();

        public Task<GitHubInstallation?> GetByInstallationIdAsync(long installationId)
        {
            GetByInstallationIdCalled = true;
            return Task.FromResult(Installs.FirstOrDefault(i => i.InstallationId == installationId));
        }

        public Task AddRepoAsync(Guid installationEntityId, long repoId, string repoFullName)
        {
            Added.Add((installationEntityId, repoId, repoFullName));
            var repo = Installs.FirstOrDefault(i => i.Id == installationEntityId)
                ?.Repos.FirstOrDefault(r => r.RepoId == repoId);
            if (repo is not null) repo.IsActive = true;
            return Task.CompletedTask;
        }

        public Task RemoveRepoAsync(Guid installationEntityId, long repoId)
        {
            Removed.Add((installationEntityId, repoId));
            var repo = Installs.FirstOrDefault(i => i.Id == installationEntityId)
                ?.Repos.FirstOrDefault(r => r.RepoId == repoId);
            if (repo is not null) repo.IsActive = false;
            return Task.CompletedTask;
        }

        public Task<List<GitHubInstallation>> ListByTenantAsync(Guid tenantId)
            => Task.FromResult(Installs.Where(i => i.TenantId == tenantId).ToList());

        public Task<GitHubInstallation> UpsertAsync(GitHubInstallation installation) => throw new NotSupportedException();
        public Task<GitHubInstallation?> GetByEntityIdAsync(Guid entityId) => throw new NotSupportedException();
        public Task<List<GitHubInstallation>> ListAsync() => throw new NotSupportedException();
        public Task<List<GitHubInstallation>> ListActiveAsync() => throw new NotSupportedException();
        public Task DeleteAsync(long installationId) => throw new NotSupportedException();
        public Task SetReposAsync(Guid installationEntityId, List<GitHubInstallationRepo> repos) => throw new NotSupportedException();
        public Task<List<GitHubInstallationRepo>> ListReposAsync(Guid installationEntityId) => throw new NotSupportedException();
        public Task SuspendAsync(long installationId) => throw new NotSupportedException();
        public Task UnsuspendAsync(long installationId) => throw new NotSupportedException();
        public Task<GitHubInstallation> CreateAsync(GitHubInstallation install) => throw new NotSupportedException();
        public Task SoftDeleteAsync(long installationId) => throw new NotSupportedException();
        public Task SetSuspendedAsync(long installationId, bool suspended) => throw new NotSupportedException();
        public Task<GitHubInstallation?> GetByRepoFullNameAsync(string repoFullName) => throw new NotSupportedException();
    }

    private sealed class RecordingEventRepo : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public DomainEvent? Preset { get; set; }
        public bool GetLastCalled { get; private set; }

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
        {
            GetLastCalled = true;
            if (Preset is not null && Preset.TenantId == tenantId && Preset.Type == type)
                return Task.FromResult<DomainEvent?>(Preset);
            return Task.FromResult(Appended.LastOrDefault(e => e.TenantId == tenantId && e.Type == type));
        }

        public Task<IReadOnlyList<DomainEvent>> ListByCorrelationIdAsync(Guid tenantId, string correlationId) => throw new NotSupportedException();
        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }
}
