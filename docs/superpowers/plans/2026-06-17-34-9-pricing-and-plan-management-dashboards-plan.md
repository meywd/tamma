# Story 34-9 — Pricing & Plan Management Dashboards (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Frontend stack is React + Vite + Vitest + Testing Library.

**Epic:** 34 — Pricing, Plans & Entitlements · **Story:** 34-9 · **Priority:** P1 · **Est:** 5-6 days
**Story file:** `docs/stories/epic-34/story-34-9/34-9-pricing-and-plan-management-dashboards.md`

---

## Goal

Ship the two UI surfaces that make Epic 34 operable by humans: (a) an **admin price-book manager**
in `packages/dashboard` (platform owners author plans/features/entitlements/margins/promos/credits
and mint+assign custom enterprise plans) and (b) a **tenant Plan & Pricing page** in
`packages/dashboard-user` (current plan, entitlement-vs-usage headroom bars, per-provider BYOK/
platform toggle, credit balance, cost-estimate widget, upgrade flow with entitlement-delta preview,
trial countdown, promo redeem). Reuse the read APIs from 34-2/34-5/34-6/34-7 and the BYOK mode
endpoint from 34-3.

## Non-goals (YAGNI guard)

- **NO new pricing business logic.** Margin math (34-5), entitlement/headroom resolution (34-6),
  credit netting / trial state (34-7), plan immutability/versioning (34-1) all stay server-side.
  The UI calls those endpoints and renders their numbers verbatim. The ONLY UI-side compute is the
  upgrade entitlement-delta preview, which is a pure set-diff over server-resolved entitlement lists
  + the server `CheckHeadroom` output — not a price calculation.
- **NO EF migrations.** All schema lands in 34-1..34-7. If a consumed read genuinely doesn't exist,
  add a thin read-only projection endpoint (additive, no migration) — and only then.
- **NO new backend services / DCB-event emission from the UI.** The UI triggers endpoints that emit
  events; it asserts (in tests) the right call was made, never appends events itself.
- **NO new npm dependencies.** React Router, Vitest, Testing Library are already wired in both packages.
- **NO billing/charging UI.** Money movement is Epic 35; this is plan/entitlement/estimate presentation.
- **NO alert-channel UI, NO admin SSE.** Out of scope.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Dependency endpoints DO NOT exist yet (this story is correctly sequenced last in Epic 34)

```
$ ls apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs            → No such file
$ ls apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs → No such file
$ ls apps/tamma-elsa/src/Tamma.Api/Services/Pricing/                        → No such directory
```

`Tamma.Data/Entities/` today has `Plan.cs` (the thin opaque-`Quotas` skeleton 34-1 replaces),
`ProviderDiagnostic.cs`, `SecretRow.cs`/`SecretVersionRow.cs`, `TenantMembership.cs`, `Tenant.cs`,
`GitHubInstallationRepo.cs` — but NO `PlanEntitlement`/`PlanPrice`/`MarginPolicy`/`TenantTrial`/
`CreditLedger`/`PromoCode`/`TenantPlanAssignment`/`TenantProviderBilling`. Those are created by
34-1..34-7. **Hard gate: do not start 34-9 until 34-2/3/4/5/6/7 are merged.**

### Authorization policies exist and are the contract (verified `Tamma.Api/Program.cs:966-1016`)

- `PlatformOwnerAccess` (line 986) — JWT `platformRole == platform_admin`. Every `/api/admin/*`
  platform-scoped route uses this. → admin Pricing section.
- `OwnerAccess` (971) — per-tenant `users:manage`. (Used by `AdminTenantsEndpoints`.)
- `SettingsManage` (1001) — `settings:manage` (owner/admin). → tenant pricing mutations.
- `MemberAccess` (991) — any authenticated user. → tenant pricing reads.
- Prompt-store precedent (1006-1016): "endpoint shape identical between modes; auth middleware
  decides which key" — 34-9 follows it for single-user vs SaaS.

### Admin dashboard patterns to mirror (`packages/dashboard/src`)

- `pages/admin/AdminLayout.tsx` — `type AdminTab = 'users' | 'tenants' | ...`; `TABS: TabDef[]`;
  content switch `{activeTab === 'x' && <XTab />}`. **Add `'pricing'` here.** Lazy-loaded by
  `router.tsx` behind `AdminGuard` + `AuthGuard` (admin dashboard is platform-admin scoped).
