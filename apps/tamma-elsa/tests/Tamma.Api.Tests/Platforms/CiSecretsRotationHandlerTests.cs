using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-8 — <see cref="CiSecretsRotationHandler"/> tests. The
/// handler bridges Epic 29's rotation saga to Epic 31's CI provisioner;
/// these tests validate:
/// <list type="bullet">
///   <item>Tenant scoping (platform-scoped secrets are rejected to
///         prevent cross-tenant leak).</item>
///   <item>Consumer-identifier JSON parsing.</item>
///   <item>Capability gating: only platforms advertising
///         <see cref="PlatformCapability.Secrets"/> are reached.</item>
///   <item>Audit-event emission per result.</item>
///   <item>Multi-platform fan-out.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class CiSecretsRotationHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string Correlation = "rotation-correlation-id-42";

    /// <summary>Stub provisioner that records every call.</summary>
    private sealed class StubProvisioner : ICiSecretsProvisioner
    {
        public PlatformKind Kind { get; }
        public List<(CiSecretScope Scope, IReadOnlyList<CiSecretTarget> Targets,
            string Name, string Plaintext)> Calls { get; } = new();
        public bool ThrowOnRotate { get; set; }
        public bool AllFail { get; set; }

        public StubProvisioner(PlatformKind kind) { Kind = kind; }

        public Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
            CiSecretScope scope, IReadOnlyList<CiSecretTarget> targets,
            string secretName, RedactedSecret secretValue,
            CiSecretMetadata? metadata = null, CancellationToken ct = default)
        {
            Calls.Add((scope, targets, secretName, secretValue.Reveal()));
            return Task.FromResult<IReadOnlyList<CiSecretProvisionResult>>(
                targets.Select(t => CiSecretProvisionResult.Ok(Kind, t)).ToArray());
        }

        public Task<IReadOnlyList<CiSecretProvisionResult>> RotateSecretAsync(
            CiSecretScope scope, IReadOnlyList<CiSecretTarget> targets,
            string secretName, RedactedSecret newValue,
            CiSecretMetadata? metadata = null, CancellationToken ct = default)
        {
            if (ThrowOnRotate) throw new InvalidOperationException("simulated");
            Calls.Add((scope, targets, secretName, newValue.Reveal()));
            return Task.FromResult<IReadOnlyList<CiSecretProvisionResult>>(
                targets.Select(t => AllFail
                    ? CiSecretProvisionResult.Failed(Kind, t, "unknown:simulated")
                    : CiSecretProvisionResult.Ok(Kind, t))
                .ToArray());
        }

        public Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
            CiSecretScope scope, IReadOnlyList<CiSecretTarget> targets,
            string secretName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CiSecretProvisionResult>>(
                targets.Select(t => CiSecretProvisionResult.Ok(Kind, t)).ToArray());

        public Task<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>> ListSecretsAsync(
            CiSecretScope scope, CiSecretTarget target,
            CancellationToken ct = default) =>
            Task.FromResult(PlatformResult<IReadOnlyList<CiSecretMetadataItem>>
                .FromOk(Array.Empty<CiSecretMetadataItem>()));
    }

    private sealed class StubDriver : IGitPlatformDriver
    {
        public PlatformKind Kind { get; init; }
        public IGitPlatformClient Client { get; } =
            new NullGitPlatformDriver().Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; init; }
            = new HashSet<PlatformCapability>();
        public ICiSecretsProvisioner? CiSecrets { get; init; }
    }

    private static string IdentifierJson(
        string secretName = "MY_TOKEN",
        string scope = "Repo",
        string owner = "acme",
        string repo = "app") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            secretName,
            scope,
            targets = new[]
            {
                new { kind = "Repo", owner, repo },
            },
        });

    // ── Refuses platform-scoped secrets (cross-tenant safety) ─────────

    [Test]
    public async Task PushAsync_PlatformScopedSecret_Throws()
    {
        var resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        var auditor = new Mock<IRotationAuditEmitter>(MockBehavior.Loose);
        var handler = new CiSecretsRotationHandler(
            resolver.Object, auditor.Object,
            NullLogger<CiSecretsRotationHandler>.Instance);

        var target = new RotationTarget(
            SecretId: Guid.NewGuid(),
            Name: "k",
            TenantId: null,  // platform-scoped
            ConsumerSystem: "ci-secrets",
            ConsumerIdentifier: IdentifierJson(),
            NewVersionNumber: 2,
            PreviousVersionNumber: 1);

        Func<Task> act = () => handler.PushAsync(
            target, "newsecret",
            RotationContext.ForCorrelation(Correlation), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*platform-scoped*tenant*");
    }

    // ── Capability gating: only Secrets-capable drivers are reached ───

    [Test]
    public async Task PushAsync_OnlySecretsCapableDriversAreInvoked()
    {
        // Tenant has TWO installations: GitHub (Secrets capable) and
        // a hypothetical PlainGit (no Secrets capability). Only the
        // GitHub provisioner should be invoked.
        var ghProv = new StubProvisioner(PlatformKind.GitHub);
        var plainProv = new StubProvisioner(PlatformKind.GitHub); // pretend; no caps

        var ghDriver = new StubDriver
        {
            Kind = PlatformKind.GitHub,
            Capabilities = new HashSet<PlatformCapability>
                { PlatformCapability.Secrets, PlatformCapability.LibsodiumSecrets },
            CiSecrets = ghProv,
        };
        var plainDriver = new StubDriver
        {
            Kind = PlatformKind.Bitbucket,
            Capabilities = new HashSet<PlatformCapability>(),  // no Secrets
            CiSecrets = plainProv,
        };

        var resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ListForTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PlatformInstallation(
                    Guid.NewGuid(), TenantId, PlatformKind.GitHub, "https://api.github.com", "1"),
                new PlatformInstallation(
                    Guid.NewGuid(), TenantId, PlatformKind.Bitbucket, "https://bitbucket.org", "2"),
            });
        resolver.Setup(r => r.ResolveForTenantAsync(
                TenantId, PlatformKind.GitHub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ghDriver);
        resolver.Setup(r => r.ResolveForTenantAsync(
                TenantId, PlatformKind.Bitbucket, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plainDriver);

        var auditor = new Mock<IRotationAuditEmitter>(MockBehavior.Loose);
        var handler = new CiSecretsRotationHandler(
            resolver.Object, auditor.Object,
            NullLogger<CiSecretsRotationHandler>.Instance);

        var target = new RotationTarget(
            Guid.NewGuid(), "k", TenantId,
            "ci-secrets", IdentifierJson(),
            NewVersionNumber: 2, PreviousVersionNumber: 1);

        await handler.PushAsync(
            target, "newvalue",
            RotationContext.ForCorrelation(Correlation), CancellationToken.None);

        ghProv.Calls.Should().HaveCount(1, "Secrets-capable driver was invoked");
        plainProv.Calls.Should().BeEmpty(
            "Driver without Secrets capability must not be reached");
    }

    // ── Multi-platform fan-out ────────────────────────────────────────

    [Test]
    public async Task PushAsync_MultiplePlatforms_AllReceiveRotation()
    {
        var ghProv = new StubProvisioner(PlatformKind.GitHub);
        var glProv = new StubProvisioner(PlatformKind.GitLab);

        var ghDriver = new StubDriver
        {
            Kind = PlatformKind.GitHub,
            Capabilities = new HashSet<PlatformCapability> { PlatformCapability.Secrets },
            CiSecrets = ghProv,
        };
        var glDriver = new StubDriver
        {
            Kind = PlatformKind.GitLab,
            Capabilities = new HashSet<PlatformCapability> { PlatformCapability.Secrets },
            CiSecrets = glProv,
        };

        var resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ListForTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PlatformInstallation(Guid.NewGuid(), TenantId, PlatformKind.GitHub, "u1", "1"),
                new PlatformInstallation(Guid.NewGuid(), TenantId, PlatformKind.GitLab, "u2", "2"),
            });
        resolver.Setup(r => r.ResolveForTenantAsync(
                TenantId, PlatformKind.GitHub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ghDriver);
        resolver.Setup(r => r.ResolveForTenantAsync(
                TenantId, PlatformKind.GitLab, It.IsAny<CancellationToken>()))
            .ReturnsAsync(glDriver);

        var auditEvents = new List<RotationAuditEvent>();
        var auditor = new Mock<IRotationAuditEmitter>(MockBehavior.Loose);
        auditor.Setup(a => a.EmitAsync(It.IsAny<RotationAuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<RotationAuditEvent, CancellationToken>((evt, _) => auditEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var handler = new CiSecretsRotationHandler(
            resolver.Object, auditor.Object,
            NullLogger<CiSecretsRotationHandler>.Instance);

        var target = new RotationTarget(
            Guid.NewGuid(), "MY_TOKEN", TenantId,
            "ci-secrets", IdentifierJson(),
            NewVersionNumber: 5, PreviousVersionNumber: 4);

        await handler.PushAsync(
            target, "newvalue123",
            RotationContext.ForCorrelation(Correlation), CancellationToken.None);

        ghProv.Calls.Should().HaveCount(1);
        glProv.Calls.Should().HaveCount(1);

        auditEvents.Should().HaveCount(2,
            "one CI_SECRET.PROVISIONED.SUCCESS per (platform, target) result");
        auditEvents.Should().AllSatisfy(e =>
            e.EventType.Should().Be(CiSecretsRotationHandler.ProvisionedSuccessEvent));
        auditEvents.Should().AllSatisfy(e =>
            e.RotationCorrelationId.Should().Be(Correlation));
    }

    // ── DryRun: no provisioner call ───────────────────────────────────

    [Test]
    public async Task PushAsync_DryRun_NoNetworkCall()
    {
        var ghProv = new StubProvisioner(PlatformKind.GitHub);
        var resolver = new Mock<IPlatformResolver>(MockBehavior.Loose);
        resolver.Setup(r => r.ListForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlatformInstallation>());

        var handler = new CiSecretsRotationHandler(
            resolver.Object,
            Mock.Of<IRotationAuditEmitter>(),
            NullLogger<CiSecretsRotationHandler>.Instance);

        var target = new RotationTarget(
            Guid.NewGuid(), "k", TenantId,
            "ci-secrets", IdentifierJson(),
            NewVersionNumber: 2, PreviousVersionNumber: 1);

        var ctx = new RotationContext(
            Correlation, Guid.Empty, DryRun: true,
            new Dictionary<string, string>());
        await handler.PushAsync(target, "v", ctx, CancellationToken.None);

        ghProv.Calls.Should().BeEmpty();
    }

    // ── Consumer-identifier parsing ───────────────────────────────────

    [Test]
    public void ParseConsumerIdentifier_RepoTarget_ReturnsRepo()
    {
        var spec = CiSecretsRotationHandler.ParseConsumerIdentifier(IdentifierJson(
            secretName: "DB_URL", scope: "Repo",
            owner: "acme", repo: "app"));

        spec.SecretName.Should().Be("DB_URL");
        spec.Scope.Should().Be(CiSecretScope.Repo);
        spec.Targets.Should().HaveCount(1);
        spec.Targets[0].Should().BeOfType<CiSecretTarget.Repo>();
    }

    [Test]
    public void ParseConsumerIdentifier_WithMetadata_PopulatesFlags()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            secretName = "K",
            scope = "Repo",
            targets = new[]
            {
                new { kind = "Repo", owner = "o", repo = "r" },
            },
            metadata = new
            {
                @protected = true,
                masked = true,
                environmentScope = "production",
                variableType = "file",
            },
        });
        var spec = CiSecretsRotationHandler.ParseConsumerIdentifier(json);
        spec.Metadata.Protected.Should().BeTrue();
        spec.Metadata.Masked.Should().BeTrue();
        spec.Metadata.EnvironmentScope.Should().Be("production");
        spec.Metadata.VariableType.Should().Be("file");
    }

    [Test]
    public void ParseConsumerIdentifier_BadScope_Throws()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            secretName = "K",
            scope = "NotARealScope",
            targets = new[] { new { kind = "Repo", owner = "o", repo = "r" } },
        });
        Action act = () => CiSecretsRotationHandler.ParseConsumerIdentifier(json);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid scope*");
    }

    // ── ProbeAsync is a no-op (healthy) ───────────────────────────────

    [Test]
    public async Task ProbeAsync_AlwaysHealthy()
    {
        var handler = new CiSecretsRotationHandler(
            Mock.Of<IPlatformResolver>(),
            Mock.Of<IRotationAuditEmitter>(),
            NullLogger<CiSecretsRotationHandler>.Instance);

        var probe = await handler.ProbeAsync(
            new RotationTarget(Guid.NewGuid(), "k", TenantId,
                "ci-secrets", IdentifierJson(), 2, 1),
            RotationContext.ForCorrelation(Correlation),
            CancellationToken.None);

        probe.IsHealthy.Should().BeTrue();
    }

    // ── System key matches DI registration convention ────────────────

    [Test]
    public void SystemKey_MatchesContractConstant()
    {
        var handler = new CiSecretsRotationHandler(
            Mock.Of<IPlatformResolver>(),
            Mock.Of<IRotationAuditEmitter>(),
            NullLogger<CiSecretsRotationHandler>.Instance);

        handler.System.Should().Be("ci-secrets");
        CiSecretsRotationHandler.SystemKey.Should().Be("ci-secrets");
    }

    // ── All-failures throws so the saga rolls back ───────────────────

    [Test]
    public async Task PushAsync_AllPlatformsFail_Throws()
    {
        var ghProv = new StubProvisioner(PlatformKind.GitHub) { AllFail = true };

        var ghDriver = new StubDriver
        {
            Kind = PlatformKind.GitHub,
            Capabilities = new HashSet<PlatformCapability> { PlatformCapability.Secrets },
            CiSecrets = ghProv,
        };

        var resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ListForTenantAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PlatformInstallation(Guid.NewGuid(), TenantId, PlatformKind.GitHub, "u", "1"),
            });
        resolver.Setup(r => r.ResolveForTenantAsync(
                TenantId, PlatformKind.GitHub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ghDriver);

        var handler = new CiSecretsRotationHandler(
            resolver.Object, Mock.Of<IRotationAuditEmitter>(),
            NullLogger<CiSecretsRotationHandler>.Instance);

        var target = new RotationTarget(
            Guid.NewGuid(), "k", TenantId,
            "ci-secrets", IdentifierJson(),
            NewVersionNumber: 2, PreviousVersionNumber: 1);

        Func<Task> act = () => handler.PushAsync(
            target, "v",
            RotationContext.ForCorrelation(Correlation), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ci-secrets rotation*");
    }
}
