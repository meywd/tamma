using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Documents;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-8 (AC1 RESOLVED clause, AC3 denormalized duration, D9). The escalation disposition
/// service must pair on the <c>escalationId</c> tag of the originating
/// <c>ESCALATION.TRIGGERED</c>, append an <c>ESCALATION.RESOLVED</c> carrying
/// disposition/note/<c>durationMs</c> (computed from the trigger's <c>CreatedAt</c>) with the
/// trigger's tags copied, 404 an unknown escalationId, and 409 a double-disposition. FAIL-LOUD:
/// the append is the operation.
/// </summary>
[TestFixture]
public class EscalationDispositionTests
{
    private Mock<IEventRepository> _events = null!;
    private EscalationDispositionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _events = new Mock<IEventRepository>();
        _service = new EscalationDispositionService(_events.Object, NullLogger<EscalationDispositionService>.Instance);
    }

    private static DomainEvent Trigger(string escalationId, DateTime createdAt)
    {
        var tags = new Dictionary<string, object?>
        {
            ["escalationId"] = escalationId,
            ["issueId"] = "issue-9",
            ["documentId"] = "doc-7",
            ["documentType"] = "design",
            ["correlationId"] = "corr-5",
        };
        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = ApprovalEvents.EscalationTriggered,
            Tags = JsonSerializer.Serialize(tags),
            Data = "{}",
            CreatedAt = createdAt,
            IssueNumber = 9,
        };
    }

    private void SetupTriggered(params DomainEvent[] events) =>
        _events.Setup(r => r.QueryAsync(It.IsAny<Guid?>(), ApprovalEvents.EscalationTriggered, null, It.IsAny<int>()))
            .ReturnsAsync(events.ToList());

    private void SetupResolved(params DomainEvent[] events) =>
        _events.Setup(r => r.QueryAsync(It.IsAny<Guid?>(), ApprovalEvents.EscalationResolved, null, It.IsAny<int>()))
            .ReturnsAsync(events.ToList());

    [Test]
    public async Task Disposition_Resolved_AppendsResolvedWithDurationAndCopiedTags()
    {
        var tenant = Guid.NewGuid();
        var trigger = Trigger("esc-1", DateTime.UtcNow.AddSeconds(-10));
        SetupTriggered(trigger);
        SetupResolved(/* none yet */);

        DomainEvent? appended = null;
        _events.Setup(r => r.AppendAsync(It.IsAny<DomainEvent>()))
            .Callback<DomainEvent>(e => appended = e)
            .ReturnsAsync((DomainEvent e) => e);

        var result = await _service.DispositionAsync(
            tenant, "esc-1", EscalationDisposition.Overridden, "manual override", "alice@x.test", ApprovalChannel.User);

        result.Outcome.Should().Be(EscalationDispositionOutcome.Resolved);
        result.DurationMs.Should().BeGreaterThan(9_000).And.BeLessThan(120_000);

        appended.Should().NotBeNull();
        appended!.Type.Should().Be(ApprovalEvents.EscalationResolved);
        appended.TenantId.Should().Be(tenant);

        var tags = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(appended.Tags)!;
        tags["escalationId"].GetString().Should().Be("esc-1");
        tags["issueId"].GetString().Should().Be("issue-9");
        tags["documentId"].GetString().Should().Be("doc-7");
        tags["documentType"].GetString().Should().Be("design");
        tags["correlationId"].GetString().Should().Be("corr-5");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(appended.Data)!;
        data["disposition"].GetString().Should().Be("overridden");
        data["note"].GetString().Should().Be("manual override");
        data["resolvedBy"].GetString().Should().Be("alice@x.test");
        data["channel"].GetString().Should().Be("user");
        data["durationMs"].GetInt64().Should().Be(result.DurationMs);
    }

    [Test]
    public async Task Disposition_UnknownEscalationId_Returns404_NoAppend()
    {
        SetupTriggered(/* no matching trigger */);
        SetupResolved();

        var result = await _service.DispositionAsync(
            Guid.NewGuid(), "missing", EscalationDisposition.Resolved, null, "dev@x.test", ApprovalChannel.User);

        result.Outcome.Should().Be(EscalationDispositionOutcome.NotFound);
        _events.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task Disposition_AlreadyResolved_Returns409_NoAppend()
    {
        var trigger = Trigger("esc-2", DateTime.UtcNow.AddSeconds(-5));
        SetupTriggered(trigger);

        // A RESOLVED already exists for this escalationId.
        var resolvedTags = JsonSerializer.Serialize(new Dictionary<string, object?> { ["escalationId"] = "esc-2" });
        SetupResolved(new DomainEvent { Type = ApprovalEvents.EscalationResolved, Tags = resolvedTags, Data = "{}", CreatedAt = DateTime.UtcNow });

        var result = await _service.DispositionAsync(
            Guid.NewGuid(), "esc-2", EscalationDisposition.Resolved, null, "dev@x.test", ApprovalChannel.User);

        result.Outcome.Should().Be(EscalationDispositionOutcome.AlreadyResolved);
        _events.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public void Disposition_AppendFailure_PropagatesFailLoud()
    {
        var trigger = Trigger("esc-3", DateTime.UtcNow.AddSeconds(-1));
        SetupTriggered(trigger);
        SetupResolved();
        _events.Setup(r => r.AppendAsync(It.IsAny<DomainEvent>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var act = async () => await _service.DispositionAsync(
            Guid.NewGuid(), "esc-3", EscalationDisposition.Resolved, null, "dev@x.test", ApprovalChannel.Api);

        act.Should().ThrowAsync<InvalidOperationException>(
            "the disposition service is FAIL-LOUD — the event IS the operation, unlike best-effort emitters");
    }
}
