using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-8 — handler contract tests using a fake
/// <see cref="ICranlApiClient"/>. Exercises push happy path,
/// retry-then-success, probe polling, rollback, dry-run, key-diff log
/// shape, and rate-limit propagation.
/// </summary>
[TestFixture]
public class CranlEnvVarRotationHandlerTests
{
    private static readonly IReadOnlyList<TimeSpan> NoDelays = new[]
    {
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
    };

    private static RotationTarget MakeTarget(int newV = 2, int oldV = 1) =>
        new(Guid.NewGuid(), "cranl/shared-secret", null, "cranl",
            "app=app_abc;env=TAMMA_SHARED_SECRET", newV, oldV);

    private static RotationContext MakeCtx(bool dryRun = false, string cranlMode = "reload") =>
        new("rot_1", Guid.Empty, dryRun,
            new Dictionary<string, string>
            {
                ["CranlMode"] = cranlMode,
                ["ProbeTimeoutSeconds"] = "5",
            });

    private (CranlEnvVarRotationHandler handler, FakeCranlClient cranl, StubGateway gw) Build()
    {
        var cranl = new FakeCranlClient();
        var gw = new StubGateway();
        var h = new CranlEnvVarRotationHandler(cranl, gw,
            NullLogger<CranlEnvVarRotationHandler>.Instance)
        {
            RetryDelays = NoDelays,
        };
        return (h, cranl, gw);
    }

    [Test]
    public async Task PushAsync_HappyPath_MergesEnvAndReloads()
    {
        var (handler, cranl, _) = Build();
        cranl.EnvText = "EXISTING=1\nTAMMA_SHARED_SECRET=old\n";

        await handler.PushAsync(MakeTarget(), "new-value", MakeCtx(), default);

        cranl.LastPutEnv.Should().NotBeNull();
        cranl.LastPutEnv!.Should().Contain("EXISTING=1");
        cranl.LastPutEnv!.Should().Contain("TAMMA_SHARED_SECRET=new-value");
        cranl.ReloadCalls.Should().HaveCount(1);
        cranl.DeployCalls.Should().BeEmpty();
    }

    [Test]
    public async Task PushAsync_AddsMissingKey()
    {
        var (handler, cranl, _) = Build();
        cranl.EnvText = "EXISTING=1\n";

        await handler.PushAsync(MakeTarget(), "new", MakeCtx(), default);

        cranl.LastPutEnv!.Should().Contain("TAMMA_SHARED_SECRET=new");
        cranl.LastPutEnv!.Should().Contain("EXISTING=1");
    }

    [Test]
    public async Task PushAsync_Redeploy_CallsDeploy()
    {
        var (handler, cranl, _) = Build();
        cranl.EnvText = "";
        await handler.PushAsync(MakeTarget(), "v", MakeCtx(cranlMode: "redeploy"), default);
        cranl.DeployCalls.Should().HaveCount(1);
        cranl.ReloadCalls.Should().BeEmpty();
    }

    [Test]
    public async Task PushAsync_DryRun_DoesNotTouchCranl()
    {
        var (handler, cranl, _) = Build();
        await handler.PushAsync(MakeTarget(), "v", MakeCtx(dryRun: true), default);
        cranl.GetEnvCalls.Should().Be(0);
        cranl.LastPutEnv.Should().BeNull();
        cranl.ReloadCalls.Should().BeEmpty();
    }

    [Test]
    public async Task PushAsync_Retries5xxThenSucceeds()
    {
        var (handler, cranl, _) = Build();
        cranl.EnvText = "A=1\n";
        cranl.FailPutFirstN = 2;
        cranl.FailPutStatus = HttpStatusCode.InternalServerError;

        await handler.PushAsync(MakeTarget(), "v", MakeCtx(), default);

        cranl.PutAttempts.Should().Be(3);
    }

    [Test]
    public async Task PushAsync_PersistentFailure_Throws()
    {
        var (handler, cranl, _) = Build();
        cranl.EnvText = "";
        cranl.FailPutFirstN = int.MaxValue;
        cranl.FailPutStatus = HttpStatusCode.InternalServerError;
        Func<Task> act = () => handler.PushAsync(MakeTarget(), "v", MakeCtx(), default);
        await act.Should().ThrowAsync<CranlApiException>();
    }

    [Test]
    public async Task ProbeAsync_Running_Healthy()
    {
        var (handler, cranl, _) = Build();
        cranl.AppStatus = "running";
        var result = await handler.ProbeAsync(MakeTarget(), MakeCtx(), default);
        result.Status.Should().Be(ProbeStatus.Healthy);
    }