- `services/admin/admin-tenants-client.ts` — the canonical typed client: module-level `fetchJSON<T>`
  with `credentials: 'include'`, a typed `AdminTenantApiError(status, message, body)`, camelCase wire
  types (default `System.Text.Json`), a `buildQuery(filters)` helper, and a frozen `adminTenantsApi`
  object. **Mirror exactly** for `admin-pricing-client.ts`. Reuse `adminTenantsApi.updatePlan(tenantId, planId)`
  (lines 170-177) for assignment — do NOT add a duplicate.
- `services/secrets/secrets-api-client.ts` — the write-only secret pattern: `CreateSecretBody.plaintext`
  is POSTed; responses carry metadata + a reveal token, never the plaintext. **Mirror** for the BYOK key.
- Tests: colocated `__tests__/*.test.tsx`; `src/test/` has `setup.ts`, `render-helpers.tsx`, `fixtures.ts`.
  `pages/admin/__tests__/AdminLayout.test.tsx` already asserts the tab set — extend it.

### Tenant dashboard patterns to mirror (`packages/dashboard-user/src`)

- `App.tsx` — `<BrowserRouter><AuthProvider><Routes>`; routes nest under
  `<AuthGuard><AppLayout/></AuthGuard>`; admin-only routes add `<TenantAdminGuard>`. **Add
  `/settings/billing` under AuthGuard → AppLayout** (page self-gates mutations by role, like
  `TenantAlertFeed`).
- `api/client.ts` — `ApiClient` with `get/post/put/delete`, `credentials: 'include'`, single-shot
  refresh-on-401, `ApiError`/`UnauthorizedError`. **Build `api/pricing.ts` on `apiClient`** (like
  `api/alerts.ts`).
- `api/alerts.ts` — tenant client precedent: derives nothing from URL the user controls; has a
  client-side `hasPlaintextCredential()` pre-flight that rejects secrets in config blobs (lines
  212-245). **Mirror that pre-flight idea** for the BYOK key field.
- `pages/alerts/TenantAlertFeed.tsx` — the closest UI analog: reads `tenantId`/`role` from
  `useAuth()` (lines 34-37), computes `canMutate = role === 'admin' || role === 'owner'`, hides
  mutate controls for members, surfaces errors via `role="alert"`, uses an inline `Modal`. **Mirror
  this structure** for `PlanPricingPage.tsx`.
- `hooks/useAuth.tsx` — `AuthUser { id, email, displayName, tenantId?, role? }` from `/api/auth/me`.
  `tenantId`/`role` reflect the server's mode + membership. The page never decides mode itself.
- `guards/TenantAdminGuard.tsx` — `ADMIN_OR_HIGHER = {'admin','owner'}`; renders a 403-style panel
  for members rather than redirecting. (Used for routes that are entirely admin; the billing page is
  member-visible read-only, so it self-gates instead of wrapping in this guard.)
- `layouts/AppLayout.tsx` — sidebar nav `<Link>` list (Dashboard/Repositories/Runs/Settings). **Add
  a "Billing" link.**
- Tests: colocated `*.test.tsx`; `src/test/` setup; `useAuth` stubbed per test for the mode/role matrix.

### Mode seam

`useAuth()` → `/api/auth/me` is the UI's single source of mode + identity. Single-user: sole user,
no role gating. SaaS: `tenantId` + `role` populated; `member` read-only. The server
(`ITammaModeProvider` + `RequireTenantMembership` filter + the policies above) is authoritative; the
UI gate is UX-only.

---

## Phased task breakdown (test-first)

### Phase 0 — Preflight gate (no code)

