# Epic 46 backend implementation — defects found OUTSIDE the implementing agent's file lane

**Date**: 2026-07-27
**Status**: 🐛 Open (each item is outside the 46-0/46-1 backend agent's file lane — recorded, not fixed)
**Reporter**: Epic 46 backend agent (stories 46-0 + 46-1)

## 1. Story 46-0 AC4's route `GET /api/admin/providers` is already taken (story defect)

`docs/stories/epic-46/story-46-0/46-0-live-model-listing-seam.md` AC4 specifies the provider
status roster at `GET /api/admin/providers`. That route ALREADY EXISTS: Story 34-11's provider
COST price-book roster maps it at `apps/tamma-elsa/src/Tamma.Api/Program.cs`
(`admin.MapGet("/providers", AdminProviderPricingEndpoints.ListProviders)` on the `/api/admin`
group). Mapping both would be an ambiguous-match failure at request time, and changing the
34-11 response shape would break the existing pricing dashboard (`pages/admin/pricing/`).

**Resolution taken (deviation, in-lane):** the 46-0 status roster is mounted at
`GET /api/admin/providers/status` instead (same group, same policy, same response contract).
The sub-routes are as specified (`/{key}/models`, `/{key}/settings`).

**Action for the story owner:** update the 46-0 story + epic README route tables (docs are
outside the backend agent's lane), and make sure 46-2's admin UI binds `/status`.

## 2. `Tamma.ElsaServer/appsettings.json` LlmProviders examples carry rotten model ids

Story 46-1 AC7 requires the shipped `LlmProviders` config EXAMPLES
(`apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json:64-89`) to be refreshed alongside the
descriptor defaults. That file is outside the backend agent's lane (`Tamma.ElsaServer/**`), so
the examples still carry:

- `LlmProviders:anthropic:DefaultModel = "claude-sonnet-4-20250514"` — deprecated dated
  snapshot (Anthropic's migration guide lists Claude Sonnet 4's retirement as 2026-06-15,
  already past; the catalogue descriptor now ships `claude-sonnet-4-5`).
- `LlmProviders:openrouter:DefaultModel = "anthropic/claude-sonnet-4-20250514"` — a dated
  Anthropic-style slug; OpenRouter marketplace slugs are undated and dot-formed
  (the descriptor now ships `anthropic/claude-sonnet-4.5`).

Note these are config EXAMPLES — with the sections present, config outranks the descriptor
(precedence step 3), so a deployment using this file verbatim still pins the rotten ids until
either the file is refreshed or an admin sets a platform row through the new
`PUT /api/admin/providers/{key}/settings` (which now outranks config — epic 46 D2).

## 3. Observation (not a defect fix): provider-session `"default"` model label

`apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:689` labels a provider session
`"default"` when the create request names no model. Reviewed during the 46-1 D5 chain audit:
this is a session LABEL (cost/diagnostic attribution), not an egress model default — it does
not bypass `GetDefaultModel`. Left unchanged; flagging so a future reader doesn't mistake the
literal `"default"` for a resolvable model id.

## Related

- `docs/stories/epic-46/story-46-0/46-0-live-model-listing-seam.md` (AC4)
- `docs/stories/epic-46/story-46-1/46-1-persisted-model-selection.md` (AC7)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderAdminEndpoints.cs` (the /status deviation)
