using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Story 34-11 — idempotently ports the frozen provider COST rate sheet
/// (<c>ProviderPricingService</c>'s <c>FrozenDictionary</c>) into the
/// control-plane <c>providers</c> + <c>provider_model_prices</c> tables as v1
/// seed rows. Each provider becomes a <see cref="Provider"/> row (carrying its
/// <c>AuthModel</c>); each <c>(model, Rate)</c> becomes a v1
/// <see cref="ProviderModelPrice"/> row (<c>Status='active'</c>,
/// <c>Source='seed'</c>, <c>EffectiveFrom = SEED_EPOCH</c>), with USD-per-token
/// re-expressed as USD-per-1M.
///
/// <para><b>Insert-missing-only, per row</b> (mirrors <see cref="PlansSeeder"/>
/// and the convention system-defaults ownership rule): a row is inserted only
/// when absent. A re-run is a no-op and NEVER reverts a <c>Source='admin'</c>
/// row — re-pricing happens through the admin write path, not the seeder.</para>
///
/// <para><b>Deterministic UUIDv7-shaped ids.</b> .NET 8 lacks
/// <c>Guid.CreateVersion7</c>, so seed ids are derived deterministically from a
/// stable namespace + name (SHA-256), with the RFC-4122 version nibble forced
/// to 7 and the variant bits set. Deterministic ids let the insert-missing-only
/// check key off a known id and keep FK targets stable across environments.</para>
///
/// <para><b>This seeder reads <c>ProviderPricingService</c> as data ONLY.</b>
/// The static frozen table is the seed source; the seeder lives in
/// <c>Tamma.Data</c> and cannot reference the <c>Tamma.Api</c> service, so the
/// frozen rate sheet is mirrored here as a private snapshot that the
/// ProviderPricingParityTests pin byte-identical to the live service.</para>
/// </summary>
public static class ProviderPricingSeeder
{
    /// <summary>
    /// The fixed seed epoch every v1 row carries as <c>EffectiveFrom</c>. A
    /// stable past instant so the EffectiveFrom window always selects a v1 row
    /// for any realistic <c>OccurredAt</c>. NEVER change — it is the version-1
    /// boundary the time-travel resolver anchors on.
    /// </summary>
    public static readonly DateTime SeedEpoch =
        new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>DNS-style namespace for deterministic seed ids. NEVER change.</summary>
    private const string IdNamespace = "tamma.provider-cost.v1";