- [ ] **0.1** Verify 34-2/34-3/34-4/34-5/34-6/34-7 are merged: confirm
      `Tamma.Api/Endpoints/PricingEndpoints.cs` + `Endpoints/Admin/AdminPricingEndpoints.cs` exist
      and enumerate their real route paths + DTO shapes (the clients below must match the actual
      wire shapes, not this plan's sketch). Capture any path/shape deltas as the single edit point.
- [ ] **0.2** Run `pnpm test --filter @tamma/dashboard` and `--filter @tamma/dashboard-user` to
      confirm a green baseline before touching anything.

### Phase 1 — Admin typed client (`packages/dashboard`)

- [ ] **1.1 (test first)** `services/admin/admin-pricing-client.test.ts` — assert URL/method/body
      for `listPlans` (incl. `includeDeprecated`/`includeCustom` query), `getPlan`,
      `createPlanVersion`, `updatePlanVersion`, `deprecatePlan` (parses 409 `{affectedTenants,
      requiresForce}`), `mintCustomPlan`, `listMargins`/`upsertMargin`, `listPromos`/`createPromo`,
      `grantCredits`; non-2xx throws the typed error. Mock `fetch`.
- [ ] **1.2** Implement `services/admin/admin-pricing-client.ts` mirroring `admin-tenants-client.ts`
      (`fetchJSON<T>`, `AdminPricingApiError`, camelCase wire types: `PlanSnapshot`, `PlanFeatureDto`,
      `PlanEntitlementDto` keyed by `EntitlementMetricKey`, `PlanPriceDto`, `MarginPolicyDto`,
      `PromoCodeDto`, `CreditLedgerEntryDto`, `DeprecateResult`). Reuse `adminTenantsApi.updatePlan`
      for assignment (import it; do NOT re-declare).
- [ ] **1.3** Verify green: `pnpm test --filter @tamma/dashboard -- admin-pricing-client`.

### Phase 2 — Admin Pricing tab + sub-panels (`packages/dashboard`)

- [ ] **2.1 (test first)** `pages/admin/pricing/__tests__/PlanVersionEditor.test.tsx` — edit
      feature/entitlement/price rows → save calls create/update; saving an active version surfaces
      the "new version created + prior deprecated" result and re-renders the supersede chain;
      deprecate-with-assignments shows the 409 affected count + a "Deprecate anyway (force)" confirm
      that re-calls with `force=true`.
- [ ] **2.2** Implement `PlanVersionEditor.tsx` (entitlement keys constrained to the
      `EntitlementMetricKey` union; immutable-active-version UX; force-deprecate confirm).
- [ ] **2.3 (test first)** `MarginPolicyPanel.test.tsx` — list renders; save validates "at least one
      of MarkupMultiplier / FixedUsdPer1M"; calls `upsertMargin`; surfaces `PRICING.MARGIN.UPDATED`
      success.
- [ ] **2.4** Implement `MarginPolicyPanel.tsx` (scope plan|provider|global, RefKey, multiplier/fixed,
      EffectiveFrom).
- [ ] **2.5 (test first)** `PromoCreditPanel.test.tsx` — promo create validates
      DiscountKind/MaxRedemptions/Expiry client-side; credit grant shows new balance.
- [ ] **2.6** Implement `PromoCreditPanel.tsx`.
- [ ] **2.7 (test first)** `CustomPlanPanel.test.tsx` — mint `IsCustom` plan bound to tenant; assign
      via `adminTenantsApi.updatePlan`; custom plans excluded from public-catalog view; public-surface
      attempt shows the server 400 inline.
- [ ] **2.8** Implement `CustomPlanPanel.tsx`.
- [ ] **2.9 (test first)** `PricingTab.test.tsx` — sub-tab switching (Plans | Margins | Promos &
      Credits | Custom Plans); each sub-panel mounts.
- [ ] **2.10** Implement `PricingTab.tsx` (sub-tab shell composing the four panels).
- [ ] **2.11** Modify `pages/admin/AdminLayout.tsx`: add `'pricing'` to `AdminTab`, a `TABS` entry
      `{ id: 'pricing', label: 'Pricing' }`, and `{activeTab === 'pricing' && <PricingTab />}`.
      Extend `pages/admin/__tests__/AdminLayout.test.tsx` to assert the tab exists + renders.
- [ ] **2.12** Verify green: `pnpm test --filter @tamma/dashboard`.

### Phase 3 — Tenant typed client (`packages/dashboard-user`)

- [ ] **3.1 (test first)** `api/pricing.test.ts` — URL/method/body for `getEntitlements`,
      `getCurrentPlan`, `listPublicPlans`, `estimate` (query-string assembly), `subscribe`,
      `redeemPromo`, `setProviderMode` (PUT body carries `plaintextKey` only on enable; response
      type has NO key field); refresh-on-401 inherited from `ApiClient`; BYOK pre-flight rejects a
      key in a non-key field.
- [ ] **3.2** Implement `api/pricing.ts` on `apiClient` (types: `ResolvedEntitlement` with
      null=unlimited + `currentUsage`/`remaining`/`over` from `CheckHeadroom`; `CurrentPlanResponse`
      incl. `providerModes[]` with `hasSecretRef` boolean + `trial` + `creditBalanceUsd`;
      `EstimateResponse`; `PricingMode`). `plaintextKey` is request-only and never in any response type.
- [ ] **3.3** Verify green: `pnpm test --filter @tamma/dashboard-user -- pricing`.

### Phase 4 — Tenant widgets (`packages/dashboard-user/src/components/pricing/`)

- [ ] **4.1 (test first)** `EntitlementBar.test.tsx` — bar from `remaining`/`over`; over-limit
      styling; `limit === null` → "Unlimited", no bar.
- [ ] **4.2** Implement `EntitlementBar.tsx`.
- [ ] **4.3 (test first, CRITICAL)** `ByokModePanel.test.tsx` — enable BYOK posts the key once; the
      plaintext key NEVER appears in rendered DOM after submit, NEVER in any response fixture, NEVER
      in `console` (spy on `console.*`); post-enable shows only a `hasSecretRef` badge; member role
      renders mode read-only (no toggle); a 403 on mutate surfaces cleanly.
- [ ] **4.4** Implement `ByokModePanel.tsx` (key field in local state, cleared on submit, excluded
      from all logging/error context; client-side pre-flight from `api/pricing.ts`).
- [ ] **4.5 (test first)** `CostEstimateWidget.test.tsx` — renders cost/margin/sell/credits/mode;
      BYOK → zero token markup; unknown model → `PricingUnknownModel` inline (never 0).
- [ ] **4.6** Implement `CostEstimateWidget.tsx`.
- [ ] **4.7 (test first)** `UpgradePlanModal.test.tsx` — entitlement delta preview shows gains AND
      losses vs current resolved set; downgrade that exceeds a new limit flagged; commit calls
      `subscribe`; the server's flagged-violation list surfaces as a non-blocking warning.
- [ ] **4.8** Implement `UpgradePlanModal.tsx` (pure set-diff over resolved entitlement lists; no
      price math).
- [ ] **4.9 (test first)** `TrialBanner.test.tsx` + `PromoRedeemForm.test.tsx` — countdown from
      `EndsAt`; 422 promo reason surfaced; member read-only.
- [ ] **4.10** Implement `TrialBanner.tsx` + `PromoRedeemForm.tsx`.
- [ ] **4.11** Verify green: `pnpm test --filter @tamma/dashboard-user -- components/pricing`.

### Phase 5 — Tenant page + routing (`packages/dashboard-user`)

- [ ] **5.1 (test first)** `pages/settings/PlanPricingPage.test.tsx` — current plan + version render;
      entitlement bars render; unlimited path; empty trial/credits/custom-plan states; **per-mode
      matrix**: single-user (all controls, no gating) vs SaaS owner/admin (controls) vs SaaS member
      (read-only, stubbed 403 surfaces cleanly); `tenantId`/`role` sourced only from `useAuth()`.
- [ ] **5.2** Implement `PlanPricingPage.tsx` composing the widgets; `canMutate = isSingleUser ||
      role ∈ {owner, admin}`; loading/error/empty states like `TenantAlertFeed`.
- [ ] **5.3** Modify `App.tsx`: add `/settings/billing` route under `AuthGuard → AppLayout` (page
      self-gates; not wrapped in `TenantAdminGuard` since members get a read-only view). Modify
      `layouts/AppLayout.tsx`: add a "Billing" sidebar `<Link to="/settings/billing">`.
- [ ] **5.4** Verify green: `pnpm test --filter @tamma/dashboard-user`.

### Phase 6 — Cross-cutting RBAC / isolation hardening + (only-if-needed) backend reads

- [ ] **6.1** Add the negative RBAC tests called out in the story (member can't mutate in either
      dashboard; non-platform-owner can't see the admin Pricing tab; cross-tenant id never derived
      from a URL param). Confirm the BYOK key-leak test asserts absence across DOM + response
      fixtures + console.
- [ ] **6.2 (escape hatch, only if Phase 0.1 found a missing consumed read)** add a thin read-only
      projection endpoint in `PricingEndpoints.cs` (tenant, `MemberAccess`) or
      `AdminPricingEndpoints.cs` (`PlatformOwnerAccess`) over existing entities via
      `ControlPlaneDbContext`. Additive — run `dotnet ef migrations has-pending-model-changes` and
      confirm **none**. Write xUnit endpoint tests in `tests/Tamma.Api.Tests/`. **Skip entirely if
      all reads already exist.**
- [ ] **6.3** Full verification: `pnpm test --filter @tamma/dashboard --filter @tamma/dashboard-user`,
      `pnpm lint`, `pnpm build`; if 6.2 touched C#, `sg docker -c "dotnet test ..."` for the new
      endpoint tests. No success claim without green output (superpowers:verification-before-completion).

---

## Sequencing & dependencies

```
Phase 0 (gate) ─► Phase 1 (admin client) ─► Phase 2 (admin UI)
            └────► Phase 3 (tenant client) ─► Phase 4 (tenant widgets) ─► Phase 5 (tenant page/route)
                                                                                 └► Phase 6 (RBAC + optional backend)
```

- Phase 0 is a HARD gate on 34-2/3/4/5/6/7 being merged.
- Phases 1-2 (admin) and 3-5 (tenant) are independent and can be parallelized across two agents
  after Phase 0 (superpowers:dispatching-parallel-agents) — they share no files.
- Phase 6 runs last (needs both UIs to exist for the cross-cutting tests).

**External deps:** React/Vite/Vitest/Testing Library (present); no new npm packages.
**Internal deps:** 34-2 (plan catalog + custom plans), 34-3 (BYOK mode + secret cabinet),
34-4 (assignment + subscribe), 34-5 (margins + estimate, canonical markup owner), 34-6 (entitlements
+ headroom), 34-7 (trials/credits/promo). Transitively 34-1 (data model + `EntitlementMetricKey`).

---

## Risks + mitigations

- **Dependency endpoints not merged / wire-shape drift (High).** Phase 0 hard-gates and enumerates
  the real routes/DTOs; the typed clients (`admin-pricing-client.ts`, `api/pricing.ts`) are the
  single adjustment point if a path/field differs from this plan's sketch. Do an integration smoke
  test against a running API before UI sign-off.
- **BYOK key leak (Critical).** Write-only field cleared on submit; the response type has no key
  field; a dedicated leak test (Phase 4.3) asserts absence in DOM + response fixtures + console; the
  key is excluded from all logging and error-context objects; the server's reveal-once cabinet (34-3)
  is authoritative.
- **UI re-implements pricing/headroom math (High).** Non-goal + AC-13 forbid it; render server
  numbers verbatim; the only UI compute is the upgrade set-diff (no prices). Code-review checklist item.
- **Member reaches a mutation (High).** Two layers: controls hidden when `!canMutate` AND server 403;
  both tested (never trust the hidden control alone).
- **Single-user vs SaaS mis-gating (Medium).** `canMutate` derives from `useAuth()` which reflects
  the server mode; per-mode test matrix in `PlanPricingPage.test.tsx`.
- **Deprecate-with-assignments confusion (Medium).** Surface affected-tenant count + explicit
  force confirm; no silent force.
- **Accidental backend scope creep (Medium).** Phase 6.2 is an escape hatch only for a genuinely
  missing read; default is zero backend changes, no migration.

---

## Acceptance criteria (mirror of the story)

- [ ] Admin `packages/dashboard`: a Pricing tab (gated by the existing `AdminGuard`; routes
      `PlatformOwnerAccess`) with plan-version editor (features/entitlements/prices, immutable-active
      + supersede chain + 409-force deprecate), margin-policy editor, promo/credit management, and
      custom-plan minting + assignment (via `adminTenantsApi.updatePlan`); non-owner users never see it.
- [ ] Tenant `packages/dashboard-user`: a Plan & Pricing page (`/settings/billing`) showing current
      plan + entitlements with usage-vs-limit bars (via `CheckHeadroom`, unlimited handled),
      per-provider BYOK/platform toggle (`SettingsManage`), credit balance, and a cost-estimate widget
      calling `GET /api/pricing/estimate`.
- [ ] BYOK toggle stores the key via the secret-cabinet-backed 34-3 endpoint and shows mode +
      `hasSecretRef` without EVER displaying/logging the stored key; member role sees read-only state.
- [ ] Upgrade flow: tenant picks a public plan → `POST /api/pricing/subscribe`; entitlement deltas
      (gains/losses, downgrade-over-limit flagged) confirmed before commit using the headroom calc.
- [ ] Trial/promo affordances: redeem-promo input (`POST /api/pricing/promo/redeem`, 422 reason
      surfaced) + trial countdown banner driven by 34-7 data.
- [ ] All pricing UI gated by the same RBAC as the APIs (admin section platform-owner; tenant
      mutations owner/admin; members read-only) and degrades cleanly in single-user mode (no RBAC,
      sole user).
- [ ] Component/E2E tests for: admin plan-edit round-trip, tenant headroom bars, BYOK toggle never
      leaks key, upgrade delta preview, member read-only enforcement.
- [ ] No new server pricing logic; no EF migration; `pnpm test`/`lint`/`build` green for both
      dashboard packages.
