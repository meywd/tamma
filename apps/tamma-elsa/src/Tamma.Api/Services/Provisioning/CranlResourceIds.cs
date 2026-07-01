using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Epic 30 Phase B (Task B3) — typed read/write helper for the Cranl
/// walk / resume working-state that used to live in the dedicated
/// <c>tenants.cranl_*</c> columns (<c>CranlProjectId</c>,
/// <c>CranlDatabaseId</c>, <c>CranlAppId</c>, <c>CranlAppUrl</c>,
/// <c>CranlRegion</c>). Those columns were dropped in B3; the state now
/// lives in the <c>tenants.provider_resource_ids</c> JSONB shadow column
/// (a flat string→string map).
///
/// <para><b>Why a shared helper.</b> Two collaborators read/write the same
/// map and must not drift: <see cref="CranlProvisioningWorkflow"/>
/// accumulates the ids as the multi-step Cranl REST walk progresses (a
/// <c>provisioning.tenant</c> platform task can be re-reserved mid-walk and
/// must resume by reading the ids it already minted), and
/// <see cref="V2.Cranl.CranlTenantProviderV2"/> reads them back for the
/// idempotency guard, the <c>ProviderResourceIds</c> result contract, and
/// endpoint building. Keeping the key names + (de)serialisation in one place
/// prevents a writer/reader mismatch.</para>
///
/// <para><b>Encrypted DB URL is deliberately NOT in this map.</b> The Cranl
/// admin <c>DATABASE_URL</c> is a secret; it lives encrypted-at-rest on the
/// <c>tenant_databases</c> pool row's <c>AdminConnectionStringEncrypted</c>
/// (minted by B2) and is re-derived transiently by polling the Cranl DB on
/// each <c>DatabaseReady</c> pass. It is never persisted on the tenant row.</para>
///
/// <para><b>Storage semantics.</b> The map is stored via the EF change
/// tracker's shadow property so we can persist it without adding it to the
/// <see cref="Tenant"/> POCO; a subsequent <c>SaveChangesAsync</c> flushes
/// it. An empty map is stored as <c>null</c> (so the column reads back as
/// SQL NULL rather than <c>"{}"</c>), matching how a fresh tenant looks.</para>
/// </summary>
internal static class CranlResourceIds
{
    public const string ProjectId = "cranl_project_id";
    public const string DatabaseId = "cranl_database_id";
    public const string AppId = "cranl_app_id";
    public const string AppUrl = "cranl_app_url";
    public const string Region = "cranl_region";

    /// <summary>Shadow-property name of the <c>tenants.provider_resource_ids</c>
    /// JSONB column (CLR type <c>string?</c>).</summary>
    private const string ShadowProperty = "ProviderResourceIds";

    /// <summary>
    /// Read the resource-id map from the JSONB shadow column. Returns a
    /// fresh empty (ordinal) map when the column is null/empty. Throws
    /// (fail-loud) if the column holds malformed JSON — a corrupt row must
    /// not be silently treated as "no ids" (that would restart the walk and
    /// leak a duplicate project/db/app).
    /// </summary>
    public static Dictionary<string, string> Read(EntityEntry<Tenant> entry)
    {
        var json = entry.Property<string?>(ShadowProperty).CurrentValue;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return map is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(map, StringComparer.Ordinal);
    }

    /// <summary>
    /// Read a single id, or <c>null</c> when absent/empty. The empty-string
    /// coalescing keeps the call sites' <c>string.IsNullOrEmpty(...)</c>
    /// resume guards behaving exactly as the old column reads did.
    /// </summary>
    public static string? Get(EntityEntry<Tenant> entry, string key)
        => Read(entry).TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    /// <summary>
    /// Upsert a single id and write the map back to the shadow column. A
    /// null/empty <paramref name="value"/> removes the key (mirrors clearing
    /// a column to NULL). The write is buffered on the change tracker; the
    /// caller's next <c>SaveChangesAsync</c> persists it.
    /// </summary>
    public static void Set(EntityEntry<Tenant> entry, string key, string? value)
    {
        var map = Read(entry);
        if (string.IsNullOrEmpty(value))
        {
            map.Remove(key);
        }
        else
        {
            map[key] = value;
        }

        Write(entry, map);
    }

    /// <summary>Replace the whole map (used by deprovision teardown, which
    /// keeps only the region hint).</summary>
    public static void Write(EntityEntry<Tenant> entry, IReadOnlyDictionary<string, string> map)
    {
        entry.Property<string?>(ShadowProperty).CurrentValue =
            map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }
}
