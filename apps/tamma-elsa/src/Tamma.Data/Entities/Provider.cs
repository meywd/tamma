namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane PROVIDER entity (Story 34-11): the platform's COST identity
/// for an external LLM provider. Platform-global — NOT tenant-scoped (cost is
/// the provider's published rate, identical for every tenant). AuthModel feeds
/// 32-4 SaaS-eligibility. This is the *cost* primitive; sell price is 34-1
/// (PlanPrice) and markup is 34-5 (MarginPolicy).
///
/// <para>There is deliberately NO <c>TenantId</c>/<c>UserId</c> column: cost is
/// the provider's published rate — identical for every tenant in both modes
/// (design §4.4). BYOK vs platform-provided is a *sell-side* concern owned by
/// 34-3/34-5, never a cost-basis concern.</para>
/// </summary>
public class Provider
{
    /// <summary>UUIDv7 — server default <c>gen_random_uuid()</c>; the seeder bakes deterministic ids.</summary>
    public Guid Id { get; set; }

    /// <summary>Canonical provider key: <c>anthropic|openai|google|openrouter|local|claude-code</c>. Unique.</summary>
    public string Key { get; set; } = null!;

    /// <summary>Display name shown in the admin UI.</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Auth model: <c>api-key</c> | <c>cli-token</c>. Feeds 32-4 SaaS-eligibility
    /// (only <c>api-key</c> providers are SaaS-eligible). A CHECK pins the enum.
    /// </summary>
    public string AuthModel { get; set; } = "api-key";

    /// <summary>Lifecycle: <c>active</c> | <c>retired</c>. A CHECK pins the enum.</summary>
    public string Status { get; set; } = "active";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The per-model versioned cost rows for this provider.</summary>
    public ICollection<ProviderModelPrice> Prices { get; set; } = new List<ProviderModelPrice>();
}
