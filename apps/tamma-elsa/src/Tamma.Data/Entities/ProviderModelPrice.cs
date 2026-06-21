namespace Tamma.Data.Entities;

/// <summary>
/// The COST pricing — per model, versioned (Story 34-11). An edit SUPERSEDES
/// rather than mutates (partial unique index on (ProviderKey, Model)
/// WHERE Status='active'); EffectiveFrom-windowed so a usage event prices under
/// the rate active at its OccurredAt (reproducible/byte-stable — the cost-side
/// companion of 34-5 AC7). USD-per-1M tokens.
///
/// <para>Platform-global — NO <c>TenantId</c>/<c>UserId</c>. The cost basis is
/// identical for every tenant; BYOK only changes the *sell* price (34-5).</para>
/// </summary>
public class ProviderModelPrice
{
    /// <summary>
    /// Primary key. New rows get a v4 GUID — the DB default is
    /// <c>gen_random_uuid()</c> (UUIDv4) and admin inserts use
    /// <c>Guid.NewGuid()</c> (UUIDv4). ONLY the seeder bakes deterministic,
    /// UUIDv7-shaped ids (for insert-missing-only idempotency).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Canonical provider key (alias-normalized on write). FK → <c>providers.Key</c>.</summary>
    public string ProviderKey { get; set; } = null!;

    /// <summary>Model id — e.g. <c>claude-sonnet-4-20250514</c>, <c>gpt-4o</c>.</summary>
    public string Model { get; set; } = null!;

    /// <summary>Input cost in USD per 1,000,000 tokens.</summary>
    public decimal InputUsdPer1M { get; set; }

    /// <summary>Output cost in USD per 1,000,000 tokens.</summary>
    public decimal OutputUsdPer1M { get; set; }

    /// <summary>Reserved (nullable) — cache-read cost USD per 1M tokens.</summary>
    public decimal? CacheReadUsdPer1M { get; set; }

    /// <summary>Reserved (nullable) — cache-write cost USD per 1M tokens.</summary>
    public decimal? CacheWriteUsdPer1M { get; set; }

    /// <summary>UTC instant this rate became effective (the resolution-window key).</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Lifecycle: <c>active</c> | <c>superseded</c>. A CHECK pins the enum.</summary>
    public string Status { get; set; } = "active";

    /// <summary>Provenance: <c>seed</c> | <c>admin</c>. A CHECK pins the enum.
    /// The seeder is insert-missing-only and NEVER reverts a <c>Source='admin'</c> row.</summary>
    public string Source { get; set; } = "seed";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
