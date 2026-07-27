# Bug: Tenant surface cannot learn the platform-default model while an override is active — 46-3's reset confirm can only name it opportunistically

**Date Discovered**: 2026-07-27
**Reporter**: Claude (Story 46-3 implementation)
**Severity**: 🟢 Low
**Status**: 🐛 Open

## 📋 Summary

Story 46-3 AC3 requires the "Use platform default" confirm to *name the platform default it will
fall back to ("from the row's resolved data")*. The landed tenant contract cannot always supply
that name: while a tenant override exists, every tenant-facing read resolves THROUGH the override
(tenant-override wins the 46-1 precedence), so the "row's resolved data" IS the override —

- `GET /api/v1/agents/providers/models` → `TenantProviderRosterRow.Model` = the override
- `GET /api/v1/agents/providers/{provider}/model` → `Model` = the override, `Override` = the same
  string; there is **no `platformDefault` / fallback field** in either DTO
  (`ProviderCredentialEndpoints.cs:649-661`)

The platform routes that do expose the platform layer (`/api/admin/providers/*`) are
platform-owner-gated, so the tenant app cannot consult them.

## 🐛 Actual Behavior / shipped mitigation

`ModelSettingsPage` captures the resolved model of any row whose `source != 'tenant-override'` —
the only moment the client can see the fallback — and names it in the reset confirm when known
(e.g. the admin set the override earlier in the same page session). For a row that ALREADY has an
override when the page loads, the confirm stays generic ("will fall back to the platform
default." with no model id). Pinned by tests in
`packages/dashboard-user/src/pages/models/TenantModelPicker.test.tsx`
("reset confirm stays generic when the platform default is not client-knowable").

## 💥 Expected Behavior

The server states the fallback instead of leaving the client to remember it, e.g. an additive
`fallbackModel` (+ optionally `fallbackSource`) on `TenantProviderModelResponse` and/or the
roster row — computed server-side by running the 46-1 resolver with the tenant layer skipped.
One field; the confirm then always names the model, matching AC3's full intent.

## 🔧 Proposed Solution

Additive DTO field on the tenant model routes (C#, `ProviderCredentialEndpoints.cs` +
`InlineToolLoopRunner.ResolveDefaultModelWithSource` gaining a skip-principal overload), then
drop the client-side `platformDefaults` capture in `ModelSettingsPage.tsx`. Backward-compatible;
no migration. Not fixed in 46-3 because the fix is C# — outside the story's
`packages/dashboard-user` file lane — and inventing the field client-side would repeat the 45-1
fixture-drift failure.

## 🔗 Related

- Story: `docs/stories/epic-46/story-46-3/46-3-tenant-model-settings-ui.md` (AC3)
- Epic: `docs/stories/epic-46/README.md` — resolution precedence (D2), RBAC table (D3)
- Backend commit that landed the tenant routes: `1d6f1e3`
- Sibling envelope gap: `.dev/bugs/2026-07-27-models-envelope-lacks-delisted-flag.md`
