using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts.Rules;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — rolling-window counter semantics:
/// <list type="bullet">
///   <item><description>adds a new entry and returns the count</description></item>
///   <item><description>drops entries older than the window</description></item>
///   <item><description>separate buckets per (ruleId, groupKey)</description></item>
///   <item><description>Null store always returns 1 (loud mis-use signal)</description></item>
/// </list>
/// </summary>
[TestFixture]
public class InMemoryRuleWindowStoreTests
{
    [Test]
    public void RecordAndCount_SingleEntry_ReturnsOne()
    {
        var store = new InMemoryRuleWindowStore();
        var now = DateTime.UtcNow;
        var count = store.RecordAndCount(
            Guid.NewGuid(), "tenantA", now, TimeSpan.FromMinutes(5));
        count.Should().Be(1);
    }

    [Test]
    public void RecordAndCount_ThreeEntriesWithinWindow_ReturnsThree()
    {
        var store = new InMemoryRuleWindowStore();
        var ruleId = Guid.NewGuid();
        var start = DateTime.UtcNow;
        store.RecordAndCount(ruleId, "tenantA", start, TimeSpan.FromMinutes(5));
        store.RecordAndCount(ruleId, "tenantA", start.AddSeconds(30), TimeSpan.FromMinutes(5));
        var third = store.RecordAndCount(
            ruleId, "tenantA", start.AddSeconds(60), TimeSpan.FromMinutes(5));
        third.Should().Be(3);
    }

    [Test]
    public void RecordAndCount_ExpiredEntriesDropped()
    {
        var store = new InMemoryRuleWindowStore();
        var ruleId = Guid.NewGuid();
        var start = DateTime.UtcNow;
        store.RecordAndCount(ruleId, "tenantA", start, TimeSpan.FromMinutes(5));
        store.RecordAndCount(ruleId, "tenantA", start.AddMinutes(2), TimeSpan.FromMinutes(5));
        var expiredWindow = store.RecordAndCount(
            ruleId, "tenantA", start.AddMinutes(10), TimeSpan.FromMinutes(5));
        // The first two are now >5 min old; only the new one remains.
        expiredWindow.Should().Be(1);
    }

    [Test]
    public void RecordAndCount_SeparateGroupKeys_IndependentCounts()
    {
        var store = new InMemoryRuleWindowStore();
        var ruleId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        store.RecordAndCount(ruleId, "tenantA", now, TimeSpan.FromMinutes(5));
        store.RecordAndCount(ruleId, "tenantA", now, TimeSpan.FromMinutes(5));
        var tenantB = store.RecordAndCount(
            ruleId, "tenantB", now, TimeSpan.FromMinutes(5));
        tenantB.Should().Be(1, "different group key → fresh bucket");
    }

    [Test]
    public void RecordAndCount_SeparateRuleIds_IndependentCounts()
    {
        var store = new InMemoryRuleWindowStore();
        var now = DateTime.UtcNow;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.RecordAndCount(a, "tenantA", now, TimeSpan.FromMinutes(5));
        store.RecordAndCount(a, "tenantA", now, TimeSpan.FromMinutes(5));
        var second = store.RecordAndCount(
            b, "tenantA", now, TimeSpan.FromMinutes(5));
        second.Should().Be(1);
    }

    [Test]
    public void NullStore_AlwaysReturnsZero_SoCountGteNeverFires()
    {
        // Fail-safe: a count_gte rule wired to NullRuleWindowStore
        // evaluates 0 >= threshold (with threshold >= 1 per parser
        // validation), which is always false — the rule silently
        // refuses to fire rather than always-firing. See
        // NullRuleWindowStore XML doc.
        var store = new NullRuleWindowStore();
        store.RecordAndCount(Guid.NewGuid(), "any", DateTime.UtcNow, TimeSpan.FromMinutes(1))
            .Should().Be(0);
    }
}
