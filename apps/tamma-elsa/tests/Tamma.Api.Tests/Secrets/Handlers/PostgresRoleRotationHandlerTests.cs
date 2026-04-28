using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-7 — unit tests for
/// <see cref="PostgresRoleRotationHandler"/> using a fake
/// <see cref="IPostgresRotationExecutor"/> + an in-memory gateway.
/// Integration tests against Testcontainers live in a separate suite.
/// </summary>
[TestFixture]
public class PostgresRoleRotationHandlerTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TammaAdmin"] =
                    "Host=localhost;Port=5432;Username=admin;Password=adminpw;Database=tamma_control",
            })
            .Build();

    private static RotationTarget PlatformTarget(int newV, int oldV) =>
        new(Guid.NewGuid(), "db/app-role", null, "postgres", "role=tamma_app;db=tamma_control", newV, oldV);

    private static RotationContext Ctx() => RotationContext.ForCorrelation("rot_test");

    [Test]
    public async Task PushAsync_SafePassword_ExecutesAlterRole()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);

        var pw = PostgresPasswordGenerator.Generate();
        await handler.PushAsync(PlatformTarget(2, 1), pw, Ctx(), default);

        exec.AlterCalls.Should().HaveCount(1);
        exec.AlterCalls[0].role.Should().Be("tamma_app");
        exec.AlterCalls[0].password.Should().Be(pw);
    }

    [Test]
    public async Task PushAsync_DryRun_DoesNotCallExecutor()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);

        var pw = PostgresPasswordGenerator.Generate();
        var ctx = new RotationContext("rot_test", Guid.Empty, DryRun: true,
            new Dictionary<string, string>());
        await handler.PushAsync(PlatformTarget(2, 1), pw, ctx, default);

        exec.AlterCalls.Should().BeEmpty();
    }

    [Test]
    public async Task PushAsync_UnsafePassword_Throws()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        Func<Task> act = () =>
            handler.PushAsync(PlatformTarget(2, 1), "bad'pw", Ctx(), default);
        await act.Should().ThrowAsync<ArgumentException>();
        exec.AlterCalls.Should().BeEmpty();
    }

    [Test]
    public async Task PushAsync_NonWhitelistedRole_Throws()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "postgres",
            "role=postgres;db=tamma_control", 2, 1);
        var pw = PostgresPasswordGenerator.Generate();
        Func<Task> act = () => handler.PushAsync(target, pw, Ctx(), default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*whitelist*");
    }

    [Test]
    public async Task ProbeAsync_Healthy_WhenSelectOneSucceeds()
    {
        var exec = new FakeExecutor { ProbeReturnMs = 42 };
        var gateway = new StubGateway();
        var target = PlatformTarget(2, 1);
        gateway.Plaintexts[(target.SecretId, 2)] = PostgresPasswordGenerator.Generate();

        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var result = await handler.ProbeAsync(target, Ctx(), default);
        result.Status.Should().Be(ProbeStatus.Healthy);
        result.DurationMs.Should().Be(42);
    }

    [Test]
    public async Task ProbeAsync_Unhealthy_WhenProbeThrows()
    {
        var exec = new FakeExecutor { ProbeException = new InvalidOperationException("auth failed") };
        var gateway = new StubGateway();
        var target = PlatformTarget(2, 1);
        gateway.Plaintexts[(target.SecretId, 2)] = PostgresPasswordGenerator.Generate();

        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var result = await handler.ProbeAsync(target, Ctx(), default);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.Reason.Should().Be("InvalidOperationException");
    }

    [Test]
    public async Task ProbeAsync_MissingPlaintext_Unhealthy()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway(); // no plaintext stored
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var result = await handler.ProbeAsync(PlatformTarget(2, 1), Ctx(), default);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.Reason.Should().Be("new_plaintext_missing");
    }

    [Test]
    public async Task RollbackAsync_WithPrevious_RestoresOldPassword()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var target = PlatformTarget(2, 1);
        var oldPw = PostgresPasswordGenerator.Generate();
        gateway.Plaintexts[(target.SecretId, 1)] = oldPw;

        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var newPw = PostgresPasswordGenerator.Generate();
        await handler.RollbackAsync(target, newPw, Ctx(), default);

        exec.AlterCalls.Should().HaveCount(1);
        exec.AlterCalls[0].password.Should().Be(oldPw);
        exec.NullCalls.Should().BeEmpty();
    }

    [Test]
    public async Task RollbackAsync_NoPrevious_DisablesRolePassword()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var target = PlatformTarget(1, 0); // first rotation → no previous

        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var newPw = PostgresPasswordGenerator.Generate();
        await handler.RollbackAsync(target, newPw, Ctx(), default);

        exec.NullCalls.Should().HaveCount(1);
        exec.AlterCalls.Should().BeEmpty();
    }

    [Test]
    public async Task RevokeOldAsync_InvokesPoolDrain()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var handler = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        var oldPw = PostgresPasswordGenerator.Generate();
        await handler.RevokeOldAsync(PlatformTarget(2, 1), oldPw, Ctx(), default);
        exec.DrainCalls.Should().HaveCount(1);
    }

    [Test]
    public void System_ExposesPostgresKey()
    {
        var exec = new FakeExecutor();
        var gateway = new StubGateway();
        var h = new PostgresRoleRotationHandler(exec, gateway, BuildConfig(),
            NullLogger<PostgresRoleRotationHandler>.Instance);
        h.System.Should().Be("postgres");
    }

    [Test]
    public void BuildProbeConnectionString_OverridesCredentialsAndDb()
    {
        var admin = "Host=localhost;Port=5432;Username=admin;Password=adminpw;Database=other";
        var parsed = new PostgresConsumerIdentifier("tamma_app", "tamma_control");
        var cs = PostgresRoleRotationHandler.BuildProbeConnectionString(admin, parsed, "newpw");
        cs.Should().Contain("Username=tamma_app");
        cs.Should().Contain("Password=newpw");
        cs.Should().Contain("Database=tamma_control");
        cs.Should().Contain("Host=localhost");
    }

    // ─── fakes ───────────────────────────────────────────────────────────

    private sealed class FakeExecutor : IPostgresRotationExecutor
    {
        public List<(string cs, string role, string password)> AlterCalls { get; } = new();
        public List<(string cs, string role)> NullCalls { get; } = new();
        public List<string> DrainCalls { get; } = new();
        public long ProbeReturnMs { get; set; } = 10;
        public Exception? ProbeException { get; set; }

        public Task AlterRolePasswordAsync(string adminCs, string role, string pw, CancellationToken ct)
        {
            AlterCalls.Add((adminCs, role, pw));
            return Task.CompletedTask;
        }

        public Task SetRolePasswordNullAsync(string adminCs, string role, CancellationToken ct)
        {
            NullCalls.Add((adminCs, role));
            return Task.CompletedTask;
        }

        public Task<long> ProbeRoleAsync(string probeCs, CancellationToken ct)
        {
            if (ProbeException is not null) throw ProbeException;
            return Task.FromResult(ProbeReturnMs);
        }

        public void DrainPool(string connectionString) => DrainCalls.Add(connectionString);
    }

    private sealed class StubGateway : ISecretRotationGateway
    {
        public Dictionary<(Guid, int), string> Plaintexts { get; } = new();

        public Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult<SecretRotationSnapshot?>(null);

        public Task<int> MintPendingVersionAsync(Guid secretId, string newPlaintext,
            string rotationCorrelationId, Guid operatorUserId, CancellationToken ct) =>
            Task.FromResult(1);

        public Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ActivateVersionAsync(Guid secretId, int newV, int prevV, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RevertActivationAsync(Guid secretId, int newV, int prevV, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RetireVersionAsync(Guid secretId, int v, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetVersionPlaintextAsync(Guid secretId, int v, CancellationToken ct) =>
            Task.FromResult(Plaintexts.TryGetValue((secretId, v), out var p) ? p : null);
    }
}
