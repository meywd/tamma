using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets.Query;

/// <summary>
/// Unit tests for <see cref="SecretQueryService"/> — the Story 29-4 /
/// 29-5 query + retire surface consumed by the admin + tenant secret
/// UIs. Pins:
///
/// <list type="bullet">
///   <item><description>Scope filter on list — platform scope never
///     surfaces tenant rows and vice versa.</description></item>
///   <item><description>Scope filter on get — cross-tenant read
///     returns null (not leak-through).</description></item>
///   <item><description>Retire refuses the active version.</description></item>
///   <item><description>Retire scrubs ciphertext on revoke.</description></item>
///   <item><description>Retire emits <c>SECRET.VERSION.REVOKED</c>.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SecretQueryServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid ActorUserId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private SecretsDbContextFactoryDouble _contextFactory = null!;
    private RecordingAuditor _auditor = null!;
    private SecretQueryService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _contextFactory = new SecretsDbContextFactoryDouble(Guid.NewGuid().ToString());
        _auditor = new RecordingAuditor();
        _service = new SecretQueryService(
            _contextFactory,
            _auditor,
            TimeProvider.System,
            NullLogger<SecretQueryService>.Instance);
    }

    [TearDown]
    public void TearDown() => _contextFactory.Dispose();

    // ── ListAsync ───────────────────────────────────────────────────

    [Test]
    public async Task List_Platform_OnlyReturnsPlatformRows()
    {
        await SeedAsync(SecretRow("platform-a", "platform", null));
        await SeedAsync(SecretRow("tenant-a", "tenant", TenantA));

        var rows = await _service.ListAsync(SecretScope.Platform, null);

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("platform-a");
        rows[0].Scope.Should().Be(SecretScope.Platform);
    }

    [Test]
    public async Task List_Tenant_FiltersByTenantId()
    {
        await SeedAsync(SecretRow("tenant-a", "tenant", TenantA));
        await SeedAsync(SecretRow("tenant-b", "tenant", TenantB));

        var rows = await _service.ListAsync(SecretScope.Tenant, TenantA);

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("tenant-a");
        rows[0].TenantId.Should().Be(TenantA);
    }

    [Test]
    public void List_Tenant_WithoutTenantId_Throws()
    {
        Func<Task> act = () => _service.ListAsync(SecretScope.Tenant, null);
        act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void List_Platform_WithTenantId_Throws()
    {
        Func<Task> act = () => _service.ListAsync(SecretScope.Platform, TenantA);
        act.Should().ThrowAsync<ArgumentException>();
    }

    // ── GetAsync — scope enforcement ────────────────────────────────

    [Test]
    public async Task Get_CrossTenantAccess_ReturnsNull()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        await SeedAsync(row);

        var asTenantB = await _service.GetAsync(row.Id, SecretScope.Tenant, TenantB);

        asTenantB.Should().BeNull("tenant B must not see tenant A's secret");
    }

    [Test]
    public async Task Get_PlatformRequestForTenantRow_ReturnsNull()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        await SeedAsync(row);

        var asPlatform = await _service.GetAsync(row.Id, SecretScope.Platform, null);

        asPlatform.Should().BeNull("platform scope cannot pull tenant rows");
    }

    [Test]
    public async Task Get_MatchingScope_ReturnsMetadata()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        await SeedAsync(row);

        var result = await _service.GetAsync(row.Id, SecretScope.Tenant, TenantA);

        result.Should().NotBeNull();
        result!.Name.Should().Be("db/app-role");
        result.TenantId.Should().Be(TenantA);
    }

    // ── ListVersionsAsync ───────────────────────────────────────────

    [Test]
    public async Task ListVersions_Unauthorized_ReturnsEmpty()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 1, "active");

        var versionsAsB = await _service.ListVersionsAsync(
            row.Id, SecretScope.Tenant, TenantB);

        versionsAsB.Should().BeEmpty();
    }

    [Test]
    public async Task ListVersions_NewestFirst()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 1, "retired_grace");
        await SeedVersionAsync(row.Id, 2, "active");
        await SeedVersionAsync(row.Id, 3, "pending");

        var versions = await _service.ListVersionsAsync(
            row.Id, SecretScope.Tenant, TenantA);

        versions.Should().HaveCount(3);
        versions[0].VersionNumber.Should().Be(3);
        versions[1].VersionNumber.Should().Be(2);
        versions[2].VersionNumber.Should().Be(1);
    }

    // ── RetireVersionAsync ──────────────────────────────────────────

    [Test]
    public async Task Retire_Active_Throws()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        row.ActiveVersionNumber = 2;
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 2, "active");

        Func<Task> act = () => _service.RetireVersionAsync(
            row.Id, 2, SecretScope.Tenant, TenantA, ActorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Retire_RetiredGrace_FlipsToRevoked_AndScrubs()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        row.ActiveVersionNumber = 2;
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 1, "retired_grace", ciphertext: new byte[] { 1, 2, 3 });
        await SeedVersionAsync(row.Id, 2, "active");

        var status = await _service.RetireVersionAsync(
            row.Id, 1, SecretScope.Tenant, TenantA, ActorUserId);

        status.Should().Be(SecretVersionStatus.Revoked);

        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var v = await ctx.SecretVersions.FirstAsync(
            v => v.SecretId == row.Id && v.VersionNumber == 1);
        v.Status.Should().Be("revoked");
        v.Ciphertext.Should().BeNull("revoke must scrub the envelope bytes");
    }

    [Test]
    public async Task Retire_EmitsAuditEvent()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        row.ActiveVersionNumber = 2;
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 1, "retired_grace");
        await SeedVersionAsync(row.Id, 2, "active");

        await _service.RetireVersionAsync(
            row.Id, 1, SecretScope.Tenant, TenantA, ActorUserId);

        _auditor.Events.Should().ContainSingle();
        _auditor.Events[0].EventType.Should().Be(SecretAuditEventTypes.VersionRevoked);
        _auditor.Events[0].VersionNumber.Should().Be(1);
        _auditor.Events[0].ActorUserId.Should().Be(ActorUserId);
        _auditor.Events[0].Reference.TenantId.Should().Be(TenantA);
    }

    [Test]
    public async Task Retire_CrossTenantAccess_ThrowsKeyNotFound()
    {
        var row = SecretRow("db/app-role", "tenant", TenantA);
        row.ActiveVersionNumber = 2;
        await SeedAsync(row);
        await SeedVersionAsync(row.Id, 1, "retired_grace");

        Func<Task> act = () => _service.RetireVersionAsync(
            row.Id, 1, SecretScope.Tenant, TenantB, ActorUserId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── helpers ─────────────────────────────────────────────────────

    private async Task SeedAsync(SecretRow row)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync();
        ctx.Secrets.Add(row);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedVersionAsync(
        Guid secretId,
        int version,
        string status,
        byte[]? ciphertext = null)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync();
        ctx.SecretVersions.Add(new SecretVersionRow
        {
            Id = Guid.NewGuid(),
            SecretId = secretId,
            VersionNumber = version,
            Status = status,
            Ciphertext = ciphertext,
            KekId = 1,
            FormatVersion = 1,
            CreatedAt = DateTime.UtcNow,
            ActivatedAt = status == "active" ? DateTime.UtcNow : null,
            RetiredAt = status is "retired_grace" or "revoked" ? DateTime.UtcNow : null,
            CreatedByUserId = Guid.Empty,
        });
        await ctx.SaveChangesAsync();
    }

    private static SecretRow SecretRow(string name, string scope, Guid? tenantId)
    {
        return new SecretRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            Scope = scope,
            TenantId = tenantId,
            Purpose = "DbCredential",
            ConsumerRefsJson = "[]",
            RotationScheduleJson = "{\"Kind\":\"None\"}",
            OwnerUserId = Guid.Empty,
            ActiveVersionNumber = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // ── doubles ─────────────────────────────────────────────────────

    private sealed class SecretsDbContextFactoryDouble
        : IDbContextFactory<SecretsDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretsDbContext? _trackingHandle;

        public SecretsDbContextFactoryDouble(string dbName)
        {
            _dbName = dbName;
            _trackingHandle = CreateDbContext();
            _trackingHandle.Database.EnsureCreated();
        }

        public SecretsDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SecretsDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics
                        .InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SecretsDbContext(options);
        }

        public Task<SecretsDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _trackingHandle?.Dispose();
            _trackingHandle = null;
        }
    }

    private sealed class RecordingAuditor : ISecretAccessAuditor
    {
        public List<SecretAuditEvent> Events { get; } = new();
        public Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
