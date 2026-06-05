using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 AC4 — unit tests for the pre-drop pg_dump backup. The
/// activity must be a pure no-op when the gate is off, must skip cleanly
/// when there is nothing to back up, must invoke pg_dump correctly when
/// enabled, and must NEVER place the password on the command line.
/// </summary>
[TestFixture]
public class BackupTenantDatabaseActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task BackupAsync_Disabled_IsNoOp()
    {
        var runner = new RecordingProcessRunner();
        var admin = new FakeAdmin { DatabaseExists = true };
        var options = new TenantBackupOptions { DeletionBackup = false };

        var produced = await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, Tenant, DateTime.UtcNow, null, CancellationToken.None);

        produced.Should().BeFalse();
        runner.LastRequest.Should().BeNull("disabled backup must never spawn pg_dump");
        admin.DatabaseExistsCalls.Should().Be(0, "disabled backup must not even probe the DB");
    }

    [Test]
    public async Task BackupAsync_DatabaseMissing_SkipsDump()
    {
        var runner = new RecordingProcessRunner();
        var admin = new FakeAdmin { DatabaseExists = false };
        var options = NewEnabledOptions();

        var produced = await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, Tenant, DateTime.UtcNow, null, CancellationToken.None);

        produced.Should().BeFalse();
        runner.LastRequest.Should().BeNull("nothing to dump when the DB is already gone");
    }

    [Test]
    public async Task BackupAsync_Enabled_InvokesPgDump_PasswordOnlyInEnvironment()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "", "", false, 1),
        };
        var admin = new FakeAdmin
        {
            DatabaseExists = true,
            Info = new TenantAdminConnectionInfo(
                Host: "db.internal", Port: 6432, Username: "tamma_provisioner",
                Password: "super-secret-pw", Database: "placeholder"),
        };
        var options = NewEnabledOptions();
        options.PgDumpPath = "pg_dump";

        var produced = await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, Tenant, DateTime.UtcNow, null, CancellationToken.None);

        produced.Should().BeTrue();
        var req = runner.LastRequest!;
        req.FileName.Should().Be("pg_dump");

        var dbName = TenantNaming.DatabaseName(Tenant);
        req.Arguments.Should().Contain("--dbname");
        req.Arguments.Should().Contain(dbName, "the activity passes the resolved tenant DB name");
        req.Arguments.Should().Contain(new[] { "--host", "db.internal" });
        req.Arguments.Should().Contain(new[] { "--port", "6432" });

        // The critical assertion: the password must NEVER appear in argv.
        req.Arguments.Should().NotContain("super-secret-pw");
        req.Arguments.Should().NotContain(a => a.Contains("super-secret-pw"));

        // …it travels via PGPASSWORD instead.
        req.EnvironmentOverrides.Should().NotBeNull();
        req.EnvironmentOverrides!["PGPASSWORD"].Should().Be("super-secret-pw");
    }

    [Test]
    public async Task BackupAsync_NonZeroExit_Throws()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(1, "", "permission denied", false, 1),
        };
        var admin = new FakeAdmin { DatabaseExists = true };
        var options = NewEnabledOptions();

        var act = async () => await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, Tenant, DateTime.UtcNow, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pg_dump failed*");
    }

    [Test]
    public async Task BackupAsync_TimedOut_Throws()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(-1, "", "", true, 999),
        };
        var admin = new FakeAdmin { DatabaseExists = true };
        var options = NewEnabledOptions();

        var act = async () => await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, Tenant, DateTime.UtcNow, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*timed out*");
    }

    private static TenantBackupOptions NewEnabledOptions() => new()
    {
        DeletionBackup = true,
        Directory = Path.Combine(Path.GetTempPath(), "tamma-backup-test-" + Guid.NewGuid().ToString("N")),
    };

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public ProcessRunRequest? LastRequest { get; private set; }
        public ProcessRunResult Result { get; set; } = new(0, "", "", false, 0);

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAdmin : ITenantAdminConnection
    {
        public bool DatabaseExists { get; set; }
        public int DatabaseExistsCalls { get; private set; }
        public TenantAdminConnectionInfo Info { get; set; } = new(
            "localhost", 5432, "tamma_provisioner", "pw", "placeholder");

        public Task<bool> RoleExistsAsync(string roleName, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default)
        {
            DatabaseExistsCalls++;
            return Task.FromResult(DatabaseExists);
        }

        public Task<int> ExecuteAsync(string commandText, CancellationToken ct = default)
            => Task.FromResult(0);

        public string BuildTenantConnectionString(string databaseName, string roleName, string password)
            => $"Host={Info.Host};Database={databaseName}";

        public TenantAdminConnectionInfo GetConnectionInfo(string databaseName)
            => Info with { Database = databaseName };
    }
}
