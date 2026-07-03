using System.Collections.Concurrent;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Services.Jira;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Jira;

/// <summary>
/// Integration BYOK — <see cref="JiraMediationService"/> composition. The old
/// SaaS-deny guard is replaced by per-tenant credential resolution: the tenant's
/// JIRA credential is resolved (BYOK→system→fail-loud) and threaded into the
/// credential-bound <see cref="IJiraApiClient"/>; a terminal DCB event is emitted.
///
/// <para>Pins: present credential ⇒ the client is called with it and one terminal
/// event is emitted; ABSENT credential ⇒ fail-loud
/// <see cref="JiraFailureCodes.CredentialUnavailable"/> with the client NEVER
/// reached; client failures map to the typed key-free taxonomy; an exception
/// becomes PLATFORM_ERROR.</para>
/// </summary>
[TestFixture]
public class JiraMediationServiceTests
{
    private const string TicketId = "PROJ-42";
    private static readonly JiraCredential Cred = new("https://jira.example.com", "bot@example.com", "fake-jira-token");

    private Mock<IJiraApiClient> _jira = null!;
    private FakeResolver _resolver = null!;
    private RecordingEventRepository _events = null!;
    private JiraMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _jira = new Mock<IJiraApiClient>(MockBehavior.Strict);
        _resolver = new FakeResolver { Resolution = new JiraCredentialResolution(Cred, IntegrationCredentialSource.Tenant) };
        _events = new RecordingEventRepository();
        _sut = new JiraMediationService(_jira.Object, _resolver, _events, NullLogger<JiraMediationService>.Instance);
    }

    [Test]
    public async Task GetTicket_CredentialResolved_CallsClientWithIt_OneEvent()
    {
        _jira.Setup(j => j.GetTicketAsync(Cred, TicketId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrationResult<JiraTicket?>.Ok(new JiraTicket { Id = "1", Key = TicketId, Summary = "s", Status = "In Progress" }));

        var result = await _sut.GetTicketAsync(_tenant, TicketId, "corr-j");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Read");
        result.Ticket!.Key.Should().Be(TicketId);
        _jira.Verify(j => j.GetTicketAsync(Cred, TicketId, It.IsAny<CancellationToken>()), Times.Once);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketReadSuccess);
    }

    [Test]
    public async Task UpdateTicket_CredentialResolved_ReturnsKey_OneEvent()
    {
        _jira.Setup(j => j.UpdateTicketAsync(Cred, TicketId, It.IsAny<JiraTicketUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrationResult<JiraTicketResult>.Ok(new JiraTicketResult { Success = true, TicketKey = TicketId }));

        var body = new UpdateTicketRequest { Status = "Done", Comment = "merged", CorrelationId = "corr-u" };
        var result = await _sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Updated");
        result.TicketKey.Should().Be(TicketId);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedSuccess);
    }

    [Test]
    public async Task NoCredential_FailsLoud_NeverCallsClient_OneFailedEvent()
    {
        // The strict mock has NO setup ⇒ any call would throw. Resolver returns null.
        _resolver.Resolution = null;

        var body = new UpdateTicketRequest { Status = "Done", CorrelationId = "corr-x" };
        var result = await _sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.CredentialUnavailable);
        result.TicketKey.Should().Be(TicketId);
        _jira.Verify(j => j.UpdateTicketAsync(It.IsAny<JiraCredential>(), It.IsAny<string>(), It.IsAny<JiraTicketUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedFailed);
    }

    [Test]
    public async Task UpdateTicket_NotConfiguredFromClient_TypedNotConfigured()
    {
        _jira.Setup(j => j.UpdateTicketAsync(Cred, TicketId, It.IsAny<JiraTicketUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrationResult<JiraTicketResult>.Fail("JIRA not configured"));

        var body = new UpdateTicketRequest { Status = "Done", CorrelationId = "corr-u" };
        var result = await _sut.UpdateTicketAsync(_tenant, TicketId, body);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.NotConfigured);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketUpdatedFailed);
    }

    [Test]
    public async Task GetTicket_ClientThrows_TypedPlatformError_OneFailedEvent()
    {
        _jira.Setup(j => j.GetTicketAsync(Cred, TicketId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.GetTicketAsync(_tenant, TicketId, "corr-j");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(JiraFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(JiraEventTypes.TicketReadFailed);
    }

    private sealed class FakeResolver : IJiraCredentialResolver
    {
        public JiraCredentialResolution? Resolution { get; set; }
        public Task<JiraCredentialResolution?> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
            => Task.FromResult(Resolution);
        public void Invalidate(Guid? tenantId) { }
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