    /// <summary>
    /// Inserts any missing provider + price rows. Safe to call on every startup
    /// — per-row existence checks make re-runs a no-op and never revert admin
    /// edits. Returns the number of price rows inserted.
    /// </summary>
    public static async Task<int> SeedAsync(
        ControlPlaneDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = DateTime.UtcNow;
        var insertedPrices = 0;

        var existingProviderKeys = await context.Providers
            .AsNoTracking()
            .Select(p => p.Key)
            .ToListAsync(cancellationToken);
        var providerKeySet = existingProviderKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Per-row idempotency keyed off the deterministic id — never a
        // whole-table short-circuit (so a partially-seeded DB backfills).
        var existingPriceIds = await context.ProviderModelPrices
            .AsNoTracking()
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var priceIdSet = existingPriceIds.ToHashSet();

        foreach (var spec in s_seed)
        {
            if (!providerKeySet.Contains(spec.Key))
            {
                context.Providers.Add(new Provider
                {
                    Id = DeterministicId($"provider:{spec.Key}"),
                    Key = spec.Key,
                    DisplayName = spec.DisplayName,
                    AuthModel = spec.AuthModel,
                    Status = "active",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            // Stamp each model with a deterministic sub-millisecond offset off
            // the seed epoch, in the frozen-table declaration order. This makes
            // ORDER BY EffectiveFrom reproduce the frozen insertion order so the
            // null/"default"→first-model rule resolves to the SAME model in the
            // DB-backed resolver as in the frozen table (the parity contract).
            // The offsets are tiny (1 tick = 100ns each) and stay within the
            // seed epoch — well before any admin re-price, so the EffectiveFrom
            // resolution window is unaffected.
            var ordinal = 0;
            foreach (var (model, in1M, out1M) in spec.Models)
            {
                // 1 microsecond (10 ticks) per step — Postgres `timestamptz` is
                // microsecond-precision, so a sub-microsecond offset would round
                // away and collapse the ordering.
                var effectiveFrom = SeedEpoch.AddTicks(ordinal * 10L);
                ordinal++;

                var id = DeterministicId($"price:{spec.Key}:{model}");
                if (priceIdSet.Contains(id))
                {
                    continue;
                }

                context.ProviderModelPrices.Add(new ProviderModelPrice
                {
                    Id = id,
                    ProviderKey = spec.Key,
                    Model = model,
                    InputUsdPer1M = in1M,
                    OutputUsdPer1M = out1M,
                    CacheReadUsdPer1M = null,
                    CacheWriteUsdPer1M = null,
                    EffectiveFrom = effectiveFrom,
                    Status = "active",
                    Source = "seed",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                insertedPrices++;
            }
        }

        if (insertedPrices > 0 || context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return insertedPrices;
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

        // Construct from the BIG-ENDIAN field layout so the canonical string
        // form shows the version nibble (7) and variant in the standard RFC-4122
        // positions — i.e. a true UUIDv7-shaped id, not the mixed-endian default
        // Guid(ReadOnlySpan<byte>) would produce.
        return new Guid(guid, bigEndian: true);
    }

    // The frozen rate sheet mirrored here as USD-per-1M (the storage unit). The
    // ProviderPricingParityTests assert byte-for-byte parity between this seed
    // and the live ProviderPricingService.Compute output for every pair.
    private sealed record ProviderSeed(
        string Key,
        string DisplayName,
        string AuthModel,
        IReadOnlyList<(string Model, decimal In1M, decimal Out1M)> Models);

    private static readonly IReadOnlyList<ProviderSeed> s_seed = BuildSeed();

    private static IReadOnlyList<ProviderSeed> BuildSeed()
    {
        var anthropic = new (string, decimal, decimal)[]
        {
            ("claude-sonnet-4-20250514", 3.00m, 15.00m),
            ("claude-opus-4-20250514", 15.00m, 75.00m),
            ("claude-3-5-sonnet-20241022", 3.00m, 15.00m),
            ("claude-3-5-haiku-20241022", 0.80m, 4.00m),
            ("claude-3-opus-20240229", 15.00m, 75.00m),
            ("claude-3-sonnet-20240229", 3.00m, 15.00m),
            ("claude-3-haiku-20240307", 0.25m, 1.25m),
        };

        return
        [
            new ProviderSeed("anthropic", "Anthropic", "api-key", anthropic),
            new ProviderSeed("openai", "OpenAI", "api-key", new (string, decimal, decimal)[]
            {
                ("gpt-4o", 2.50m, 10.00m),
                ("gpt-4o-mini", 0.15m, 0.60m),
                ("gpt-4-turbo", 10.00m, 30.00m),
                ("gpt-4", 30.00m, 60.00m),
                ("gpt-3.5-turbo", 0.50m, 1.50m),
                ("o1-preview", 15.00m, 60.00m),
                ("o1-mini", 3.00m, 12.00m),
            }),
            new ProviderSeed("google", "Google", "api-key", new (string, decimal, decimal)[]
            {
                ("gemini-1.5-pro", 1.25m, 5.00m),
                ("gemini-1.5-flash", 0.075m, 0.30m),
                ("gemini-2.0-flash", 0.10m, 0.40m),
            }),
            new ProviderSeed("openrouter", "OpenRouter", "api-key", new (string, decimal, decimal)[]
            {
                ("anthropic/claude-3.5-sonnet", 3.00m, 15.00m),
                ("anthropic/claude-3-opus", 15.00m, 75.00m),
                ("anthropic/claude-3-haiku", 0.25m, 1.25m),
                ("openai/gpt-4o", 2.50m, 10.00m),
                ("openai/gpt-4o-mini", 0.15m, 0.60m),
                ("meta-llama/llama-3.1-405b-instruct", 2.70m, 2.70m),
                ("meta-llama/llama-3.1-70b-instruct", 0.52m, 0.75m),
                ("meta-llama/llama-3.1-8b-instruct", 0.055m, 0.055m),
                ("mistralai/mistral-large", 2.00m, 6.00m),
                ("mistralai/mixtral-8x7b-instruct", 0.24m, 0.24m),
            }),
            // claude-code uses Anthropic pricing (CLI harness — cli-token).
            new ProviderSeed("claude-code", "Claude Code", "cli-token", anthropic),
            new ProviderSeed("local", "Local", "api-key", new (string, decimal, decimal)[]
            {
                ("local", 0m, 0m),
                ("default", 0m, 0m),
            }),
        ];
    }
}
