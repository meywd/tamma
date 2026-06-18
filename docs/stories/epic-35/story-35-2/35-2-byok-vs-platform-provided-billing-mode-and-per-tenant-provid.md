# Story 35-2: BYOK vs Platform-Provided Billing Mode & Per-Tenant Provider Key Cabinet Integration

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base usage rules, TRACE/DEBUG logging requirements, the test-first (TDD) workflow, the 100% critical-path coverage requirement, and build-success enforcement.

## User Story

As a **tenant owner of a Tamma SaaS organization**,
I want every LLM usage record and billing event to carry the billing mode (BYOK vs platform-provided) that already governed the call — the mode declared on Story 34-3's `TenantProviderBilling` and the credential source resolved by Story 32-3's `IProviderCredentialResolver`,
so that BYOK usage is billed as a platform/seat fee only (no token markup) while platform-provided usage is metered and marked up — and so Story 35-3 can split billable from non-billable token usage with a single tag filter.

## Priority

P0 — Required so Story 35-3 (BYOK-aware usage metering) can split billable from non-billable token usage. Without a billing-mode tag stamped onto the usage trail there is no correct basis to invoice.

## Boundary (READ FIRST)

This story owns the **billing TREATMENT of modes only** — it does **not** own key resolution or the mode source of truth:

- **Story 32-3** owns BYOK→platform provider-key resolution from the Epic 29 secret cabinet into the LLM call path via `IProviderCredentialResolver` (`Tamma.Api.Services.Providers`), returning `ProviderCredential { ApiKey, Source ∈ {Byok, Platform}, ... }`. 35-2 **consumes** that `Source`; it does **not** read the cabinet or rewrite the proxy's `x-api-key` resolution.
- **Story 34-3** owns the pricing-MODE selection per `(tenant, provider)` via the `TenantProviderBilling` entity and its `IProviderKeyResolver`/`ProviderKeyResolution` (`Tamma.Api.Services.Pricing`) — the single source of truth for BYOK-vs-platform mode — plus the BYOK enable/disable endpoints and the `ProviderDiagnostic.BillingMode` column. 35-2 **reads** the resolved mode from 34-3; it does **not** define a competing per-tenant mode field or endpoint, and it does **not** add the diagnostic column.

35-2's sliver: propagate the already-resolved mode (34-3) + credential source (32-3) onto the **usage/billing DCB event** as a `BillingMode` tag so Story 35-3 meters ONLY platform-provided usage and excludes BYOK token usage from billable metering.

## Acceptance Criteria

