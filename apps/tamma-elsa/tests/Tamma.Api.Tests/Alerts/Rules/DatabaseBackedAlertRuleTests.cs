using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — per-event evaluation tests for
/// <see cref="DatabaseBackedAlertRule"/>. Exercises every predicate
/// op end-to-end against real <see cref="DomainEvent"/> payloads.
/// </summary>
[TestFixture]
public class DatabaseBackedAlertRuleTests
{
    private static AlertRule MakeRow(
        string predicate,
        string severity = AlertSeverity.Warning,
        string eventType = "TEST.EVENT",
        int throttle = 0)
    {
        return new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = "test-rule",
            Description = "tenant {tenantId} fired {eventType}",
            IsEnabled = true,
            Severity = severity,
            EventType = eventType,
            Predicate = predicate,
            ThrottleSeconds = throttle,
            ChannelIds = Array.Empty<Guid>(),
            IsBuiltIn = false,
            BuiltInKey = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static DomainEvent MakeEvent(
        string type = "TEST.EVENT",
        Guid? tenantId = null,
        string? tagsJson = null,
        string? dataJson = null)
    {
        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = tagsJson ?? "{}",
            Metadata = "{}",
            Data = dataJson ?? "{}",
            CreatedAt = DateTime.UtcNow,
        };
    }

    [Test]
    public void Always_FiresOnEveryEvent()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"always"}"""));
        var evt = MakeEvent(tenantId: Guid.NewGuid());
        var payload = rule.Evaluate(new AlertRuleContext(
            rule.Id, evt, new NullRuleWindowStore()));
        payload.Should().NotBeNull();
        payload!.Severity.Should().Be(AlertSeverity.Warning);
        payload.RuleId.Should().Be(rule.Id);
        payload.TenantId.Should().Be(evt.TenantId);
    }

    [Test]
    public void Always_InterpolatesMustacheTokens()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"always"}"""));
        var tenantId = Guid.NewGuid();
        var payload = rule.Evaluate(new AlertRuleContext(
            rule.Id,
            MakeEvent(type: "BUDGET.EXHAUSTED", tenantId: tenantId),
            new NullRuleWindowStore()));
        payload!.Description.Should().Contain(tenantId.ToString("N"));
        payload.Description.Should().Contain("BUDGET.EXHAUSTED");
    }

    [Test]
    public void CountGte_DoesNotFireBelowThreshold()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"count_gte","window_seconds":300,"threshold":3}"""));
        var tenantId = Guid.NewGuid();
        var store = new InMemoryRuleWindowStore();
        // First two events correlated by tenantId.
        rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tenantId: tenantId), store))
            .Should().BeNull();
        rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tenantId: tenantId), store))
            .Should().BeNull();
    }

    [Test]
    public void CountGte_FiresAtThreshold()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"count_gte","window_seconds":300,"threshold":3}"""));
        var tenantId = Guid.NewGuid();
        var store = new InMemoryRuleWindowStore();
        rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: tenantId), store));
        rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: tenantId), store));
        var third = rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: tenantId), store));
        third.Should().NotBeNull();
    }

    [Test]
    public void CountGte_SeparateTenants_DontCrossCorrelate()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"count_gte","window_seconds":300,"threshold":2}"""));
        var store = new InMemoryRuleWindowStore();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: t1), store))
            .Should().BeNull();
        // Tenant 2's first event — separate bucket, no fire.
        rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: t2), store))
            .Should().BeNull();
        // Tenant 1's second event — fires.
        rule.Evaluate(new AlertRuleContext(rule.Id, MakeEvent(tenantId: t1), store))
            .Should().NotBeNull();
    }

    [Test]
    public void TagEq_MatchesWhenTagValueEqualsLiteral()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"tag_eq","key":"severity","value":"critical"}"""));
        var firing = rule.Evaluate(new AlertRuleContext(
            rule.Id,
            MakeEvent(tagsJson: """{"severity":"critical"}"""),
            new NullRuleWindowStore()));
        firing.Should().NotBeNull();

        var notFiring = rule.Evaluate(new AlertRuleContext(
            rule.Id,
            MakeEvent(tagsJson: """{"severity":"warning"}"""),
            new NullRuleWindowStore()));
        notFiring.Should().BeNull();
    }

    [Test]
    public void DataFieldEq_NestedPathMatches()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""{"op":"data_field_eq","path":"nested.state","value":"failed"}"""));
        var firing = rule.Evaluate(new AlertRuleContext(
            rule.Id,
            MakeEvent(dataJson: """{"nested":{"state":"failed"}}"""),
            new NullRuleWindowStore()));
        firing.Should().NotBeNull();
    }

    [Test]
    public void And_BothClausesMustMatch()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""
            {"op":"and","clauses":[
                {"op":"tag_eq","key":"a","value":"1"},
                {"op":"tag_eq","key":"b","value":"2"}
            ]}
            """));
        var both = rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tagsJson: """{"a":"1","b":"2"}"""),
            new NullRuleWindowStore()));
        both.Should().NotBeNull();

        var onlyA = rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tagsJson: """{"a":"1"}"""),
            new NullRuleWindowStore()));
        onlyA.Should().BeNull();
    }

    [Test]
    public void Or_AnyClauseSuffices()
    {
        var rule = new DatabaseBackedAlertRule(
            MakeRow("""
            {"op":"or","clauses":[
                {"op":"tag_eq","key":"a","value":"1"},
                {"op":"tag_eq","key":"b","value":"2"}
            ]}
            """));
        var a = rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tagsJson: """{"a":"1"}"""),
            new NullRuleWindowStore()));
        a.Should().NotBeNull();

        var neither = rule.Evaluate(new AlertRuleContext(
            rule.Id, MakeEvent(tagsJson: """{"c":"3"}"""),
            new NullRuleWindowStore()));
        neither.Should().BeNull();
    }

    [Test]
    public void Ctor_WithInvalidPredicate_Throws()
    {
        var bad = MakeRow("""{"op":"bogus"}""");
        var act = () => new DatabaseBackedAlertRule(bad);
        act.Should().Throw<InvalidAlertRulePredicateException>();
    }
}
