using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — one measured usage event to be priced. Built from a
/// <c>ProviderDiagnostic</c> row (its <c>InputTokens</c> / <c>OutputTokens</c> /
/// <c>ProviderKey</c> / <c>Model</c> / billing-mode columns) or the equivalent
/// DCB usage event emitted in Epic 32-9. Input and output tokens are carried
/// separately because <c>IProviderPricingService</c> bills them at different
/// rates.
/// </summary>
/// <param name="Provider">Canonical provider key (e.g. <c>anthropic</c>).</param>
/// <param name="Model">Model id (e.g. <c>claude-sonnet-4-20250514</c>); null/"default" resolves to the provider's first model.</param>
/// <param name="InputTokens">Prompt tokens billed at the input rate (clamped to &gt;= 0).</param>
/// <param name="OutputTokens">Completion tokens billed at the output rate (clamped to &gt;= 0).</param>
/// <param name="PricingMode">Platform-provided (markup applied) vs BYOK (token sell price 0).</param>
/// <param name="OccurredAt">UTC instant the call happened — drives timestamp-effective margin-policy selection.</param>
public sealed record UsageLine(
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    PricingMode PricingMode,
    DateTime OccurredAt);