1. A new `IBillingModeTagger` seam (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingModeTagger.cs`) computes the canonical `billing_mode` token (`byok | platform`) for a `(tenantId, providerKey)` LLM call by **reading** Story 34-3's mode source of truth — `IProviderKeyResolver.ResolveAsync(...)` / the `TenantProviderBilling` row (`Tamma.Api.Services.Pricing`) — and reconciling it with Story 32-3's resolved credential source (`ProviderCredential.Source`) when that is already on the call context. 35-2 defines **no** mode field or mode-switch endpoint of its own.
2. When both signals are present they MUST agree (34-3 mode == 32-3 source); a mismatch is logged WARN, the **32-3 credential source wins** for the tag (it is the source actually used for the wire call), and a `BILLING.MODE.MISMATCH` DCB diagnostic event is emitted so the divergence is auditable.
3. The `billing_mode` token is propagated onto the **usage DCB event** emitted on the LLM path: every `LLM.CALL.SUCCESS` and `LLM.CALL.FAILED` event carries `billing_mode` (`byok | platform`), `provider`, `model`, and `tenantId` in its JSONB `Tags`, so Story 35-3's metering/alert evaluators can split usage off the event stream without a join.
4. The same `billing_mode` token is written into the existing `ProviderDiagnostic.BillingMode` column (the column is **owned and created by Story 34-3** — this story only ensures the value on the LLM-call usage path matches the tag on the DCB event; it does **not** add or migrate the column).
5. The LLM call path (`LlmProxyService.ChatAsync`, plus the agent provider-chain diagnostic path via `ProviderChainResolver`) obtains its provider key from Story 32-3's `IProviderCredentialResolver` — 35-2 **does not** resolve keys, read the cabinet, or rewrite the `x-api-key` header itself. If a call site does not yet consume 32-3's resolver, the billing-mode tag is derived from 34-3's mode alone (AC1) without 35-2 introducing a competing key path.
6. The DCB usage event emission on the LLM path is added by this story (the current `LlmProxyService` records a `ProviderDiagnostic` but emits no `LLM.CALL.*` DCB event); events are appended via `IEventRepository.AppendAsync` to the tenant-scoped control-plane `DomainEvents` store (`DomainEvent.TenantId` set).
7. The `billing_mode` token written to both the event tag and the diagnostic column is **read-only** with respect to 34-3 — 35-2 never writes `TenantProviderBilling`, never writes `BillingCustomer.BillingMode`, and exposes no `PUT/POST/DELETE` route that changes a tenant's mode. Mode changes remain Story 34-3's BYOK enable/disable endpoints.
8. **Single-user mode** (`ITammaModeProvider.Mode == TammaMode.SingleUser`) treats all usage as self-owned: the tagger resolves to `platform` semantics from 34-3 (single-user always platform) and the markup-relevant `billing_mode` tagging is skipped — usage events/diagnostics for single-user calls carry no billable-mode implication. Registration uses a Null seam (mirroring Story 35-1's `NullBillingProvider`); request handlers never branch on mode.
9. No provider key plaintext is read, handled, logged, or serialised by this story: key plaintext lives only inside Story 32-3's resolver. A redaction unit test asserts that no key string appears in any captured log output or in any usage/diagnostic response body produced on the 35-2 path.
10. SaaS provider auth is **API-key only** (BYOK secrets are `SecretPurpose.ApiKey`, owned by 32-3/34-3); the CLI/token agent providers (`ICLIAgentProvider`) remain a single-user/self-hosted concern and are explicitly out of scope for the SaaS billing-mode tag.
11. `BillingMode` tag values are constrained to exactly `byok` or `platform` (matching 32-3's `CredentialSource` and 34-3's `MetricBillingMode` tokens); any other resolved value is rejected with a logged ERROR rather than silently tagged.
12. Unit tests cover: tagger reads 34-3 mode = `byok` → tag `byok`; 34-3 mode = `platform` → tag `platform`; 32-3 source present and agreeing → tag matches; 32-3 source disagreeing with 34-3 → 32-3 wins + WARN + `BILLING.MODE.MISMATCH` event; single-user → no billable-mode implication; a `byok`-tagged usage event/diagnostic is read by Story 35-3 as non-billable-for-tokens.
13. Unit tests assert the usage event tagging: a `byok` call ⇒ `LLM.CALL.SUCCESS` tagged `billing_mode=byok` and `ProviderDiagnostic.BillingMode == "byok"`; a `platform` call ⇒ `billing_mode=platform`; an over-budget/failed call ⇒ `LLM.CALL.FAILED` carrying the same `billing_mode` tag.
14. Integration tests (xUnit, control-plane DbContext on a real Postgres via the existing docker-bound test harness) cover tenant isolation of the tag: the `billing_mode` resolved for tenant A reflects A's `TenantProviderBilling` mode and never B's; the usage event/diagnostic for a call is tagged with the calling tenant's mode only. Stripe and the upstream provider HTTP handler are mocked.

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/Tamma.Api/Services/Billing/
  IBillingModeTagger.cs            # NEW — seam: compute billing_mode token for (tenant, provider)
  BillingModeTagger.cs             # NEW — READS 34-3 mode (IProviderKeyResolver) + reconciles 32-3 source
  NullBillingModeTagger.cs         # NEW — single-user no-op (no billable-mode implication)
  BillingModeEvents.cs             # NEW — LLM.CALL.SUCCESS/FAILED + BILLING.MODE.MISMATCH constants
  BillingModeTokens.cs             # NEW — "byok" | "platform" constants (match 32-3/34-3 tokens)

apps/tamma-elsa/src/Tamma.Api/Extensions/
  BillingModeServiceCollectionExtensions.cs   # NEW — AddBillingModeTagging(); single-user registers Null seam

apps/tamma-elsa/src/Tamma.Api/Services/SaaS/
  LlmProxyService.cs               # MODIFY — tag usage DCB event + diagnostic with billing_mode (read from tagger);
                                   #          key comes from 32-3's IProviderCredentialResolver, NOT this story
  ILlmProxyService.cs              # (unchanged signature; behaviour change only)
```

> **Not owned by this story (consumed only):**
> - `IProviderCredentialResolver` + `ProviderCredential { ApiKey, Source }` (`Tamma.Api.Services.Providers`) — **Story 32-3**. The LLM call path's `x-api-key` is resolved there; 35-2 reads `Source`.
> - `TenantProviderBilling` entity + `IProviderKeyResolver`/`ProviderKeyResolution` + `MetricBillingMode` enum + BYOK enable/disable endpoints (`Tamma.Api.Services.Pricing`) — **Story 34-3**. The per-(tenant,provider) mode is read from here.
> - `ProviderDiagnostic.BillingMode` column + its migration — **created by Story 34-3**. 35-2 only writes the value on the LLM-call usage path; it does **not** add or migrate the column.
>
> All names verified against the current tree. `IBillingProvider`/`NullBillingProvider` and the `Services/Billing/` directory are **created by Story 35-1** (prerequisite). `Services/SaaS/LlmProxyService.cs`, `Services/Providers/ProviderChainResolver.cs`, `Data/Repositories/IEventRepository.cs`, `Data/Entities/DomainEvent.cs`, and `Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`) all exist today. No reference is made to the deleted `packages/api`.

