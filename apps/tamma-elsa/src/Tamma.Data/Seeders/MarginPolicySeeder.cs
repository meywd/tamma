using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Story 34-5 — seeds the default <b>global</b> margin policy
/// (<c>MarkupMultiplier = 1.3</c>, i.e. +30%) into the control-plane
/// <c>margin_policies</c> table so the cost->price engine always has a global
/// safety-net policy to resolve to.
///
/// <para><b>Insert-missing-only</b> (mirrors <see cref="ProviderPricingSeeder"/>
/// / <see cref="PlansSeeder"/> and the convention system-defaults ownership
/// rule): the row is inserted only when absent. A re-run is a no-op and NEVER
/// reverts an admin-edited multiplier — re-pricing happens through the admin
/// write path (supersede + insert), not the seeder. The idempotency check keys
/// off the deterministic seed id, so even after an admin supersedes the seeded
/// global row (leaving it <c>superseded</c> plus a fresh <c>active</c> row) the
/// re-run still short-circuits.</para>
///
/// <para><b>Deterministic UUIDv7-shaped id.</b> .NET 8 lacks
/// <c>Guid.CreateVersion7</c>, so the seed id is derived deterministically from a
/// stable namespace + name (SHA-256), with the RFC-4122 version nibble forced to
/// 7 and the variant bits set — a stable id across environments.</para>
/// </summary>
public static class MarginPolicySeeder
{
    /// <summary>
    /// The fixed seed epoch the v1 global policy carries as
    /// <c>EffectiveFrom</c> — a stable past instant so the EffectiveFrom window
    /// always selects it for any realistic <c>OccurredAt</c>. NEVER change.
    /// </summary>
    public static readonly DateTime SeedEpoch =
        new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>DNS-style namespace for the deterministic seed id. NEVER change.</summary>
    private const string IdNamespace = "tamma.margin-policy.v1";

    /// <summary>The default platform markup: +30% on the provider cost basis.</summary>
    public const decimal DefaultGlobalMarkupMultiplier = 1.3m;

    /// <summary>
    /// Inserts the missing global margin policy. Safe to call on every startup —
    /// the per-id existence check makes re-runs a no-op and never reverts an
    /// admin edit. Returns the number of policy rows inserted (0 or 1).
    /// </summary>
    public static async Task<int> SeedAsync(
        ControlPlaneDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var globalId = DeterministicId("global");

        var exists = await context.MarginPolicies
            .AsNoTracking()
            .AnyAsync(p => p.Id == globalId, cancellationToken);
        if (exists)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        context.MarginPolicies.Add(new MarginPolicy
        {
            Id = globalId,
            Scope = "global",
            RefKey = null,
            MarkupMultiplier = DefaultGlobalMarkupMultiplier,
            FixedUsdPer1M = null,
            EffectiveFrom = SeedEpoch,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        });

        await context.SaveChangesAsync(cancellationToken);
        return 1;
    }

    /// <summary>
    /// Derive a stable, deterministic UUIDv7-shaped GUID from a name. SHA-256 of
    /// <c>"{namespace}:{name}"</c>, version nibble forced to 7, RFC-4122 variant.
    /// </summary>
    public static Guid DeterministicId(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{IdNamespace}:{name}"));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);

        // RFC-4122: version 7 in the high nibble of byte 6.
        guid[6] = (byte)((guid[6] & 0x0F) | 0x70);
        // RFC-4122 variant (10xx) in the high bits of byte 8.
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);

        return new Guid(guid, bigEndian: true);
    }
}
