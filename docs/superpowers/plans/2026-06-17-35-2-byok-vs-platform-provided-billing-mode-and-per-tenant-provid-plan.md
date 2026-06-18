# Story 35-2 — BYOK vs Platform-Provided Billing Mode & Per-Tenant Provider Key Cabinet Integration

> Implementation plan · Epic 35 (Billing & Payments, C#) · target `apps/tamma-elsa` · written 2026-06-17 · rev 1.1.0 (2026-06-17): scope-corrected — consume 32-3 keys + 34-3 mode; removed duplicate key resolver and parallel mode store · TDD (test-first) · est. 2-3 days

> **For agentic workers:** REQUIRED SUB-SKILL — use `superpowers:subagent-driven-development`
> (recommended) or `superpowers:executing-plans` to implement this plan phase-by-phase. Phases use
> checkbox (`- [ ]`) tracking. Project is test-first: every phase writes failing tests before
> implementation. Docker-bound suites run via `sg docker -c "dotnet test ..."`; the build itself
> needs no wrapper.

## Goal

Make the BYOK decision **visible on the billing trail**. The mode is already decided by Story 34-3
(`TenantProviderBilling`, the single source of truth) and the key already resolved by Story 32-3
(`IProviderCredentialResolver`, `ProviderCredential.Source`). This story computes a canonical
`billing_mode` token from those two inputs and tags every `LLM.CALL.*` DCB usage event with it (and
stamps the existing `ProviderDiagnostic.BillingMode` column owned by 34-3) so Story 35-3 can split
billable vs non-billable usage with a single column/tag filter — BYOK token usage excluded from
billable metering, platform-provided usage metered + marked up.

## Boundary (canonical — read first)

- **Story 32-3** owns BYOK→platform provider-key resolution into the LLM call path via
  `IProviderCredentialResolver` (`ProviderCredential { ApiKey, Source ∈ {Byok, Platform} }`,
  namespace `Tamma.Api.Services.Providers`). 35-2 **consumes** `Source`; it does **not** read the
  cabinet, resolve keys, or rewrite the proxy `x-api-key`.
- **Story 34-3** owns the pricing-MODE per `(tenant, provider)` via `TenantProviderBilling` +
  `IProviderKeyResolver`/`MetricBillingMode` (`Tamma.Api.Services.Pricing`), the BYOK enable/disable
  endpoints, **and the `ProviderDiagnostic.BillingMode` column + migration**. 35-2 **reads** the mode
  and **writes the column value** on the usage path; it owns no mode field, no mode endpoint, and no
  diagnostic-column migration.

## Non-goals (YAGNI guard)

- **NOT a key-resolution story.** Provider-key resolution from the Epic 29 cabinet into the call path
  is Story 32-3. This story reads no cabinet and handles no key plaintext. (Boundary.)
- **NOT a mode-ownership story.** The per-(tenant,provider) mode source of truth is Story 34-3's
  `TenantProviderBilling`. This story defines **no** `BillingMode` field of its own, **no**
  `/api/v1/billing/mode` endpoint, and **no** `BILLING.MODE.CHANGED` event — mode changes are 34-3's
  BYOK enable/disable endpoints.
- **NO diagnostic-column ownership.** `ProviderDiagnostic.BillingMode` (column + migration) is Story
  34-3's. This story only writes the value on the LLM-call usage path.
- **NO multi-provider chain build-out.** `LlmProxyService` stays Anthropic-shaped; this story changes
  only the *tagging* of usage, not the provider matrix (Epic 1/9 territory).
- **NO metering / invoicing logic.** Producing the `billing_mode` signal is in scope; *consuming* it
  to split billable usage is Story 35-3.
- **NO single-user billing.** Single-user mode skips the markup/mode tagging — registration-time
  Null seam, no runtime branching.
- **NO CLI/token agent provider BYOK.** SaaS provider auth is API-key only; `ICLIAgentProvider` stays
  single-user/self-hosted.
- **NO new secret-handling primitives.** 35-2 never touches the cabinet; key plaintext lives only in
  32-3's resolver.

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists today

| Component | Path | Notes |
|---|---|---|
| LLM proxy | `src/Tamma.Api/Services/SaaS/LlmProxyService.cs` | Records `ProviderDiagnostic` via `IDiagnosticsService.RecordEventAsync` (line ~213-237). **Emits no DCB `LLM.CALL.*` events** — this story adds those. The outbound key is resolved by **Story 32-3's** `IProviderCredentialResolver` (not this story). Budget check at ~58-68. |
| LLM proxy contract | `src/Tamma.Api/Services/SaaS/ILlmProxyService.cs` | `ChatAsync(ChatRequest, Guid? tenantId, ct)` — tenant id already threaded. |
| Diagnostic entity | `src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Has `ProviderKey`, `Model`, `TenantId`, `InputTokens`/`OutputTokens`, `Cost`. The **`BillingMode` column is added by Story 34-3** — this story only writes its value on the usage path. |
| **Mode source of truth (34-3)** | `src/Tamma.Api/Services/Pricing/` — `IProviderKeyResolver`/`ProviderKeyResolver` reading `TenantProviderBilling`; `ProviderKeyResolution(ApiKey, MetricBillingMode Mode, SecretName)` | **Story 34-3.** 35-2 reads `.Mode` for the tag. Do not duplicate. |
| **Credential source (32-3)** | `src/Tamma.Api/Services/Providers/` — `IProviderCredentialResolver`; `ProviderCredential { ApiKey, Source ∈ {Byok, Platform} }` | **Story 32-3.** 35-2 reads `.Source` to reconcile the tag. Do not duplicate. |
| DCB events | `src/Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync(DomainEvent)`; `src/Tamma.Data/Entities/DomainEvent.cs` (`Type`, `TenantId`, `Tags`, `Metadata`, `Data` JSONB strings) | CP-resident store the alert/metering evaluators poll. Emit pattern in `OrgEndpoints.cs` ~1043 (`new DomainEvent { Type, TenantId, Tags = JsonSerializer.Serialize(...), Data = ... }`). |
| Mode (process) | `src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider.Mode` (`SingleUser`/`SaaS`), process-stable | Drives the Null-seam registration. |
| Provider chain | `src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` | Reads `agent_configs.config` chains; writes diagnostics on the chain path — needs the same `billing_mode` tag stamped. |
| Tests | `tests/Tamma.Api.Tests/SaaS`, `tests/Tamma.Api.Tests/Providers` | New `Billing/` test folder. |

> No reference is made to the deleted `packages/api`. All paths are under `apps/tamma-elsa/`.

### What the prerequisite stories provide (hard blockers)

- **Story 32-3** — `IProviderCredentialResolver` + `ProviderCredential.Source` (`byok|platform`). The
  LLM call path's key resolution. 35-2 consumes `Source`.
- **Story 34-3** — `TenantProviderBilling` (single source of truth for mode), `IProviderKeyResolver` /
  `MetricBillingMode`, the BYOK enable/disable endpoints, and the `ProviderDiagnostic.BillingMode`
  column + migration. 35-2 reads the mode and writes the column value.
- **Story 35-1** — `IBillingProvider`/`NullBillingProvider`, the `src/Tamma.Api/Services/Billing/`
  directory + the Null-seam registration pattern. **All `Services/Billing/*` paths assume that
  directory already exists.** If 35-1 is not merged, stop and land it first.

### Key gap this story closes

Today's proxy writes diagnostics with no billing dimension and emits no usage DCB event, so there is
no way to tell a BYOK call (no token markup) from a platform call (billable) off the trail. This plan
adds (1) a `billing_mode` *tagger* that reads 34-3's mode and reconciles 32-3's credential source, and
(2) the `LLM.CALL.*` DCB usage-event emission tagged with that token (plus stamping 34-3's existing
diagnostic column). It adds **no** key resolver and **no** mode store.

## Architecture

```
Mode change          ──► owned by Story 34-3 (POST/DELETE …/pricing/providers/{providerKey}/byok)
                         writes TenantProviderBilling — NOT this story.

Key resolution       ──► owned by Story 32-3 (IProviderCredentialResolver.ResolveAsync)
                         returns ProviderCredential { ApiKey, Source } — NOT this story.

LlmProxyService.ChatAsync (+ ProviderChainResolver path):
   key  ◄── 32-3 IProviderCredentialResolver  (ApiKey + Source)        [consumed]
   tag  ◄── IBillingModeTagger.ResolveTagAsync(tenantId, providerKey, source)   [this story]
              ├─ read 34-3 IProviderKeyResolver → ProviderKeyResolution.Mode (byok|platform)
              ├─ reconcile with 32-3 source: disagree ⇒ 32-3 wins + WARN + BILLING.MODE.MISMATCH
              └─ validate token ∈ {byok, platform}
   ──► ProviderDiagnostic.BillingMode = tag          (column owned by 34-3; this story writes value)
   ──► IEventRepository.AppendAsync(LLM.CALL.SUCCESS|FAILED, Tags = { tenantId, billing_mode, provider, model })
```

Per-mode ownership (mandatory two-scoping answer): single-user ⇒ no billing dimension, `NullBillingModeTagger`,
no billable-mode implication (34-3 yields platform for single-user). SaaS ⇒ tenant owns mode via 34-3's
`TenantProviderBilling` (35-2 reads it); the key is 32-3's. Tenant isolation is inherited: 34-3's
resolver reads only the calling tenant's row; the usage event/diagnostic is stamped with the call
context's tenant id, never a client-supplied id.

## Phased task breakdown (TDD)

### Phase 1 — `IBillingModeTagger` + `BillingModeTagger` + tokens/events

**Files:** `src/Tamma.Api/Services/Billing/IBillingModeTagger.cs` (contract),
`BillingModeTagger.cs`, `BillingModeTokens.cs` (`byok`/`platform` constants),
`BillingModeEvents.cs` (`LLM.CALL.SUCCESS`/`LLM.CALL.FAILED`/`BILLING.MODE.MISMATCH` constants).

**Tests first:** `tests/Tamma.Api.Tests/Billing/BillingModeTaggerTests.cs` —
(a) faked 34-3 `IProviderKeyResolver` mode `byok` ⇒ tag `byok`; (b) mode `platform` ⇒ `platform`;
(c) 32-3 `credentialSource` supplied + agreeing ⇒ tag matches, **no** mismatch event;
(d) 32-3 source disagrees with 34-3 ⇒ 32-3 wins + WARN + exactly one `BILLING.MODE.MISMATCH`;
(e) resolved token not in `{byok,platform}` ⇒ ERROR (AC11).

**Approach:** Inject 34-3's `IProviderKeyResolver`, `IEventRepository` (mismatch event), `ILogger`.
`ResolveTagAsync(tenantId, providerKey, credentialSource?)` reads `ProviderKeyResolution.Mode`,
reconciles against `credentialSource` (32-3), validates the token. **No cabinet, no key plaintext.**

### Phase 2 — `NullBillingModeTagger` + DI extension + Program wiring

**Files:** `src/Tamma.Api/Services/Billing/NullBillingModeTagger.cs`,
`src/Tamma.Api/Extensions/BillingModeServiceCollectionExtensions.cs` (`AddBillingModeTagging()`),
`Program.cs` (call `AddBillingModeTagging()`).

**Tests first:** `tests/Tamma.Api.Tests/Billing/BillingModeTaggerTests.cs` (single-user case) —
`ITammaModeProvider.Mode == SingleUser` ⇒ `NullBillingModeTagger` yields platform semantics, no
billable-mode implication, no mismatch event.

**Approach:** Extension registers `BillingModeTagger` in SaaS and `NullBillingModeTagger` in
single-user (same Null-seam pattern as Story 35-1's `NullBillingProvider`); handlers never branch on
mode. **No HTTP endpoint is mapped** — 35-2 exposes no mutating route.

### Phase 3 — Wire `LlmProxyService` (tag diagnostic value + emit DCB usage events)

**Files:** `src/Tamma.Api/Services/SaaS/LlmProxyService.cs` (inject `IBillingModeTagger` +
`IEventRepository`); `src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` (stamp same tag on
the chain-path diagnostic); `Program.cs` (constructor DI). The outbound key is **already** resolved by
Story 32-3's `IProviderCredentialResolver` — this story does not touch key resolution.

**Tests first:** `tests/Tamma.Api.Tests/Billing/LlmProxyServiceBillingModeTests.cs` —
(a) Byok call ⇒ `diag.BillingMode == "byok"` + `LLM.CALL.SUCCESS` tagged `billing_mode=byok`; the key
comes from a faked **32-3** `IProviderCredentialResolver` (assert 35-2 resolves no key itself);
(b) platform call ⇒ `"platform"`; (c) over-budget ⇒ `LLM.CALL.FAILED` with the same tag;
(d) single-user ⇒ no billable-mode implication.
Plus `tests/Tamma.Api.Tests/Billing/ProviderChainBillingModeTests.cs` — chain path stamps the same
token; and `tests/Tamma.Api.Tests/Billing/BillingModeRedactionTests.cs` — capture logs + usage-event
payloads; assert no key string ever appears (35-2 holds no plaintext).

**Approach:** After the call (key from 32-3), call `_tagger.ResolveTagAsync(tenantId, providerKey,
credentialSource)`; stamp `diag.BillingMode = token` (column owned by 34-3); append
`LLM.CALL.SUCCESS|FAILED` with `Tags = { tenantId, billing_mode, provider, model }`. Keep budget
check + error shape unchanged.

### Phase 4 — Full-suite green + docs

**Tests:** run `sg docker -c "dotnet test apps/tamma-elsa"` (Api.Tests at minimum). No migration is
added by this story (the `ProviderDiagnostic.BillingMode` column is Story 34-3's); confirm
`has-pending-model-changes` is clean — if it is not, the model change belongs to 34-3, not here.

**Approach:** Fix any cross-cutting fallout (DI registration order, existing `LlmProxyServiceTests`
needing the new ctor deps `IBillingModeTagger`/`IEventRepository`). Update Epic 35 status note if one
exists.

## Sequencing & dependencies

```
Phase 1 (tagger) ──► Phase 2 (Null seam + DI) ──► Phase 3 (proxy/chain wiring) ──► Phase 4 (suite green)
```

- **Hard prerequisites:** Story 32-3 (`IProviderCredentialResolver`), Story 34-3
  (`TenantProviderBilling` mode + `ProviderDiagnostic.BillingMode` column), Story 35-1
  (`Services/Billing` dir + Null-seam pattern). Block on all three.
- Phases are sequential: the tagger (1) is registered (2) then consumed by the proxy/chain (3); suite
  green (4) last.
- **Feeds Story 35-3** (consumes the `billing_mode` tag + the populated diagnostic value).

## Risks + mitigations

| Risk | Sev | Mitigation |
|---|---|---|
| 35-2 accidentally reimplements key resolution or a mode store | High | Plan + story Boundary make 32-3 (keys) and 34-3 (mode + diagnostic column) the only owners; 35-2 has no cabinet dep and no mode endpoint; tests assert the key comes from the faked 32-3 resolver. |
| 34-3 not merged ⇒ `TenantProviderBilling` / `ProviderDiagnostic.BillingMode` missing | High | Plan opens by checking 34-3 (and 32-3, 35-1) are merged; do not stub the entity or add the column here. |
| 34-3 mode disagrees with 32-3 credential source at call time | Med | Reconcile: 32-3 source wins (it is the wire credential) + WARN + `BILLING.MODE.MISMATCH` event for audit; no silent mistag. |
| Provider key plaintext leaks via 35-2 | Low | 35-2 never holds plaintext (32-3 owns it); `BillingModeRedactionTests` asserts no key in 35-2 logs/payloads. |
| Diagnostic/event store topology shift (Story 28-1 / Epic 30 per-tenant events) | Med | `billing_mode` is a value on 34-3's existing column + a tag on tenant-scoped DCB events appended via the CP `IEventRepository` today — later re-routing leaves the value/tag unchanged. |
| Existing `LlmProxyServiceTests` break on new ctor deps | Low | Phase 3/4 update the test fixtures with faked `IBillingModeTagger` + `IProviderCredentialResolver` (32-3) + `IEventRepository`. |
| Spurious `has-pending-model-changes` because 34-3's column not yet applied | Low | This story adds no model change; if drift appears it belongs to 34-3 — land 34-3's migration first, do not generate one here. |

## Acceptance criteria (mirror of the story)

- [ ] `IBillingModeTagger` computes the `billing_mode` token by **reading** Story 34-3's mode (`IProviderKeyResolver`/`TenantProviderBilling`) and reconciling Story 32-3's `ProviderCredential.Source`; 35-2 defines no mode field and no mode-switch endpoint.
- [ ] On 34-3 mode ≠ 32-3 source: 32-3 source wins for the tag, WARN logged, one `BILLING.MODE.MISMATCH` DCB event emitted.
- [ ] Every `LLM.CALL.SUCCESS|FAILED` usage event is tagged `billing_mode` (`byok|platform`), `provider`, `model`, `tenantId`; the same token is written into 34-3's `ProviderDiagnostic.BillingMode` column (this story writes the value, does not own/migrate the column).
- [ ] The LLM call path obtains its key from Story 32-3's `IProviderCredentialResolver` — 35-2 resolves no key, reads no cabinet, rewrites no `x-api-key`.
- [ ] The usage DCB event emission is added by this story (none exists today) via tenant-scoped `IEventRepository.AppendAsync`.
- [ ] `billing_mode` is read-only w.r.t. 34-3: 35-2 never writes `TenantProviderBilling` / `BillingCustomer.BillingMode` and exposes no mutating mode route; mode changes stay on 34-3's BYOK endpoints.
- [ ] Token is constrained to `byok` or `platform`; any other value is a logged ERROR, not a silent tag.
- [ ] No provider-key plaintext is read/logged/serialised by 35-2; redaction test asserts absence in 35-2 logs/payloads.
- [ ] Single-user mode skips the billable-mode tagging (Null seam, no implication); SaaS provider auth is API-key only; CLI/token providers out of scope.
- [ ] Unit tests: tagger mode reads + mismatch reconciliation, usage-event/diagnostic tagging, Byok call flagged non-billable-for-tokens, single-user no-op. Integration tests: tenant isolation of the tag, mode round-trip (34-3 enable/disable reflected on next call).
- [ ] Full `dotnet test` suite green; this story adds no migration; `has-pending-model-changes` clean (any drift belongs to 34-3).
