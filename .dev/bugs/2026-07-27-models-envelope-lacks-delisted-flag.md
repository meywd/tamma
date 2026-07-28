# Bug: Models envelope carries no synthesized/delisted flag — the "no longer listed" marker is only heuristically detectable client-side

**Date Discovered**: 2026-07-27
**Reporter**: Claude (Story 46-2 implementation)
**Severity**: 🟢 Low
**Status**: ✅ Resolved

## 📋 Summary

Story 46-2 AC2 (and 46-3's tenant picker, same envelope) requires the model picker to mark the
currently-effective model with "no longer listed by the provider" when the provider's live list
no longer carries it. The landed 46-0 envelope cannot express that distinction: when the current
model is delisted, `ProviderAdminEndpoints.BuildModelsResponse` **synthesizes** an entry
(`DisplayName: null, Deprecated: false, Current: true`) and prepends it at index 0 — but the
`ProviderModelEntry` record (`Id`, `DisplayName`, `Deprecated`, `Current`) has no field saying
"this entry was synthesized / is absent from the live list". A listed current model and a
synthesized one are structurally identical for providers whose lists carry no display names.

## 🔍 Details

### Affected Components
- API: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderAdminEndpoints.cs` —
  `BuildModelsResponse()` (used by BOTH the admin route and the tenant models route) and the
  `ProviderModelEntry` record
- UI consumer: `packages/dashboard/src/pages/admin/providers/ModelPicker.tsx`
  (`currentDelisted` heuristic; carries a code comment pointing here)
- Consumer (landed with 46-3): `packages/dashboard-user/src/pages/models/TenantModelPicker.tsx`
  — `isCurrentDelisted()` heuristic (exported + unit-tested; carries a code comment pointing
  here). NOTE: 46-3 chose the opposite trade-off from 46-2's heuristic below — it additionally
  requires at least one non-current entry to carry a display name (or sole-entry + failed fetch),
  so display-name-less providers (OpenAI/Groq/DeepSeek) get a **false negative** (marker missing
  on a genuinely delisted model) instead of 46-2's false positive. Both heuristics collapse to
  the same plain flag read once the proposed field lands.

### The heuristic the UI ships with (46-2)

Delisted ⇔ fresh response (`stale: false`, `errorCode: null`) AND `models.length > 1` AND the
`current: true` entry sits at index 0 with `displayName: null`.

### False positive / negative cases

- **False positive**: providers whose lists carry no display names at all (per the Epic 46
  README wire survey: OpenAI, Groq, DeepSeek — every entry has `displayName: null`) when the
  current model genuinely IS the first entry of the provider's own list order.
- **False negative**: none known — synthesis always produces exactly the index-0/null-name shape.

## 💥 Expected Behavior

The envelope states the fact instead of leaving the UI to infer it, e.g.
`ProviderModelEntry.Listed: bool` (or `Synthesized: bool`) set `false` only on the injected
entry. One field, both routes, both UIs; the 46-2/46-3 heuristics then become a plain flag read.

## 🐛 Actual Behavior

The UI infers delisting positionally/structurally and can mis-mark a legitimately-listed current
model for display-name-less providers.

## 🔧 Proposed Solution

Add the boolean to the C# record + `BuildModelsResponse`, extend the 46-0 endpoint tests
(delisted-current case already exists there to pin the synthesis), then swap the dashboard(-user)
heuristics for the flag. Backward-compatible additive field; no migration.

Not fixed in 46-2 because the fix is C# (outside the story's `packages/dashboard` file lane) and
the response DTOs are the fixtures' source of truth — inventing the field client-side first would
repeat the Epic 45 (45-1) fixture-drift failure in mirror image.

## 🔗 Related

- Story: `docs/stories/epic-46/story-46-2/46-2-platform-admin-provider-settings-ui.md` (AC2)
- Epic: `docs/stories/epic-46/README.md` — D6 (fail-soft envelope contract)
- Backend commit that landed the envelope: `1d6f1e3`

## ✅ Resolution

**Resolution Date**: 2026-07-28
**Resolution**: Fixed as proposed — additive `Delisted` flag on the envelope; both
client heuristics deleted.

- **C#**: `ProviderModelEntry` gained `bool Delisted = false`
  (`[JsonIgnore(WhenWritingDefault)]` — the wire carries `"delisted":true` on the
  synthesized entry and omits the field on genuinely-listed entries, so
  absent/false both read as "listed"). `BuildModelsResponse` sets it `true` only
  on the entry it prepends. One field, both routes (admin + tenant), both UIs.
- **dashboard (46-2)**: `ModelPicker.tsx`'s index-0/null-displayName heuristic
  deleted — `currentDelisted` is now `currentEntry?.delisted === true`.
  `providers-api-client.ts` mirrors the field as `delisted?: boolean`.
- **dashboard-user (46-3)**: the exported `isCurrentDelisted()` helper deleted
  (nothing else used it) — the picker reads the flag directly.
  `provider-models.ts` mirrors the field.
- **Tests**: `ProviderModelCatalogTests` injection tests pin `delisted` true only
  on synthesized entries, false on a genuinely-listed nameless first entry (the
  46-2 false positive, now distinguishable), and the wire omission of `false`.
  Both UI suites fixture the flag; the 46-2 false-positive and 46-3
  false-negative shapes are pinned as regression tests (marker only with the
  flag). The 58 provider golden fixtures and the no-row byte-identity suite are
  untouched; the DTO reflection-scan hygiene test still passes.
