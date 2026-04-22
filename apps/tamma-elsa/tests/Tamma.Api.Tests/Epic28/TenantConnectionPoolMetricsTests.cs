using FluentAssertions;
using NUnit.Framework;
using System.Diagnostics.Metrics;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-4 — exercises <see cref="TenantConnectionPoolMetrics"/>:
/// counters, gauges, and the cache-hit-ratio derivation. Subscribes
/// via <see cref="MeterListener"/> so we can assert the OTel-side
/// names match the user-task surface
/// (<c>tamma.tenant_pools.warm</c> et al).
/// </summary>
[TestFixture]
public class TenantConnectionPoolMetricsTests
{
    [Test]
    public void Initial_State_Is_Zero()
    {
        using var sut = new TenantConnectionPoolMetrics();
        sut.WarmPoolCount.Should().Be(0);
        sut.OpenedTotal.Should().Be(0);
        sut.EvictedTotal.Should().Be(0);
        sut.HitsTotal.Should().Be(0);
        sut.MissesTotal.Should().Be(0);
    }

    [Test]
    public void RecordOpened_Bumps_Warm_And_Opened_Counters()
    {
        using var sut = new TenantConnectionPoolMetrics();

        sut.RecordOpened();
        sut.RecordOpened();

        sut.OpenedTotal.Should().Be(2);
        sut.WarmPoolCount.Should().Be(2);
    }

    [Test]
    public void RecordEviction_Decrements_Warm_And_Bumps_Evicted()
    {
        using var sut = new TenantConnectionPoolMetrics();
        sut.RecordOpened();
        sut.RecordOpened();

        sut.RecordEviction("lru");

        sut.WarmPoolCount.Should().Be(1);
        sut.EvictedTotal.Should().Be(1);
    }

    [Test]
    public void RecordHit_And_RecordMiss_Update_Tallies()
    {
        using var sut = new TenantConnectionPoolMetrics();
        sut.RecordHit();
        sut.RecordHit();
        sut.RecordHit();
        sut.RecordMiss();

        sut.HitsTotal.Should().Be(3);
        sut.MissesTotal.Should().Be(1);
    }

    [Test]
    public void Otel_Surface_Exposes_Expected_Instruments()
    {
        using var sut = new TenantConnectionPoolMetrics();

        var instrumentNames = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TenantConnectionPoolMetrics.MeterName)
                {
                    instrumentNames.Add(instrument.Name);
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        // The metrics object publishes its instruments at construction —
        // they're already in the set by the time the listener starts. We
        // need to invoke RecordObservableInstruments to flush gauges, but
        // counters are already published.
        listener.RecordObservableInstruments();

        instrumentNames.Should().BeEquivalentTo(new[]
        {
            "tamma.tenant_pools.opened_total",
            "tamma.tenant_pools.evicted_total",
            "tamma.tenant_pools.warm",
            "tamma.tenant_pools.cache_hit_ratio",
        });
    }

    [Test]
    public void Cache_Hit_Ratio_Gauge_Reads_Live_Counters()
    {
        using var sut = new TenantConnectionPoolMetrics();

        // Bias 3:1 hits:misses → ratio == 0.75.
        sut.RecordHit();
        sut.RecordHit();
        sut.RecordHit();
        sut.RecordMiss();

        double? observed = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TenantConnectionPoolMetrics.MeterName
                    && instrument.Name == "tamma.tenant_pools.cache_hit_ratio")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observed = value);
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().NotBeNull();
        observed!.Value.Should().BeApproximately(0.75d, 1e-6);
    }

    [Test]
    public void Cache_Hit_Ratio_Is_Zero_Before_Any_Access()
    {
        using var sut = new TenantConnectionPoolMetrics();

        double? observed = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TenantConnectionPoolMetrics.MeterName
                    && instrument.Name == "tamma.tenant_pools.cache_hit_ratio")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observed = value);
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().Be(0d, "no accesses yet → ratio defaults to 0 (not NaN)");
    }
}
