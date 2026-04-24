using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Tamma.Data.Internal;

/// <summary>
/// Pre-INSERT sequence-number generator for the
/// <c>domain_events.SequenceNumber</c> /
/// <c>platform_events.SequenceNumber</c> columns.
///
/// <para><b>Why this exists:</b> the production schema declares those
/// columns as Postgres <c>BIGSERIAL</c> (server-side identity), and
/// the Npgsql provider correctly emits an <c>INSERT ... RETURNING</c>
/// that flows the server-assigned value back into the entity. The EF
/// Core <b>InMemory</b> provider used by unit tests doesn't honour
/// <c>UseSerialColumn()</c>, so non-PK <c>ValueGeneratedOnAdd()</c>
/// properties stay at their default <c>0</c>. Two events at default
/// <c>0</c> means our cursor scan
/// (<c>WHERE SequenceNumber &gt; cursor</c>) skips them all.</para>
///
/// <para>This generator wires a process-wide, atomic, monotonic
/// counter that fills the property pre-INSERT. On Postgres the
/// store-generated value from <c>RETURNING</c> overwrites the
/// generator's output, so production behaviour is unchanged. On
/// InMemory the generator's value sticks and we get the
/// monotonic-per-stream guarantee the cursor relies on.</para>
///
/// <para>Each entity CLR type has its own counter so <c>DomainEvent</c>
/// and <c>PlatformEvent</c> receive independent sequences — matching
/// production where the two tables each own a separate BIGSERIAL
/// sequence. The counter is process-wide (not per-context) which is
/// fine for unit-test isolation: tests assert <i>relative</i>
/// monotonicity (<c>next &gt; prev</c>), not absolute starting values,
/// and the cursor itself only cares about strict ordering.</para>
/// </summary>
public sealed class EventSequenceValueGenerator : ValueGenerator<long>
{
    private static readonly ConcurrentDictionary<Type, long> _counters
        = new();

    public override bool GeneratesTemporaryValues => false;

    public override long Next(EntityEntry entry)
    {
        return _counters.AddOrUpdate(
            entry.Metadata.ClrType,
            1L,
            static (_, prev) => prev + 1L);
    }
}
