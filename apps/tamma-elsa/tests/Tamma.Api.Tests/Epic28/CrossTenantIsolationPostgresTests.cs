using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Wave-4 review M5 — cross-tenant isolation negative tests for the
/// <see cref="EmailOutboxRepository"/> and <see cref="QueuedTaskRepository"/>
/// against a real Postgres testcontainer.
///
/// <para>The pre-fix bug: <see cref="EmailOutboxRepository.ClaimNextPendingAsync"/>
/// emitted a Postgres SQL <c>UPDATE ... FROM email_outbox</c> that lacked
/// <c>WHERE "TenantId" = @tid</c>, and 5 <see cref="QueuedTaskRepository"/>
/// methods (<c>GetAsync</c>, <c>MarkProcessingAsync</c>, <c>MarkCompletedAsync</c>,
/// <c>MarkFailedAsync</c>, <c>IncrementRetryAndRequeueAsync</c>) used
/// <c>FindAsync(id)</c> which keys on PK only. While the per-tenant tables
/// physically still co-reside on the CP DB during the Story 28-1 transition,
/// tenant A's call could read or mutate tenant B's row.</para>
///
/// <para>Why Postgres, not EF-InMemory: EF-InMemory satisfies <c>FindAsync(id)</c>
/// from a global PK dictionary regardless of how the predicates filter, so
/// the missing predicates pass silently. A real Postgres connection is the
/// only thing that exposes the gap. These tests run against a
/// <see cref="PostgreSqlContainer"/>, seed rows via raw SQL into a single
/// shared <c>email_outbox</c> / <c>queued_tasks</c> table (mirroring the
/// transitional shared-DB topology), then call the repository as the other
/// tenant and assert the read/write is a no-op.</para>
/// </summary>
[TestFixture]
public class CrossTenantIsolationPostgresTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("xtenant_iso_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Create the two tables we need (mirroring the EmailOutbox /
        // TaskQueue / TaskQueueClaimedAt migrations) plus a minimal
        // tenants table for the active-tenant lookup. Skipping the full
        // migration bundle keeps the fixture under 5s.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "Tenants" (
                "Id" uuid PRIMARY KEY,
                "DeletedAt" timestamptz NULL
            );

            CREATE TABLE IF NOT EXISTS email_outbox (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NULL,
                "UserId" uuid NULL,
                "Template" varchar(100) NOT NULL,
                "ToAddress" varchar(320) NOT NULL,
                "Subject" varchar(512) NOT NULL,
                "HtmlBody" text NOT NULL,
                "TextBody" text NOT NULL,
                "FromAddress" varchar(320) NOT NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'pending',
                "Attempts" integer NOT NULL DEFAULT 0,
                "MaxAttempts" integer NOT NULL DEFAULT 5,
                "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
                "LastError" text NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
                "SentAt" timestamptz NULL
            );

            CREATE TABLE IF NOT EXISTS queued_tasks (
                "Id" uuid PRIMARY KEY,
                "Type" varchar(255) NOT NULL,
                "TenantId" uuid NULL,
                "InstallationId" bigint NULL,
                "Payload" jsonb NOT NULL DEFAULT '{}'::jsonb,
                "Status" varchar(20) NOT NULL DEFAULT 'pending',
                "Error" text NULL,
                "RetryCount" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
                "ClaimedAt" timestamptz NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        // Reset both tables + reseed two active tenants per test.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            TRUNCATE email_outbox, queued_tasks, "Tenants";
            INSERT INTO "Tenants" ("Id") VALUES (@a), (@b);
            """;
        cmd.Parameters.AddWithValue("a", TenantA);
        cmd.Parameters.AddWithValue("b", TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private (EmailOutboxRepository emailRepo, QueuedTaskRepository taskRepo, ControlPlaneDbContext cp)
        BuildRepos()
    {
        // The TenantDbContextFactory used here is intentionally trivial:
        // every tenant gets a context against the same connection string.
        // That mirrors the transitional shared-DB topology — the very
        // setting where the predicate-gap bug bites.
        var cpOpts = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        var tenantOpts = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        // Use the production ControlPlaneDbContext but configure a
        // minimal "Tenants" entity slice via a derived context. The CP
        // model is large; for this test we only need the Tenants set
        // for the active-tenant lookup. We override by using a raw
        // scoped helper that the repositories actually consume.
        var cp = new MinimalCpDbContext(cpOpts);
        var factory = new SharedConnectionTenantFactory(tenantOpts);
        var emailRepo = new EmailOutboxRepository(factory, cp);
        var taskRepo = new QueuedTaskRepository(factory, cp);
        return (emailRepo, taskRepo, cp);
    }

    private async Task SeedEmailRowAsync(Guid tenantId, Guid id, string status = "pending")
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO email_outbox
                ("Id","TenantId","Template","ToAddress","Subject","HtmlBody","TextBody",
                 "FromAddress","Status","Attempts","MaxAttempts","NextAttemptAt",
                 "CreatedAt","UpdatedAt")
            VALUES (@id,@tid,'verification','to@example.com','subj','<p/>','t',
                    'from@example.com',@status,0,5,now(),now(),now())
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedTaskRowAsync(Guid tenantId, Guid id, string status = "pending")
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO queued_tasks
                ("Id","Type","TenantId","Payload","Status","RetryCount",
                 "CreatedAt","UpdatedAt")
            VALUES (@id,'github.test',@tid,'{}'::jsonb,@status,0,now(),now())
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(string status, Guid? tenantId)> ReadEmailRowAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Status","TenantId" FROM email_outbox WHERE "Id" = @id
            """;
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var s = reader.GetString(0);
        var tid = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        return (s, tid);
    }

    private async Task<(string status, Guid? tenantId, int retryCount)>
        ReadTaskRowAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Status","TenantId","RetryCount" FROM queued_tasks WHERE "Id" = @id
            """;
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var s = reader.GetString(0);
        var tid = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var rc = reader.GetInt32(2);
        return (s, tid, rc);
    }

    // ── EmailOutboxRepository.ClaimNextPendingAsync ────────────────────

    [Test]
    public async Task ClaimNextPendingAsync_DoesNotReturn_OtherTenantsRow_OnPostgres()
    {
        // Seed-in-A; query-as-B; expect-null.
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        var claimed = await emailRepo.ClaimNextPendingAsync(TenantB, DateTime.UtcNow);

        claimed.Should().BeNull(
            "tenant B must not be able to claim tenant A's row from the shared email_outbox");

        // Belt-and-braces: the row must still be tenant A's, still pending.
        var (status, tid) = await ReadEmailRowAsync(rowId);
        status.Should().Be("pending", "tenant B's claim must NOT have flipped status to 'sending'");
        tid.Should().Be(TenantA);
    }

    [Test]
    public async Task ClaimNextPendingAsync_ReturnsOwnTenantsRow_OnPostgres()
    {
        // Positive control — confirm the predicate doesn't reject the
        // legitimate owner's claim. If this fails, the WHERE clause is
        // over-tight, not under-tight.
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        var claimed = await emailRepo.ClaimNextPendingAsync(TenantA, DateTime.UtcNow);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(rowId);
        claimed.TenantId.Should().Be(TenantA);
    }

    // ── EmailOutboxRepository.MarkSent / MarkFailed / GetByIdAsync /
    //    DeleteAsync — wrong-tenant must be no-op ──────────────────────

    [Test]
    public async Task MarkSentAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        await emailRepo.MarkSentAsync(TenantB, rowId);

        var (status, _) = await ReadEmailRowAsync(rowId);
        status.Should().Be("pending",
            "MarkSent called as tenant B must not flip tenant A's row to 'sent'");
    }

    [Test]
    public async Task MarkFailedAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        var updated = await emailRepo.MarkFailedAsync(
            TenantB, rowId, "boom", TimeSpan.FromMinutes(5));

        updated.Should().BeNull(
            "MarkFailed called as tenant B must not return tenant A's row");

        var (status, _) = await ReadEmailRowAsync(rowId);
        status.Should().Be("pending",
            "the row must still be in its original state — no cross-tenant write");
    }

    [Test]
    public async Task GetByIdAsync_ReturnsNull_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        var fetched = await emailRepo.GetByIdAsync(TenantB, rowId);

        fetched.Should().BeNull(
            "GetById must NOT leak tenant A's row to tenant B's call");
    }

    [Test]
    public async Task DeleteAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedEmailRowAsync(TenantA, rowId);
        var (emailRepo, _, _) = BuildRepos();

        await emailRepo.DeleteAsync(TenantB, rowId);

        var (status, tid) = await ReadEmailRowAsync(rowId);
        status.Should().Be("pending",
            "the row must still exist after a wrong-tenant delete");
        tid.Should().Be(TenantA);
    }

    // ── QueuedTaskRepository — every tenantId-bearing method must
    //    refuse to act on a wrong-tenant row ──────────────────────────

    [Test]
    public async Task QueuedTask_GetAsync_ReturnsNull_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId);
        var (_, taskRepo, _) = BuildRepos();

        var fetched = await taskRepo.GetAsync(TenantB, rowId);

        fetched.Should().BeNull(
            "GetAsync must NOT leak tenant A's task to tenant B's call");
    }

    [Test]
    public async Task QueuedTask_MarkProcessingAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId);
        var (_, taskRepo, _) = BuildRepos();

        var claimed = await taskRepo.MarkProcessingAsync(TenantB, rowId);

        claimed.Should().BeNull(
            "MarkProcessing called as tenant B must not flip tenant A's row");

        var (status, _, _) = await ReadTaskRowAsync(rowId);
        status.Should().Be("pending");
    }

    [Test]
    public async Task QueuedTask_MarkCompletedAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId, status: "processing");
        var (_, taskRepo, _) = BuildRepos();

        await taskRepo.MarkCompletedAsync(TenantB, rowId);

        var (status, _, _) = await ReadTaskRowAsync(rowId);
        status.Should().Be("processing",
            "MarkCompleted called as tenant B must not flip tenant A's row to 'completed'");
    }

    [Test]
    public async Task QueuedTask_MarkFailedAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId, status: "processing");
        var (_, taskRepo, _) = BuildRepos();

        await taskRepo.MarkFailedAsync(TenantB, rowId, "boom");

        var (status, _, _) = await ReadTaskRowAsync(rowId);
        status.Should().Be("processing",
            "MarkFailed called as tenant B must not flip tenant A's row to 'failed'");
    }

    [Test]
    public async Task QueuedTask_IncrementRetryAndRequeueAsync_IsNoop_WhenCalledWithWrongTenantId()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId, status: "processing");
        var (_, taskRepo, _) = BuildRepos();

        var requeued = await taskRepo.IncrementRetryAndRequeueAsync(TenantB, rowId, "transient");

        requeued.Should().BeNull(
            "IncrementRetryAndRequeue called as tenant B must not return tenant A's row");

        var (status, _, retryCount) = await ReadTaskRowAsync(rowId);
        status.Should().Be("processing",
            "tenant A's row status must NOT have flipped to 'pending'");
        retryCount.Should().Be(0,
            "tenant A's RetryCount must NOT have incremented from a wrong-tenant call");
    }

    // ── Positive controls — own-tenant calls still work ───────────────

    [Test]
    public async Task QueuedTask_MarkProcessingAsync_Succeeds_ForOwnTenantsRow()
    {
        var rowId = Guid.NewGuid();
        await SeedTaskRowAsync(TenantA, rowId);
        var (_, taskRepo, _) = BuildRepos();

        var claimed = await taskRepo.MarkProcessingAsync(TenantA, rowId);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be("processing");

        var (status, _, _) = await ReadTaskRowAsync(rowId);
        status.Should().Be("processing");
    }
}

/// <summary>
/// Stripped <see cref="ControlPlaneDbContext"/> for the cross-tenant
/// isolation fixture — the production CP model includes 30+ entities and
/// would require the full migration bundle. We only need the
/// <c>Tenants</c> set so the repositories' fan-out helpers can list active
/// tenants.
/// </summary>
internal sealed class MinimalCpDbContext : ControlPlaneDbContext
{
    public MinimalCpDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Do NOT call base — we want a minimal model. Map only the
        // <c>Tenants</c> table with the columns the fan-out helpers
        // actually read (Id + DeletedAt). Ignore neighbour entities the
        // production Tenant entity references via navigations, otherwise
        // EF will pick them up and demand columns the fixture's stub
        // table doesn't have.
        modelBuilder.Ignore<Tamma.Data.Entities.User>();
        modelBuilder.Ignore<Tamma.Data.Entities.TenantMembership>();
        modelBuilder.Ignore<Tamma.Data.Entities.UserInvite>();
        modelBuilder.Ignore<Tamma.Data.Entities.RefreshToken>();
        modelBuilder.Ignore<Tamma.Data.Entities.PasswordResetToken>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubInstallation>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubInstallationRepo>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubWebhookDelivery>();
        modelBuilder.Ignore<Tamma.Data.Entities.Plan>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformEvent>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformQueuedTask>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformEmailOutboxMessage>();
        modelBuilder.Ignore<Tamma.Data.Entities.ApiKey>();

        modelBuilder.Entity<Tamma.Data.Entities.Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("Id");
            b.Property(t => t.DeletedAt).HasColumnName("DeletedAt");
            // Ignore every other property the production Tenant entity
            // carries — the fixture's stub "Tenants" table has only Id +
            // DeletedAt, and the fan-out paths under test only read those.
            b.Ignore(t => t.Name);
            b.Ignore(t => t.Slug);
            b.Ignore(t => t.Type);
            b.Ignore(t => t.OwnerId);
            b.Ignore(t => t.ExternalId);
            b.Ignore(t => t.Plan);
            b.Ignore(t => t.Settings);
            b.Ignore(t => t.CreatedAt);
            b.Ignore(t => t.UpdatedAt);
            // Epic 30 Phase B (Task B3): the six dedicated Cranl columns were
            // dropped from the Tenant entity, so there is nothing to Ignore.
            b.Ignore(t => t.ProvisioningState);
            b.Ignore(t => t.ProvisioningDetail);
            b.Ignore(t => t.ProvisioningUpdatedAt);
            b.Ignore(t => t.Owner);
            b.Ignore(t => t.Memberships);
            b.Ignore(t => t.Invites);
        });

        // The repos under test reference <c>EmailOutbox</c> /
        // <c>QueuedTasks</c> on the CP context only via the fan-out
        // path's <c>cp.Tenants</c> read. They don't query EmailOutbox /
        // QueuedTasks via the CP context directly. Map them defensively
        // so the model graph compiles, but no test relies on these.
        modelBuilder.Entity<Tamma.Data.Entities.EmailOutboxMessage>(b =>
        {
            b.ToTable("email_outbox");
            b.HasKey(e => e.Id);
        });
        modelBuilder.Entity<Tamma.Data.Entities.QueuedTask>(b =>
        {
            b.ToTable("queued_tasks");
            b.HasKey(e => e.Id);
        });
    }
}

/// <summary>
/// <see cref="ITenantDbContextFactory"/> that hands every tenant a fresh
/// <see cref="TenantDbContext"/> bound to the same shared connection. That
/// matches the transitional shared-DB topology (Story 28-1 PR B's stated
/// constraint): all tenants ride one Postgres database, isolation is
/// supposed to come from the repository predicates we just hardened.
/// </summary>
internal sealed class SharedConnectionTenantFactory : ITenantDbContextFactory
{
    private readonly DbContextOptions<TenantDbContext> _options;

    public SharedConnectionTenantFactory(DbContextOptions<TenantDbContext> options)
    {
        _options = options;
    }

    public ValueTask<TenantDbContext> CreateAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        return ValueTask.FromResult<TenantDbContext>(
            new MinimalTenantDbContext(_options, tenantId));
    }
}

/// <summary>
/// Minimal <see cref="TenantDbContext"/> that maps <c>email_outbox</c> +
/// <c>queued_tasks</c> against the shared Postgres test container. The
/// production tenant model graph references mentorship + workflow + agent
/// entities the test schema doesn't have; this slim variant lets the EF
/// model compile against the two tables under test.
/// </summary>
internal sealed class MinimalTenantDbContext : TenantDbContext
{
    public MinimalTenantDbContext(DbContextOptions<TenantDbContext> options, Guid tenantId)
        : base(options, tenantId) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Skip base — the production tenant model maps a full graph
        // (agent_configs, prompt_overrides, etc) that doesn't exist on
        // the fixture. Map only the two tables the repos under test
        // touch. Defensive ignores prevent EF from auto-discovering
        // adjacent entities through navigation properties on QueuedTask
        // / EmailOutboxMessage.
        modelBuilder.Ignore<Tamma.Data.Entities.User>();
        modelBuilder.Ignore<Tamma.Data.Entities.Tenant>();
        modelBuilder.Ignore<Tamma.Data.Entities.TenantMembership>();
        modelBuilder.Ignore<Tamma.Data.Entities.UserInvite>();
        modelBuilder.Ignore<Tamma.Data.Entities.RefreshToken>();
        modelBuilder.Ignore<Tamma.Data.Entities.PasswordResetToken>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubInstallation>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubInstallationRepo>();
        modelBuilder.Ignore<Tamma.Data.Entities.GitHubWebhookDelivery>();
        modelBuilder.Ignore<Tamma.Data.Entities.Plan>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformEvent>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformQueuedTask>();
        modelBuilder.Ignore<Tamma.Data.Entities.PlatformEmailOutboxMessage>();
        modelBuilder.Ignore<Tamma.Data.Entities.ApiKey>();

        modelBuilder.Entity<Tamma.Data.Entities.EmailOutboxMessage>(b =>
        {
            b.ToTable("email_outbox");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("Id");
            b.Property(e => e.TenantId).HasColumnName("TenantId");
            b.Property(e => e.UserId).HasColumnName("UserId");
            b.Property(e => e.Template).HasColumnName("Template");
            b.Property(e => e.ToAddress).HasColumnName("ToAddress");
            b.Property(e => e.Subject).HasColumnName("Subject");
            b.Property(e => e.HtmlBody).HasColumnName("HtmlBody");
            b.Property(e => e.TextBody).HasColumnName("TextBody");
            b.Property(e => e.FromAddress).HasColumnName("FromAddress");
            b.Property(e => e.Status).HasColumnName("Status");
            b.Property(e => e.Attempts).HasColumnName("Attempts");
            b.Property(e => e.MaxAttempts).HasColumnName("MaxAttempts");
            b.Property(e => e.NextAttemptAt).HasColumnName("NextAttemptAt");
            b.Property(e => e.LastError).HasColumnName("LastError");
            b.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
            b.Property(e => e.UpdatedAt).HasColumnName("UpdatedAt");
            b.Property(e => e.SentAt).HasColumnName("SentAt");
        });
        modelBuilder.Entity<Tamma.Data.Entities.QueuedTask>(b =>
        {
            b.ToTable("queued_tasks");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("Id");
            b.Property(e => e.Type).HasColumnName("Type");
            b.Property(e => e.TenantId).HasColumnName("TenantId");
            b.Property(e => e.InstallationId).HasColumnName("InstallationId");
            b.Property(e => e.Payload).HasColumnName("Payload").HasColumnType("jsonb");
            b.Property(e => e.Status).HasColumnName("Status");
            b.Property(e => e.Error).HasColumnName("Error");
            b.Property(e => e.RetryCount).HasColumnName("RetryCount");
            b.Property(e => e.ClaimedAt).HasColumnName("ClaimedAt");
            b.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
            b.Property(e => e.UpdatedAt).HasColumnName("UpdatedAt");
        });
    }
}
