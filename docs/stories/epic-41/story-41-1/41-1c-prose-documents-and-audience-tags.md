# Story 41-1c: Prose Documents & Audience Tags — the mechanism eight stories assume

Status: drafted

*Split from 41-1 — see [the enabler-set umbrella](./41-1-team-role-and-document-type-extensions.md).*

## User Story

As the **Epic 41 prose family** (ADR, postmortem, release notes, changelog, user/API docs, runbook,
roadmap, stakeholder update, retro narrative), I want prose to be a first-class thing the document
lifecycle can produce, review, accept, persist and retrieve — carrying an **audience tag** and a **kind**
but **no forced schema** — so that "prose rides the lifecycle with an audience tag" describes the code
instead of describing an intention.

## Priority

**P0 — Wave 0.** Eight stories (41-4, 41-5, 41-8, 41-9, 41-22, 41-24, 41-25, 41-26) are written against
this mechanism as if it had shipped. It has not, and before this split no story owned building it.

> **Corrected — prose support does not exist and Epic 39 never chartered it.** Six Epic 41 stories say
> `produces: prose (…, audience=…)` and the epic README called it "Epic 39: *prose stays prose*". In
> Epic 39 that is a **principle only** (`epic-39/README.md:115-116`: "Tech-writer outputs … are markdown
> with an audience tag — no forced structure"), and **39-1:58 records prose/tech-writer output as
> explicitly OUT OF SCOPE of the 10-type table**. In code: `DocumentTypeKey.cs:22-33` has exactly ten
> members and none is prose; `DocumentInstance.cs:23-89` has seventeen properties and no audience column;
> `DocumentEnvelope.cs` has no audience field. (`Audience` does appear elsewhere in `src/` —
> `ChannelOutboxMessage.Audience`, `ITaskAudienceResolver`/`AudienceMember`, JWT audience — none of them
> a document tag.) Epic 41's old Scope item 4 said audience tags would be *"extended"*; there is nothing
> to extend.

## Scope

1. **A prose document type.** A `prose` member on `DocumentTypeKey` + a `ProseDocumentType`
   `IDocumentType` registered in `DocumentTypeRegistry`, whose payload is `{ kind, audience, title, body }`
   with **`body` unvalidated markdown**. Validation asserts envelope-level facts only (kind and audience
   in vocabulary, non-empty body, `issueId`/`repository` lineage present) — never structure inside the
   prose.
2. **`Audience` as an envelope + store field**, not only a payload key: `DocumentEnvelope.Audience`,
   `DocumentInstance.Audience` + EF configuration + migration, so the store and the 39-11 lineage API can
   filter by audience without parsing bodies.
3. **The two vocabularies**, each a `[Wire]`-tagged enum with a drift test, seeded from the actual
   consumers: **audience** = `engineering` (41-9, 41-22) · `developer` (41-24 changelog, 41-25 api-docs) ·
   `user` (41-24 release notes, 41-25 user-docs) · `ops` (41-26) · `stakeholder` (41-4, 41-5) · `team`
   (41-8). **kind** = `adr` · `postmortem` · `release-notes` · `changelog` · `user-docs` · `api-docs` ·
   `runbook` · `roadmap` · `status-update` · `retro-narrative`.
4. **Review over prose.** Confirm the 39-7 review path produces a `Review` whose `ParentDocumentId` is the
   prose document, with a body that has no schema to critique against; the prose contract renderer
   instructs the *shape convention* (e.g. ADR context/decision/consequences) as guidance, not as a
   validated schema.
5. **Acceptance posture for prose** — see D2.

## Design decisions to record

- **D1 — a registered type, not an untyped body.** Rule 1 of the epic requires every producing workflow to
  declare `produces: <DocumentType>`, and `DocumentInstance.DocumentType` is a `DocumentTypeKey` wire
  string. Modelling prose outside the registry would mean a second persistence path, a second review path
  and no lineage — so prose becomes a *type whose body is unvalidated*, which is the narrowest change
  that honours "prose stays prose". Recorded here so the alternative is not silently re-opened.
- **D2 — prose acceptance default.** `AcceptanceDefaults.For` (`AcceptanceDefaults.cs:128-133`) ends in
  `_ => Rules`, so prose would silently take the single-`architect` unanimous row — wrong for a runbook
  or a stakeholder update. Default position: a `tech_writer` single-reviewer row, with per-kind overrides
  left to the consuming stories via the existing per-document-type autonomy override.
- **D3 — one type, many kinds.** `RenderContract` is per document type (`IDocumentType.cs:47-50`), so one
  prose contract must cover all ten kinds. It therefore renders the *envelope* contract (kind, audience,
  title, markdown body) and delegates per-kind shape guidance to each producing cell's prompt file.

## Acceptance Criteria

1. A prose document with `kind=adr, audience=engineering` is drafted, validated, reviewed, accepted and
   persisted through `document-lifecycle` unchanged. `DocumentTypeKeyExtensions.Parse("prose")` succeeds —
   today it throws `DOCUMENT.TYPE.UNKNOWN`; `DocumentTypeRegistry.Resolve("prose")` returns the type —
   today it throws `DOCUMENT.TYPE.NOT_REGISTERED`.
2. **Prose is not schema-checked.** An arbitrary non-empty markdown body — headings in any order, no
   headings at all — validates. An **empty or whitespace-only** body is rejected with a named violation
   code. A test pins both directions so "no forced structure" is a tested property, not a slogan.
3. **Audience round-trips as a queryable field**: envelope → `documents` row → 39-11 lineage read-back.
   Reading documents for an issue filtered to `audience=stakeholder` returns the stakeholder-tagged rows
   and excludes the others.
4. **Out-of-vocabulary values fail loud**: an unknown `audience` and an unknown `kind` are each rejected
   with a named, distinct violation code (not a silent normalisation, not a default).
5. **The review stage produces a `Review` over the prose**, with `ParentDocumentId` set to the prose
   document id and the accept gate publishing the usual `AcceptanceRequest` — no bespoke prose branch in
   `DocumentLifecycleWorkflow`.
6. **`AcceptanceDefaults.For(DocumentTypeKey.Prose)` returns the D2 row**, asserted by a test — prose does
   not reach the `_ => Rules` catch-all by accident.
7. **Migration is non-destructive**: existing `documents` rows gain a NULL `Audience` and still read back
   through the store and the lineage API; a prose row without an audience cannot be *written* (AC4).
8. **The vocabulary count pin moves by exactly one** (`DocumentTypeKeyTests.cs:20` and
   `DocumentTypeRegistryTests.cs:37`, +1 on whatever the count is when this lands — 10 today, 16 if 41-1b
   has already merged), with the reason in the test comment.

## Dependencies

- **Blocking:** Epic 39 (39-2 registry + envelope, 39-8/39-12 lifecycle, 39-7 review producers, 39-11
  store + lineage API).
- **Related:** **41-1a** — prose reviewed by `(tech_writer, review-docs)` also needs 41-1a's TechWriter
  arm on `RolePhaseMap.GetReviewActionForRole` (`RolePhaseMap.cs:376-387`), which throws today (41-1a AC3). D2's default reviewer row
  therefore cannot be exercised end-to-end until 41-1a lands; a non-TechWriter reviewer proves this story
  in the meantime.
- **Unblocks:** 41-4, 41-5, 41-9, 41-22, 41-24, 41-25, 41-26 (prose type + audience); 41-8 (audience tag
  on its retro narrative — its `Findings` half needs only 41-1a).

> **Sequencing consequence.** 41-9 is designated the Wave-1 reference implementation of the
> prose-on-lifecycle path (`41-9:14-15`). It cannot be the reference implementation of a path that does
> not exist: either 41-1c lands before Wave 1, or 41-9 leaves Wave 1. Because this story is 3–4 days and
> independent of 41-1a/41-1b, the epic README takes the first option.
>
> *(Reconciled 2026-07-24: all eight dependents' `Blocking:` lines previously named "Epic 39
> (prose-document handling …)" for a deliverable 39-1:58 records as **out of Epic 39's scope**. They now
> name 41-1c. 41-8 — which needs the audience tag for its retro *narrative* even though its `Findings`
> half needs only 41-1a — was missing from every enumeration of that set and has been added.)*

## Estimated Effort

3–4 days