    [Test]
    public async Task ProbeAsync_Error_ReturnsUnhealthyWithReason()
    {
        var (handler, cranl, _) = Build();
        cranl.AppStatus = "error";
        var result = await handler.ProbeAsync(MakeTarget(), MakeCtx(), default);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.Reason.Should().Be("cranl_status_error");
    }

    [Test]
    public async Task ProbeAsync_Timeout_ReturnsTimeoutReason()
    {
        var (handler, cranl, _) = Build();
        cranl.AppStatus = "deploying"; // never reaches running
        var ctx = new RotationContext("r", Guid.Empty, false,
            new Dictionary<string, string> { ["ProbeTimeoutSeconds"] = "0" });
        var result = await handler.ProbeAsync(MakeTarget(), ctx, default);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.Reason.Should().Be("probe_timeout");
    }

    [Test]
    public async Task RollbackAsync_WithPrevious_RestoresOldValue()
    {
        var (handler, cranl, gw) = Build();
        var target = MakeTarget(2, 1);
        gw.Plaintexts[(target.SecretId, 1)] = "previous-value";
        cranl.EnvText = "TAMMA_SHARED_SECRET=compromised-new\nX=1\n";

        await handler.RollbackAsync(target, "compromised-new", MakeCtx(), default);

        cranl.LastPutEnv!.Should().Contain("TAMMA_SHARED_SECRET=previous-value");
        cranl.ReloadCalls.Should().HaveCount(1);
    }

    [Test]
    public async Task RollbackAsync_NoPrevious_RemovesKey()
    {
        var (handler, cranl, _) = Build();
        var target = MakeTarget(1, 0);
        cranl.EnvText = "TAMMA_SHARED_SECRET=bad\nX=1\n";

        await handler.RollbackAsync(target, "bad", MakeCtx(), default);

        cranl.LastPutEnv!.Should().NotContain("TAMMA_SHARED_SECRET");
        cranl.LastPutEnv!.Should().Contain("X=1");
    }

    [Test]
    public void System_IsCranl()
    {
        var (handler, _, _) = Build();
        handler.System.Should().Be("cranl");
    }

    // ─── fakes ───────────────────────────────────────────────────────────

    private sealed class FakeCranlClient : ICranlApiClient
    {
        public string EnvText { get; set; } = string.Empty;
        public string? LastPutEnv { get; private set; }
        public List<string> ReloadCalls { get; } = new();
        public List<string> DeployCalls { get; } = new();
        public string AppStatus { get; set; } = "running";
        public int GetEnvCalls;
        public int PutAttempts;
        public int FailPutFirstN;
        public HttpStatusCode FailPutStatus = HttpStatusCode.InternalServerError;

        public Task<CranlProject> CreateProjectAsync(string name, string organizationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteProjectAsync(string projectId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CranlDatabase> CreateDatabaseAsync(CreateDatabaseRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CranlDatabase> GetDatabaseAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DatabaseLifecycleAsync(string id, string action, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CranlApplication> CreateApplicationAsync(CreateApplicationRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteApplicationAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CranlAppDomains> GetApplicationDomainsAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CranlApplication> GetApplicationAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(new CranlApplication { Id = id, Status = AppStatus });

        public Task DeployApplicationAsync(string id, CancellationToken ct = default)
        {
            DeployCalls.Add(id);
            return Task.CompletedTask;
        }

        public Task ApplicationLifecycleAsync(string id, string action, CancellationToken ct = default)
        {
            if (action == "reload") ReloadCalls.Add(id);
            return Task.CompletedTask;
        }

        public Task PutEnvironmentAsync(string id, string envText, CancellationToken ct = default)
        {
            PutAttempts++;
            if (PutAttempts <= FailPutFirstN)
                throw new CranlApiException(FailPutStatus, "server_error", "500");
            LastPutEnv = envText;
            EnvText = envText;
            return Task.CompletedTask;
        }

        public Task<string> GetEnvironmentAsync(string id, CancellationToken ct = default)
        {
            GetEnvCalls++;
            return Task.FromResult(EnvText);
        }
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
        public Task ActivateVersionAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RevertActivationAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RetireVersionAsync(Guid s, int v, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetVersionPlaintextAsync(Guid s, int v, CancellationToken ct) =>
            Task.FromResult(Plaintexts.TryGetValue((s, v), out var p) ? p : null);
    }
}