### Billing-mode tagger seam

```csharp
namespace Tamma.Api.Services.Billing;

public interface IBillingModeTagger
{
    /// <summary>Compute the canonical billing_mode token ("byok" | "platform")
    /// for an LLM call. READS Story 34-3's TenantProviderBilling mode (via
    /// IProviderKeyResolver). If Story 32-3's resolved ProviderCredential.Source
    /// is supplied, it is reconciled: on disagreement the 32-3 source wins (it is
    /// the credential actually used on the wire) + WARN + BILLING.MODE.MISMATCH.
    /// This seam OWNS no mode — it never writes TenantProviderBilling and never
    /// reads or returns a key plaintext.</summary>
    Task<string> ResolveTagAsync(
        Guid? tenantId,
        string providerKey,
        string? credentialSource = null,   // from 32-3 ProviderCredential.Source when available
        CancellationToken ct = default);
}
```

`BillingModeTagger`:

1. Calls Story 34-3's `IProviderKeyResolver.ResolveAsync(tenantId, providerKey)` (or its mode-only read) and takes `ProviderKeyResolution.Mode` (`MetricBillingMode` → `byok`/`platform`). **No cabinet read, no key plaintext handled here** — 34-3 owns that path.
2. If `credentialSource` (from 32-3's `ProviderCredential.Source`) is non-null and disagrees with the 34-3 mode: log WARN, prefer the 32-3 source for the tag, and emit a `BILLING.MODE.MISMATCH` DCB diagnostic event (`Tags = { tenantId, provider, mode34, source32 }`).
3. Validate the resulting token is exactly `byok` or `platform` (AC11) — anything else is an ERROR, not a silent tag.
4. Single-user (`ITammaModeProvider.Mode == SingleUser`) ⇒ `NullBillingModeTagger` returns the platform semantics 34-3 already yields for single-user; the markup-relevant tagging is skipped.

### LLM-call wiring (consume, don't reimplement)

`LlmProxyService.ChatAsync` (and the `ProviderChainResolver` chain path) already obtain — or will obtain, once Story 32-3 lands — their outbound key from **32-3's `IProviderCredentialResolver`**. This story does **not** add a key resolver and does **not** rewrite the `x-api-key` resolution. 35-2's only change to the call path is:

- After the credential is resolved (by 32-3) and the call completes, call `IBillingModeTagger.ResolveTagAsync(tenantId, providerKey, credentialSource)` to obtain the `billing_mode` token.
- Stamp `diag.BillingMode = token` on the `ProviderDiagnostic` row (column owned by 34-3).
- Append the usage DCB event (`LLM.CALL.SUCCESS`/`FAILED`) with `billing_mode` in `Tags` (this event emission is added by this story — see below).

If a call site has not yet been migrated to 32-3's resolver, the tag is derived from 34-3's mode alone (AC5) — 35-2 never introduces a competing key path to fill the gap.

### EF migration

**None.** The `ProviderDiagnostic.BillingMode` column and its index are added by **Story 34-3** (`<ts>_AddProviderDiagnosticBillingMode.cs` under `Migrations/ControlPlane/` per 34-3's plan). This story adds no entity column and no migration; it only writes the existing column on the LLM-call usage path. If 34-3 has not landed the column, block on 34-3.

### DCB event names

| Event | When | TenantId | Tags | Data |
|---|---|---|---|---|
| `LLM.CALL.SUCCESS` | successful proxied call | tenant | `{ tenantId, billing_mode, provider, model }` | `{ inputTokens, outputTokens, totalTokens, costUsd, durationMs }` |
| `LLM.CALL.FAILED` | failed/over-budget/upstream-error call | tenant | `{ tenantId, billing_mode, provider, model, reason }` | `{ durationMs }` |
| `BILLING.MODE.MISMATCH` | 34-3 mode ≠ 32-3 credential source on a call | tenant | `{ tenantId, provider, mode34, source32 }` | `{ observedAt }` |

Event types follow the `AGGREGATE.ACTION.STATUS` convention. `billing_mode` is the same `byok|platform` token written to the `ProviderDiagnostic.BillingMode` column (owned by 34-3), so the metering reader (35-3) can read it off either the event stream or the table. **There is no `BILLING.MODE.CHANGED` event in this story** — mode changes are owned by Story 34-3's BYOK enable/disable endpoints.

> The current `LlmProxyService` records diagnostics but does not emit DCB `LLM.CALL.*` events. This story adds those emissions (via injected `IEventRepository`) so 35-3 has an event-stream basis in addition to the diagnostics table.

### API shape

**This story exposes no mutating HTTP endpoint.** It defines no `/api/v1/billing/mode` route — the per-(tenant,provider) mode is set via **Story 34-3's** `POST/DELETE /api/v1/orgs/{tenantId}/pricing/providers/{providerKey}/byok` and read via 34-3's `GET …/pricing/providers`. 35-2 is a producer of usage-event tags only.

### Per-mode + per-tenant handling

| Concern | single-user (`TammaMode.SingleUser`) | SaaS (`TammaMode.SaaS`) |
|---|---|---|
| Who owns the billing mode? | N/A — no billing dimension; the sole user owns all usage | The tenant, via **Story 34-3's** `TenantProviderBilling` (35-2 only reads it) |
| Mode-change route | n/a | **Story 34-3's** `…/pricing/providers/{providerKey}/byok` (not this story) |
| Provider key source | **Story 32-3** (`IProviderCredentialResolver`) — local/platform | **Story 32-3** — BYOK tenant cabinet key → platform key |
| Diagnostic `BillingMode` (column owned by 34-3) | null (no billing implication) | `byok` or `platform`, written by 35-2 on the call path |
| `billing_mode` usage-event tag | absent | `byok` or `platform` |
| Markup applied (35-3) | never | `platform` only |
| Mode source of truth | `ITammaModeProvider.Mode` (single-user ⇒ platform) | 34-3 `TenantProviderBilling.Mode` |

Tenant isolation is inherited: 34-3's `IProviderKeyResolver` only reads the calling tenant's `TenantProviderBilling` row, and the usage event/diagnostic is stamped with the tenant on the call context, never a client-supplied id.

### Integration points

- **Story 32-3 (consumed)** supplies `IProviderCredentialResolver` + `ProviderCredential.Source`. The LLM call path resolves its key there; 35-2 reads `Source` to reconcile the tag. 35-2 adds **no** key resolution.
- **Story 34-3 (consumed)** supplies `TenantProviderBilling` (mode source of truth), `IProviderKeyResolver`/`ProviderKeyResolution`/`MetricBillingMode`, the BYOK enable/disable endpoints, and the `ProviderDiagnostic.BillingMode` column + migration. 35-2 reads the mode and writes the column value on the usage path; it owns none of these.
- **Story 35-1** supplies `IBillingProvider`/`NullBillingProvider` and the `Services/Billing/` directory. This story extends, never re-creates, those.
- **Story 35-3 (feeds)** consumes the `ProviderDiagnostic.BillingMode` value and the `LLM.CALL.*` `billing_mode` tag to split billable vs non-billable token usage. This story is the producer of the tag; 35-3 is the consumer.
- **`ProviderChainResolver`** (agent provider-chain path): the same `billing_mode` token is threaded onto the diagnostic that the chain path already writes, so chain-routed calls are tagged consistently with the proxy path.

## Dependencies

**Internal (prerequisite / consumed — hard blockers):**
- **Story 32-3** (credential source) — `IProviderCredentialResolver` + `ProviderCredential.Source` (`byok | platform`). The LLM call path's key resolution lives here; 35-2 reads `Source`. 35-2 MUST NOT reimplement key resolution.
- **Story 34-3** (mode) — `TenantProviderBilling` (single source of truth for BYOK-vs-platform mode), `IProviderKeyResolver`/`MetricBillingMode`, the BYOK enable/disable endpoints, **and the `ProviderDiagnostic.BillingMode` column + migration**. 35-2 reads the mode and writes the column value; it owns none of these.
- **Story 35-1** — `IBillingProvider`/`NullBillingProvider`, the `Services/Billing/` directory. Hard blocker for the directory + Null-seam pattern.

**Internal (feeds / blocks):**
- **Story 35-3** (BYOK-aware usage metering) — depends on the `billing_mode` tag (on `LLM.CALL.*`) + the populated `ProviderDiagnostic.BillingMode` value this story produces, so it meters ONLY platform-provided usage and excludes BYOK token usage from billable metering.

**Internal (supporting):**
- **Epic 27 (RBAC/ownership pattern)** — the `tenant_owner`/`tenant_admin`/`member` matrix; relevant only because 34-3's mode-change endpoints (not this story's) gate on it.

**External:**
- PostgreSQL 17 control-plane DbContext (existing) for integration tests.
- The upstream provider HTTP handler — mocked here; no live calls.

## Testing Strategy

**Unit tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):
1. `BillingModeTaggerTests` — 34-3 mode `byok` (faked `IProviderKeyResolver`) ⇒ tag `byok`; 34-3 mode `platform` ⇒ tag `platform`; 32-3 `credentialSource` supplied and agreeing ⇒ tag matches, no mismatch event; 32-3 source disagreeing with 34-3 ⇒ 32-3 wins + WARN + one `BILLING.MODE.MISMATCH` event; non-`byok|platform` token ⇒ ERROR (AC11); single-user (`NullBillingModeTagger`) ⇒ platform semantics, no billable-mode implication.
2. `LlmProxyServiceBillingModeTests` — Byok call ⇒ diagnostic `BillingMode == "byok"` + `LLM.CALL.SUCCESS` tagged `billing_mode=byok`; platform call ⇒ `"platform"`; over-budget ⇒ `LLM.CALL.FAILED` carrying the same tag; the test asserts 35-2 reads the key via the faked **32-3** `IProviderCredentialResolver` and never resolves a key itself.
3. `BillingModeRedactionTests` — capture logger output + response/usage-event payloads across a full Byok call; assert no key string ever appears (AC9). (Plaintext is owned by 32-3; this proves 35-2's path leaks nothing.)
4. `ProviderChainBillingModeTests` — a chain-routed call writes the same `billing_mode` token onto the chain-path diagnostic, consistent with the proxy path.

**Integration tests** (`Tamma.Api.Tests`, docker-bound, real Postgres control-plane):
5. **Tenant isolation of the tag**: seed tenant A `TenantProviderBilling` mode `byok`, tenant B `platform`; assert the `billing_mode` resolved/stamped for A's call is `byok` and reflects only A's row, B's only `platform`.
6. Round-trip: after 34-3 sets a tenant to `byok`, the next LLM call's `ProviderDiagnostic.BillingMode` and `LLM.CALL.SUCCESS` tag both read `byok`; after 34-3 disables BYOK, they read `platform`.
7. Single-user mode: `BillingModeServiceCollectionExtensions` registers `NullBillingModeTagger` ⇒ usage events/diagnostics carry no billable-mode implication.

**Mocks:** **32-3** `IProviderCredentialResolver` faked (returns `{ApiKey, Source}`); **34-3** `IProviderKeyResolver` faked (returns `ProviderKeyResolution.Mode`); upstream provider via `HttpMessageHandler` stub. No secret-cabinet fake is needed in 35-2 tests — 35-2 reads no cabinet.

Coverage targets per `CLAUDE.md`: 80% line / 75% branch / 85% function; tag resolution + mismatch reconciliation + redaction are critical paths → 100%.

## Estimated Effort

2-3 days (reduced from 4-5: key resolution is consumed from 32-3 and the mode store + diagnostic column from 34-3, so this story is the tagger + usage-event wiring only)

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingModeTagger.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingModeTagger.cs` | Create (reads 34-3 `IProviderKeyResolver`; reconciles 32-3 `ProviderCredential.Source`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/NullBillingModeTagger.cs` | Create (single-user no-op) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingModeEvents.cs` | Create (`LLM.CALL.*`, `BILLING.MODE.MISMATCH` constants) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingModeTokens.cs` | Create (`byok`/`platform` constants) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingModeServiceCollectionExtensions.cs` | Create (`AddBillingModeTagging`; single-user Null seam) |
| `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs` | Modify (tag diagnostic + emit `LLM.CALL.*` DCB; key comes from 32-3 resolver) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` | Modify (stamp same `billing_mode` on chain-path diagnostic) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (call `AddBillingModeTagging`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingModeTaggerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/LlmProxyServiceBillingModeTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/ProviderChainBillingModeTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingModeRedactionTests.cs` | Create |

> **Not created/modified here (owned elsewhere):** `ProviderDiagnostic.cs` / `TammaModelConfiguration.cs` / the `AddProviderDiagnosticBillingMode` migration (Story 34-3); `IProviderCredentialResolver` (Story 32-3); any `/api/v1/billing/mode` endpoint (removed — mode changes are Story 34-3's BYOK endpoints).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (especially Story 32-3 credential-resolution and Story 34-3 pricing-mode foundations)
3. Confirmed Story 32-3 (`IProviderCredentialResolver`) and Story 34-3 (`TenantProviderBilling` + `ProviderDiagnostic.BillingMode` column) are merged — this story consumes both and must not reimplement them
4. Reviewed Story 34-3's `IProviderKeyResolver` (mode read) and Story 32-3's `ProviderCredential.Source` as the two inputs to the tag
5. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **Consume the mode and key, don't reimplement them.** The mode is read from Story 34-3's `TenantProviderBilling` (via `IProviderKeyResolver`); the key/credential source is Story 32-3's `IProviderCredentialResolver`. 35-2 adds neither a key resolver nor a mode store — it only computes and stamps a `billing_mode` tag.
- **Billing mode is a tag on the usage event + the existing diagnostic value, not a join.** Writing `billing_mode` onto the `LLM.CALL.*` event tags and into 34-3's `ProviderDiagnostic.BillingMode` column lets Story 35-3 split usage with no extra lookup — the producer pays the small denormalisation cost once.
- **Reconcile, don't duplicate, the two signals.** When both 34-3's mode and 32-3's credential source are present they should agree; on disagreement the 32-3 source (the credential actually used on the wire) wins for the tag, WARN is logged, and a `BILLING.MODE.MISMATCH` event makes the divergence auditable.
- **Single-user is a registration-time no-op, not a runtime branch storm.** `AddBillingModeTagging` registers `NullBillingModeTagger` in single-user mode — the same pattern Story 35-1 uses for `NullBillingProvider` — so request handlers never branch on mode.

### Boundary Note (honored exactly)

This story **owns the BILLING TREATMENT of modes only**: stamp the resolved mode + credential source onto the usage/diagnostic record and the DCB billing tag so Story 35-3 meters ONLY platform-provided usage and excludes BYOK token usage from billable metering.

It is **not** a key-resolution story — provider-key resolution from the Epic 29 cabinet into the LLM call path is **Story 32-3** (`IProviderCredentialResolver`, `ProviderCredential.Source`). It is **not** a mode-ownership story — the pricing-MODE per `(tenant, provider)` is **Story 34-3** (`TenantProviderBilling`, the single source of truth), which also owns the `ProviderDiagnostic.BillingMode` column and the BYOK enable/disable endpoints. 35-2 reads both and produces the `billing_mode` signal that 35-3 meters against. The multi-provider chain build-out remains Epic 1/9 territory.

### Security requirements

- No provider-key plaintext is read, handled, logged, or serialised by this story — plaintext lives only inside Story 32-3's resolver. 35-2 handles only the `byok`/`platform` token.
- The `billing_mode` token is constrained to exactly `byok` or `platform`; any other value is an ERROR, never a silent tag.
- Tenant isolation is inherited from 34-3's resolver (reads only the calling tenant's `TenantProviderBilling` row) and from the call context's tenant id; 35-2 introduces no client-supplied tenant path.

## Logging Requirements

- **INFO**: billing-mode tag resolved (`tenantId`, `provider`, `billingMode` — **no key**); LLM call completed (`tenantId`, `billingMode`, `model`, `totalTokens`, `costUsd`).
- **DEBUG**: billing-mode tagging started (`tenantId`, `provider`); usage event/diagnostic stamped (`tenantId`, `billingMode`, `tokensUsed`).
- **WARN**: 34-3 mode disagrees with 32-3 credential source (`tenantId`, `provider`, `mode34`, `source32`) — 32-3 source wins; over-budget LLM rejection.
- **ERROR**: resolved `billing_mode` token is neither `byok` nor `platform`; DCB append failure on the usage/mismatch event.
- **Structured context**: include `{ tenantId, billingMode, provider, model }` where applicable.
- **Credential safety**: NEVER log any provider key or secret plaintext (35-2 never holds one). Redaction is asserted by `BillingModeRedactionTests`.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
| 2026-06-17 | 1.1.0   | Scope-corrected: consume 32-3 keys + 34-3 mode; removed duplicate key resolver and parallel mode store | Claude |
