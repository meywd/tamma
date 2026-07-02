using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Jira;
using Tamma.Core.Interfaces;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Jira;

/// <summary>
/// Story 38 (Phase 1) — <see cref="JiraMediationService"/> composition. JIRA is not
/// repo-scoped (no guard / no token resolver): it runs the config-credentialed
/// <see cref="IJiraIntegrationService"/> under the acting tenant, then emits exactly
/// one terminal DCB event. Failures ride inside a typed key-free result (200
/// success:false at the endpoint); an unexpected exception becomes PLATFORM_ERROR.
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
        _sut = new JiraMediationService(_jira.Object, _events, NullLogger<JiraMediationService>.Instance);
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
