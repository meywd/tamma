using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Epic 31 P2 (plan §4) — the read-only capability probe behind
/// <c>GET /api/v1/git/{owner}/{repo}/capabilities</c>: guard FIRST (an
/// unauthorized repo answers like a mediation 403, so the probe cannot
/// enumerate other tenants' platforms), then the resolved driver's LIVE
/// capability set + the credential-source LABEL; no driver ⇒ the same
/// fail-closed GIT_TOKEN_UNAVAILABLE shape the mediation ops use; and the
/// mediation no-throw posture (an exception becomes a typed failure).
/// </summary>
[TestFixture]
public class GitPlatformCapabilityServiceTests
{
    private const string Repo = "acme/widgets";
    private readonly Guid _tenant = Guid.NewGuid();

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private GitPlatformCapabilityService _sut = null!;

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public PlatformKind Kind => PlatformKind.Gitea;
        public IGitPlatformClient Client { get; } = NullGitPlatformDriver.Instance.Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            new HashSet<PlatformCapability> { PlatformCapability.Actions, PlatformCapability.PrFileReview };
    }

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        _sut = new GitPlatformCapabilityService(
            _authorizer.Object, _resolver.Object,
            NullLogger<GitPlatformCapabilityService>.Instance);
    }

    [Test]
    public async Task GuardDenied_403Shape_ResolverNeverConsulted()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(_tenant, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

        var result = await _sut.GetCapabilitiesAsync(_tenant, Repo);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.RepoNotAuthorized);
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task NoDriver_TokenUnavailableShape()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(_tenant, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitRepoAuthorization.Allow());
        _resolver
            .Setup(r => r.ResolveForMediationAsync(_tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediationDriverResolution?)null);

        var result = await _sut.GetCapabilitiesAsync(_tenant, Repo);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.TokenUnavailable);
    }

    [Test]
    public async Task ResolvedDriver_ReturnsKindCapabilitiesAndSourceLabel()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(_tenant, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitRepoAuthorization.Allow());
        _resolver
            .Setup(r => r.ResolveForMediationAsync(_tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediationDriverResolution(
                new FakeDriver(), MediationCredentialSource.TenantInstallation));

        var result = await _sut.GetCapabilitiesAsync(_tenant, Repo);

        result.Success.Should().BeTrue();
        result.PlatformKind.Should().Be("gitea");
        result.Capabilities.Should().BeEquivalentTo("Actions", "PrFileReview");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);
        result.Capabilities.Should().BeInAscendingOrder(StringComparer.Ordinal,
            "a stable order keeps the wire deterministic");
    }

    [Test]
    public async Task ResolverThrows_NoThrow_TypedPlatformError()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(_tenant, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitRepoAuthorization.Allow());
        _resolver
            .Setup(r => r.ResolveForMediationAsync(_tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.GetCapabilitiesAsync(_tenant, Repo);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(GitFailureCodes.PlatformError);
    }
}
