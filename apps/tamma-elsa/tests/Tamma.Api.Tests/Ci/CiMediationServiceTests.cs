using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Ci;
using Tamma.Api.Services.Git;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Ci;

/// <summary>
/// Story 38 (Phase 1) — <see cref="CiMediationService"/> composition (guard → token →
/// CI-with-resolved-token → one DCB event). Mirrors the git-mediation tests: the
/// guard runs FIRST (deny ⇒ no token resolved, CI never invoked), the resolved token
/// is the one minted into the client, an expected platform failure rides inside
/// success:false, the 503 token-unavailable path, and the resolved token never leaks
/// into the result or the audit event.
/// </summary>
[TestFixture]
public class CiMediationServiceTests
{
    private const string SecretToken = "ghp-CI-SUPER-SECRET-DO-NOT-LEAK-1234567890";
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IGitTokenResolver> _tokenResolver = null!;
    private Mock<ICiClientFactory> _factory = null!;
    private Mock<ICIIntegrationService> _ci = null!;
    private RecordingEventRepository _events = null!;
    private CiMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _tokenResolver = new Mock<IGitTokenResolver>(MockBehavior.Strict);
        _factory = new Mock<ICiClientFactory>(MockBehavior.Strict);
        _ci = new Mock<ICIIntegrationService>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        _factory.Setup(f => f.Create(It.IsAny<string>())).Returns(_ci.Object);

        _sut = new CiMediationService(
            _authorizer.Object, _tokenResolver.Object, _factory.Object, _events,
            NullLogger<CiMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private void ResolveToken(string source) => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GitTokenResolution(SecretToken, source));

    private void NoToken() => _tokenResolver
        .Setup(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((GitTokenResolution?)null);

    private static TriggerTestsRequest TriggerBody() => new() { Branch = "feature", CorrelationId = "corr-ci" };

    [Test]
    public async Task TriggerTests_GuardDenied_403_NoTokenResolved_CiNeverCalled()
    {
        Deny();

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.RepoNotAuthorized);
        result.CredentialSource.Should().BeNull();

        _tokenResolver.Verify(t => t.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _ci.VerifyNoOtherCalls();

        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    [Test]
    public async Task TriggerTests_Success_Byok_UsesResolvedToken_OneSuccessEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _ci.Setup(c => c.TriggerTestsAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<TestRunResult>.Ok(new TestRunResult { RunId = "42", Status = "success", TotalTests = 10 }));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Triggered");
        result.TestRun!.RunId.Should().Be("42");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);
        _factory.Verify(f => f.Create(SecretToken), Times.Once, "the token USED == the token RESOLVED");

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(CiEventTypes.TestsTriggeredSuccess);
        evt.TenantId.Should().Be(_tenant);
    }

    [Test]
    public async Task GetBuildStatus_Success_Platform_StampsPlatformSource()
    {
        Allow();
        ResolveToken(GitCredentialSources.Platform);
        _ci.Setup(c => c.GetBuildStatusAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<BuildStatus>.Ok(new BuildStatus { Status = "success", BuildUrl = "u" }));

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Read");
        result.BuildStatus!.Status.Should().Be("success");
        result.CredentialSource.Should().Be(GitCredentialSources.Platform);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.BuildStatusReadSuccess);
    }

    [Test]
    public async Task TriggerTests_TokenUnavailable_503_FailClosed_CiNeverCalled()
    {
        Allow();
        NoToken();

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.TokenUnavailable);
        _factory.Verify(f => f.Create(It.IsAny<string>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    [Test]
    public async Task GetBuildStatus_PlatformFailure_200SuccessFalse_PreservesStatus()
    {
        Allow();
        ResolveToken(GitCredentialSources.Platform);
        _ci.Setup(c => c.GetBuildStatusAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<BuildStatus>.Fail("403: forbidden"));

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.PlatformError);
        result.PlatformStatusCode.Should().Be(403);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.BuildStatusReadFailed);
    }

    [Test]
    public async Task CiThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _ci.Setup(c => c.TriggerTestsAsync(Repo, "feature")).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    [Test]
    public async Task CredentialSafety_ResolvedToken_NeverLeaks()
    {
        Allow();
        ResolveToken(GitCredentialSources.Byok);
        _ci.Setup(c => c.TriggerTestsAsync(Repo, "feature"))
            .ReturnsAsync(IntegrationResult<TestRunResult>.Ok(new TestRunResult { RunId = "1", Status = "success" }));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        JsonSerializer.Serialize(result).Should().NotContain(SecretToken);
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain(SecretToken);
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
