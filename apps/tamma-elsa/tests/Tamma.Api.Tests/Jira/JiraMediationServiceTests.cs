using System.Collections.Concurrent;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Jira;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Jira;

/// <summary>
/// Story 38 (Phase 1) — <see cref="JiraMediationService"/> composition. JIRA has no
/// per-tenant token resolver: it runs the config-credentialed
/// <see cref="IJiraIntegrationService"/> under the acting tenant, then emits exactly
/// one terminal DCB event. Failures ride inside a typed key-free result (200
/// success:false at the endpoint); an unexpected exception becomes PLATFORM_ERROR.
///
/// <para>Because that single credential has no per-tenant/ticket scoping, a
/// fail-closed mode guard applies: single-user ⇒ allow; SaaS ⇒ deny (typed
/// <see cref="JiraFailureCodes.SharedCredentialDeniedInSaaS"/>, underlying client
/// never called) unless <c>Jira:AllowSharedCredentialInSaaS=true</c> opts in.</para>
/// </summary>
[TestFixture]
public class JiraMediationServiceTests
{
    private const string TicketId = "PROJ-42";

    private Mock<IJiraIntegrationService> _jira = null!;
    private RecordingEventRepository _events = null!;
    private JiraMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _jira = new Mock<IJiraIntegrationService>(MockBehavior.Strict);
        _events = new RecordingEventRepository();
        // Default SUT runs in single-user mode (the sole principal owns everything),
        // so the shared JIRA credential is always allowed — matches the pre-guard
        // behavior the composition tests below assert.
        _sut = Build(TammaMode.SingleUser);
    }

    private JiraMediationService Build(TammaMode mode, bool allowSharedInSaaS = false)
    {
        var modeProvider = new Mock<ITammaModeProvider>();
        modeProvider.SetupGet(m => m.Mode).Returns(mode);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jira:AllowSharedCredentialInSaaS"] = allowSharedInSaaS ? "true" : "false",
            })
            .Build();
        return new JiraMediationService(
            _jira.Object, _events, modeProvider.Object, configuration, NullLogger<JiraMediationService>.Instance);
    }

    [Test]
    public async Task GetTicket_Success_MapsTicket_OneEvent()
    {
        _jira.Setup(j => j.GetJiraTicketAsync(TicketId))
            .ReturnsAsync(IntegrationResult<JiraTicket?>.Ok(new JiraTicket { Id = "1", Key = TicketId, Summary = "s", Status = "In Progress" }));

        var result = await _sut.GetTicketAsync(_tenant, TicketId, "corr-j");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Read");
        result.Ticket!.Key.Should().Be(TicketId);
        result.Ticket.Status.Should().Be("In Progress");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketReadSuccess);
    }

    [Test]
    public async Task UpdateTicket_Success_ReturnsKey_OneEvent()
    {
        _jira.Setup(j => j.UpdateJiraTicketAsync(TicketId, It.IsAny<JiraTicketUpdate>()))
            .ReturnsAsync(IntegrationResult<JiraTicketResult>.Ok(new JiraTicketResult { Success = true, TicketKey = TicketId }));

        var body = new UpdateTicketRequest { Status = "Done", Comment = "merged", CorrelationId = "corr-u" };
        var result = await _sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Updated");
        result.TicketKey.Should().Be(TicketId);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedSuccess);
    }

    [Test]
    public async Task UpdateTicket_NotConfigured_TypedNotConfigured_OneFailedEvent()
    {
        _jira.Setup(j => j.UpdateJiraTicketAsync(TicketId, It.IsAny<JiraTicketUpdate>()))
            .ReturnsAsync(IntegrationResult<JiraTicketResult>.Fail("JIRA not configured"));

        var body = new UpdateTicketRequest { Status = "Done", CorrelationId = "corr-u" };
        var result = await _sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.NotConfigured);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedFailed);
    }

    [Test]
    public async Task GetTicket_ServiceThrows_TypedPlatformError_OneFailedEvent()
    {
        _jira.Setup(j => j.GetJiraTicketAsync(TicketId)).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.GetTicketAsync(_tenant, TicketId, "corr-j");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketReadFailed);
    }

    // ── fail-closed tenant guard (SaaS shared-credential) ───────────────────

    [Test]
    public async Task SingleUser_SharedCredential_Allows_CallsJira()
    {
        // (a) single-user: the sole principal owns everything ⇒ the guard allows the
        // op and the underlying JIRA client is reached.
        _jira.Setup(j => j.GetJiraTicketAsync(TicketId))
            .ReturnsAsync(IntegrationResult<JiraTicket?>.Ok(new JiraTicket { Id = "1", Key = TicketId, Summary = "s", Status = "Open" }));
        var sut = Build(TammaMode.SingleUser);

        var result = await sut.GetTicketAsync(_tenant, TicketId, "corr-su");

        result.Success.Should().BeTrue();
        _jira.Verify(j => j.GetJiraTicketAsync(TicketId), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketReadSuccess);
    }

    [Test]
    public async Task Saas_NoOptIn_Denies_TypedResult_NeverCallsJira()
    {
        // (b) SaaS without the opt-in: the shared-credential path is a confused-deputy,
        // so the guard denies with the typed key-free failure and the strict-mock JIRA
        // client is NEVER invoked (no Setup ⇒ any call would throw).
        var sut = Build(TammaMode.SaaS, allowSharedInSaaS: false);

        var body = new UpdateTicketRequest { Status = "Done", CorrelationId = "corr-saas" };
        var result = await sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.SharedCredentialDeniedInSaaS);
        result.TicketKey.Should().Be(TicketId);
        _jira.Verify(j => j.UpdateJiraTicketAsync(It.IsAny<string>(), It.IsAny<JiraTicketUpdate>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedFailed);
    }

    [Test]
    public async Task Saas_WithOptIn_Allows_CallsJira()
    {
        // (c) SaaS WITH Jira:AllowSharedCredentialInSaaS=true: the operator knowingly
        // re-enabled the shared credential ⇒ the op runs and reaches the JIRA client.
        _jira.Setup(j => j.UpdateJiraTicketAsync(TicketId, It.IsAny<JiraTicketUpdate>()))
            .ReturnsAsync(IntegrationResult<JiraTicketResult>.Ok(new JiraTicketResult { Success = true, TicketKey = TicketId }));
        var sut = Build(TammaMode.SaaS, allowSharedInSaaS: true);

        var body = new UpdateTicketRequest { Status = "Done", CorrelationId = "corr-optin" };
        var result = await sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Updated");
        _jira.Verify(j => j.UpdateJiraTicketAsync(TicketId, It.IsAny<JiraTicketUpdate>()), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedSuccess);
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
