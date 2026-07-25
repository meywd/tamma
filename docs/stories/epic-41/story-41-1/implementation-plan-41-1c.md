# Implementation Plan — Story 41-1c: Prose Documents & Audience Tags — the mechanism eight stories assume

## Scope & Deliverable

When this story is done, prose is a first-class thing the document lifecycle produces, reviews, accepts,
persists and retrieves. Concretely: a `prose` member on `DocumentTypeKey` and a `ProseDocumentType`
registered alongside the other types, whose payload is `{ kind, audience, title, body }` with **`body` an
unvalidated markdown string**; an `Audience` field on `DocumentEnvelope` **and** on the
`DocumentInstance` entity (+ EF configuration + one Tenant migration adding a nullable
`document_instances.audience` column + its index); two `[Wire]`-tagged vocabularies (`ProseAudience`,
`ProseKind`) each with a drift test and each failing loud on an out-of-vocabulary value with its own
violation code; `AcceptanceDefaults.For(DocumentTypeKey.Prose)` returning a deliberately chosen row
instead of the catch-all; an `audience` field on the lineage DTO plus an `audience` query filter on the
39-11 lineage read; and a lifecycle run that drafts, validates, reviews, accepts and persists an
`kind=adr, audience=engineering` prose document **with no bespoke prose branch in
`DocumentLifecycleWorkflow`**.

Diff surface: `Tamma.Core/Documents/{DocumentTypeKey,DocumentTypeRegistry,DocumentEnvelope}.cs`,
`Documents/Types/Prose.cs`, `Documents/Policy/AcceptanceDefaults.cs`,
`Documents/Lineage/IssueDocumentLineage.cs`, `Tamma.Data/Entities/DocumentInstance.cs`,
`TammaModelConfiguration.cs`, one new `Migrations/Tenant/*`, `Repositories/{I,}DocumentInstanceRepository.cs`,
`Tamma.Api/Endpoints/DocumentEndpoints.cs`, and tests.

## Pre-Reading

- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the story (ACs are
  source of truth)
- `docs/stories/epic-41/story-41-1/implementation-plan.md` — shared lockstep rules ("adding a
  `DocumentTypeKey`" half) and the 41-1b file-sharing note
- `docs/stories/epic-41/README.md:54-67` (the Corrected note that gave this story its charter),
  `:275-282` (which activities reuse prose), `:296` (Wave-0 row)
- `docs/stories/epic-39/README.md:115-116` — "prose stays prose" as a **principle**; and 39-1:58, which
  records prose/tech-writer output as explicitly **out of scope** of the 10-type table. This story is new
  scope, not an extension.
- **The vocabulary + type pattern:** `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:22-34`,
  `:49-59`; `DocumentTypeRegistry.cs:27-40`, `:79-92`, `:103-126`;
  `Documents/IDocumentType.cs` (`Validate` `:29`, `ValidateWithContext` `:47-52`, `RenderContract` `:50`,
  `Examples` `:56`); `Documents/Types/Design.cs` (176 lines — the smallest existing type, the closest
  size analogue); `Tamma.Core/Agents/EnumWire.cs` (the `[Wire]` map both new enums use)
- **The envelope + persistence path (the whole of AC3/AC7):**
  `Documents/DocumentEnvelope.cs` — the 11 wire properties `:23-57`, `CreateDraft` `:68-101` (note the
  `issueId`/`correlationId` fail-loud at `:79-82`), `WithState` `:112-120`, and the **hand-written
  `Equals`** at `:143-159` and `GetHashCode` at `:161-162` — both must learn the new field;
  `Tamma.Data/Entities/DocumentInstance.cs:23-90` — 17 properties, no audience;
  `Tamma.Data/TammaModelConfiguration.cs:1343-1415` — `ToTable("document_instances")` `:1360`, the
  status CHECK `:1363-1366`, the indexes `:1396`/`:1398`, the self-FK `:1403-1414`;
  `Tamma.Data/Repositories/IDocumentInstanceRepository.cs:25-49` and
  `DocumentInstanceRepository.cs:27-105` (envelope → row mapping in `InsertAsync`), `:157-190`
  (`ListByIssueAsync`, `GetLatestAcceptedAsync`);
  `Tamma.Data/Migrations/Tenant/20260722180002_AddDocumentInstances.cs` — the precedent migration
- **The read path (AC3's filter):** `Tamma.Core/Documents/Lineage/IssueDocumentLineage.cs:19-32`
  (`LineageDocumentEntry`, 14 members), `:38-40` (`DocumentTypeTrail`), `:49-53` (`IssueDocumentLineage`),
  `:60-62` (`LatestAcceptedDocuments`); `Tamma.Api/Endpoints/DocumentEndpoints.cs:32-44`
  (`GetIssueLineage`), `:52-65` (`GetLatestAccepted`), `:98-130` (`PersistFromEngine` — envelope JSON in,
  `InsertAsync` out); `Tamma.Api/Program.cs:2891-2895` (route registrations)
- **Acceptance posture:** `Documents/Policy/AcceptanceDefaults.cs` — `PanelRoster` `:54-69` (**excludes
  `tech_writer` deliberately**), `Rules` `:75`, `s_humanAcceptorRules` `:113-116`, static-ctor validation
  loop `:119-121`, `For` `:129-134`
- **The review path (AC5):** `Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:58` (the
  `document-review` dispatch), `:1200-1220` (`BuildReviewEnvelope`; the `GetReviewActionForRole` call is
  at `:1212`); `Workflows/Helpers/ReviewerSelectionHelper.cs:153-168` (`ResolveDocumentAction`),
  `:61-70` (`s_documentRoster`, 7 roles, no `tech_writer`); `Tamma.Core/Agents/RolePhaseMap.cs:376-387`
  (throws for `TechWriter` today)
- **The pins:** `tests/Tamma.Core.Tests/Documents/DocumentTypeKeyTests.cs:20`,
  `DocumentTypeRegistryTests.cs:37` and `:113+`,
  `WorkflowInterfaceGraphTests.cs:31-33` and `:36-45`,
  `Policy/AcceptanceDefaultsDriftTests.cs:47/:55/:56`
- `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:286-301` — read the
  `IntentionallyUnbound` block; `("product_owner", "summarize-stakeholder")` is at `:299-301` and matters
  for 41-5 (see C5)

## Corrections to the story

- **C1 — the table is `document_instances`, not `documents`.** AC3 and AC7 both say "the `documents` row"
  / "existing `documents` rows". The entity is `DocumentInstance` and the table is
  `document_instances` (`TammaModelConfiguration.cs:1360`); the DbSet comment at
  `IDocumentInstanceRepository.cs:8-9` is what the story's wording seems to have picked up. Plan against
  `document_instances`.
- **C2 — there is no `repository` field to check.** Scope item 1 says validation asserts
  "`issueId`/`repository` lineage present". `DocumentEnvelope` (`:23-57`) has `issueId` and
  `correlationId` and **no** `repository`; `DocumentInstance` (`:23-90`) likewise. The only `Repository`
  in the document layer is inside `ReviewSubject` for a *diff* subject (39-4 D3). The lineage assertion is
  therefore `issueId` + `correlationId`, and both are already enforced fail-loud by
  `DocumentEnvelope.CreateDraft` (`:79-82`) — so `ProseDocumentType.Validate` should **not** re-assert
  them (they are envelope-level, not payload-level, and the payload never sees them).
- **C3 — AC3's "queryable field" needs three edits the Scope does not enumerate.** Adding an `Audience`
  column does not make `audience=stakeholder` filterable: `LineageDocumentEntry`
  (`IssueDocumentLineage.cs:19-32`) has no audience member, `IDocumentInstanceRepository.ListByIssueAsync`
  (`:43`) has no filter parameter, and `DocumentEndpoints.GetIssueLineage` (`:32-44`) accepts no query
  string. All three are in scope for AC3 as written. See D4.
- **C4 — AC8 understates the lockstep.** "The vocabulary count pin moves by exactly one" is true for
  `DocumentTypeKeyTests.cs:20` and `DocumentTypeRegistryTests.cs:37`, but the enum member and the
  `IDocumentType` registration cannot be split across commits:
  `DocumentTypeRegistryTests.Every_vocabulary_key_now_resolves_to_an_implementation` (`:113+`) fails on
  an unregistered key, and `WorkflowInterfaceGraphTests.PendingImplementations` (`:31-33`) — the historic
  defer-the-impl hatch — is deliberately empty with `Pending_entry_is_not_already_registered` failing on
  a re-added entry.
- **C5 — the story's D2 default reviewer is unreachable *and* one prose producing cell is already
  classified as free-text.** (a) `RolePhaseMap.GetReviewActionForRole` throws for `TechWriter`
  (`RolePhaseMap.cs:385-386`) and `ReviewerSelectionHelper.s_documentRoster` (`:61-70`) excludes it, so a
  `tech_writer` reviewer row cannot execute until 41-1a lands — the story says this under **Related** but
  AC6 only asserts the *row*, which is safe. Note also that `AcceptanceDefaults.PanelRoster`
  (`AcceptanceDefaults.cs:60-69`) excludes `tech_writer` and `AcceptanceDefaultsDriftTests.cs:56` pins the
  exclusion, so D2 must be a **single-reviewer** row, never a panel row. (b) Downstream, not this story's
  scope but recorded because this is where the prose family is enumerated:
  `("product_owner", "summarize-stakeholder")` is already in `ContractBindingTests.IntentionallyUnbound`
  (`:299-301`) as a *lenient free-text* consumer of `ContextGatheringWorkflow.ExtractPO`. When **41-5**
  binds it as a prose producer it will trip `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode`
  (`:655`: "a document producer must be BOUND, never allowlisted") — the same class of collision 41-22
  hit with `(devops, diagnose-incident)`. 41-5 needs either a new cell or a rewire of
  `ContextGatheringWorkflow`.
- **C6 — `AcceptanceDefaults.For`'s switch is at `:129-134`, not `:128-133`** (same off-by-one the sibling
  stories carry).
- **C7 — `DocumentEnvelope` has a hand-written `Equals`/`GetHashCode`.** `:143-159` compares every
  property explicitly (because `JsonElement` breaks record equality). A new `Audience` property that is
  not added there is silently excluded from equality — and the envelope round-trip tests compare
  envelopes. Not a story claim, but the single most likely silent bug in this change.

## Design Decisions

- **D1 — prose is a registered type whose body is unvalidated, not a second persistence path** (the
  story's D1, restated because the alternative keeps re-opening). Epic-41 rule 1 requires every producing
  workflow to declare `produces: <DocumentType>`, and `DocumentInstance.DocumentType` (`:34`) is a
  `DocumentTypeKey` wire string that `InsertAsync` re-validates through the registry. Modelling prose
  outside the registry means a second store, a second review path, and no lineage. So: one type, one
  registration, `Validate` asserts envelope-level facts only.
- **D2 — `Audience` is an envelope field **and** a payload key, and the envelope is authoritative.** The
  payload carries `audience` because the model writes the payload and the producing prompt instructs it;
  the envelope carries `Audience` because the store must filter without parsing bodies (Scope 2). To stop
  them diverging, `ProseDocumentType.Validate` checks the payload value is in vocabulary, and the
  **lifecycle's draft-mint path copies payload→envelope**, with a `PROSE_AUDIENCE_ENVELOPE_MISMATCH`
  violation if a caller supplies both and they disagree. `DocumentEnvelope.Audience` is
  `string?` (nullable) — every non-prose document has none — carried through `CreateDraft` as an optional
  parameter with a default of `null`, so no existing call site changes. **`Equals`/`GetHashCode` are
  updated in the same edit (C7).**
- **D3 — the two vocabularies are `[Wire]` enums in `Tamma.Core/Documents/Types/Prose.cs`, seeded from
  the actual consumers, and each fails with its own code.**
  `ProseAudience { engineering, developer, user, ops, stakeholder, team }` (6 — from 41-9/41-22,
  41-24 changelog/41-25 api-docs, 41-24 release-notes/41-25 user-docs, 41-26, 41-4/41-5, 41-8);
  `ProseKind { adr, postmortem, release-notes, changelog, user-docs, api-docs, runbook, roadmap,
  status-update, retro-narrative }` (10). AC4 requires **distinct** codes:
  `PROSE_AUDIENCE_OUT_OF_VOCABULARY` and `PROSE_KIND_OUT_OF_VOCABULARY`, each naming the offending value.
  No normalisation, no default, no case-folding — `EnumWire.TryParse` is ordinal, matching
  `DocumentTypeKeyExtensions.Parse`'s documented case-sensitivity (`DocumentTypeKey.cs:42-44`). Each enum
  gets a count pin + round-trip drift test in the `AgentRoleTests`/`DocumentTypeKeyTests` style.
- **D4 — `audience` becomes a first-class read dimension: DTO member, repository parameter, query string
  (C3).** `LineageDocumentEntry` gains `[property: JsonPropertyName("audience")] string? Audience` as its
  15th member; `IDocumentInstanceRepository.ListByIssueAsync(tenantId, issueId, string? audience, ct)`
  gains an **optional** filter applied in SQL (`audience == null` ⇒ unfiltered, preserving every existing
  caller's behaviour — including 39-10's re-entry read, which must not start filtering);
  `DocumentEndpoints.GetIssueLineage` gains `[FromQuery] string? audience`, validated against
  `ProseAudience` and 400-ing on an unknown value rather than returning an empty list. `GetLatestAccepted`
  is **not** filtered — it is 39-10's re-entry contract (`IDocumentInstanceRepository.cs:44-48`) and adding
  a dimension there would change resume semantics.
- **D5 — one contract for ten kinds; per-kind shape guidance lives in the producing prompt cell** (the
  story's D3, confirmed against `IDocumentType.cs:50` — `RenderContract` is per *type*).
  `ProseDocumentType.RenderContract` renders the **envelope** contract: the four payload keys, the closed
  audience and kind vocabularies enumerated explicitly, and the statement that `body` is free markdown
  with no required structure. ADR context/decision/consequences, postmortem timeline/root-cause, runbook
  preconditions/steps/rollback are *guidance in each cell's `Prompts/{role}/{action}.md`*, added by the
  consuming story (41-9, 41-22, 41-26 …), never a validated schema. This is what makes "no forced
  structure" (AC2) survive contact with ten different documents.
- **D6 — acceptance posture: single `tech_writer` reviewer, `AcceptorRequirement` unchanged from base.**
  Per the story's D2 and constrained by C5: a **single-reviewer** row (`ReviewerMode.Single`,
  `ReviewerRole = "tech_writer"`), never a panel row — `AcceptanceDefaults.PanelRoster` excludes
  `tech_writer` and `AcceptanceDefaultsDriftTests.cs:56` pins that exclusion. Built as a new
  `s_techWriterRules` static (`Rules with { ReviewerSelection = … }` then `.Validate()`, mirroring
  `s_panelRules` at `:100-108`) and given its own arm in `For`. Per-kind overrides (a runbook wants an
  ops reviewer; a stakeholder update wants none) are left to the consuming stories via the existing
  per-document-type autonomy override — not encoded here.
- **D7 — the migration is additive and nullable, and lands in `Migrations/Tenant/`.** One
  `ALTER TABLE document_instances ADD COLUMN audience text NULL` plus a partial index
  `IX_document_instances_issue_audience` on `(IssueId, Audience) WHERE "Audience" IS NOT NULL` (the filter
  in D4 is always issue-scoped, and only prose rows have an audience). No CHECK constraint on the column:
  the vocabulary is enforced in `Validate` with a named code (AC4), and a DB-level CHECK would turn a
  vocabulary extension into a migration. No backfill — every existing row gets NULL (AC7). Generate with
  `dotnet ef migrations add AddDocumentInstanceAudience --context TenantDbContext`, following
  `20260722180002_AddDocumentInstances`.
- **D8 — "a prose row without an audience cannot be written" is enforced in `Validate`, not by a NOT NULL
  column.** AC7 wants both a nullable column (for existing non-prose rows) and a write-time rejection for
  prose. A `NOT NULL` column contradicts the first; a CHECK on `document_type = 'prose' ⇒ audience IS NOT
  NULL` would enforce it at the DB but produce an untyped `DbUpdateException` instead of a violation code.
  So: `ProseDocumentType.Validate` emits `PROSE_AUDIENCE_MISSING`, and `InsertAsync` already re-validates
  through the registry before persisting (`IDocumentInstanceRepository.cs:16-18`), so the write is
  rejected with `DOCUMENT.STORE.INVALID_BODY` and nothing is written. Same posture the other ten types
  use for their required fields.

## Task Breakdown

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Prose.cs`** — the two `[Wire]` enums + their
   `Parse`/`ToWire` extensions (D3), the payload record
   `Prose { kind, audience, title, body }` with explicit `[JsonPropertyName]`s, and `ProseDocumentType :
   IDocumentType`:
   - `Key = "prose"`, `SchemaVersion = 1`, `PayloadClrType = typeof(Prose)`;
   - `Validate`: `kind` present + in vocabulary (`PROSE_KIND_MISSING` / `PROSE_KIND_OUT_OF_VOCABULARY`),
     `audience` present + in vocabulary (`PROSE_AUDIENCE_MISSING` / `PROSE_AUDIENCE_OUT_OF_VOCABULARY`),
     `title` non-empty (`PROSE_TITLE_MISSING`), `body` non-null and **not whitespace-only**
     (`PROSE_BODY_EMPTY`). **Nothing else.** No heading check, no length check, no structure check — AC2
     is a tested property, so the temptation to add "helpful" rules is the failure mode.
   - `RenderContract` per D5 (deterministic ordering — `DocumentTypeRegistryTests.cs:57-67` calls it
     twice and compares);
   - `Examples`: ≥1 valid (an ADR body with headings in an unusual order) + one invalid **per rule**, each
     declaring its exact `ExpectedViolationCodes` (`DocumentTypeRegistryTests.cs:88-99` requires
     exactness, not a superset).

2. **MODIFY `Tamma.Core/Documents/DocumentTypeKey.cs`** — append `[Wire("prose")] Prose` at `:33`.
   **MODIFY `DocumentTypeRegistry.cs`** — append `new ProseDocumentType()` to `s_registrations` (`:39`)
   with a comment naming this story, in the same commit (C4).

3. **MODIFY `Tamma.Core/Documents/DocumentEnvelope.cs`** (D2/C7) — add
   `[JsonPropertyName("audience")] public string? Audience { get; init; }` after `Type`/`SchemaVersion`;
   add an optional `string? audience = null` parameter to `CreateDraft` (`:68-77`) and set it at `:86-100`;
   **add `Audience == other.Audience` to `Equals` (`:147-158`) and `Audience` to `GetHashCode` (`:161`)**.
   `WithState` needs no change (`with` copies it).

4. **MODIFY `Tamma.Core/Documents/Policy/AcceptanceDefaults.cs`** (D6) — add `s_techWriterRules` beside
   `s_panelRules`/`s_humanAcceptorRules`, and a `DocumentTypeKey.Prose => s_techWriterRules` arm in `For`
   (`:129-134`). The static-ctor loop at `:119-121` validates it at class load.

5. **MODIFY `Tamma.Data/Entities/DocumentInstance.cs`** — add
   `public string? Audience { get; set; }` (18th property).
   **MODIFY `Tamma.Data/TammaModelConfiguration.cs:1358-1415`** — column configuration + the D7 partial
   index. **MODIFY `Tamma.Data/Repositories/DocumentInstanceRepository.cs:27+`** — one mapping line in
   `InsertAsync` (`Audience = envelope.Audience`) and the D4 filter in `ListByIssueAsync` (`:157-170`).
   **MODIFY `IDocumentInstanceRepository.cs:43`** — the optional `string? audience` parameter, documented
   as unfiltered-when-null so 39-10's re-entry read is provably unchanged.

6. **CREATE the migration** — `dotnet ef migrations add AddDocumentInstanceAudience --context
   TenantDbContext --project src/Tamma.Data` (D7). Verify the generated `Up`/`Down` is
   `AddColumn`/`CreateIndex` and `DropIndex`/`DropColumn` only, then
   `dotnet ef migrations has-pending-model-changes` must come back clean.

7. **MODIFY `Tamma.Core/Documents/Lineage/IssueDocumentLineage.cs:19-32`** — `LineageDocumentEntry` gains
   `audience`. **MODIFY the lineage assembler** (`LineageAssembler.Assemble`/`AssembleLatest`, called from
   `DocumentEndpoints.cs:41` and `:62`) to carry it. **MODIFY
   `Tamma.Api/Endpoints/DocumentEndpoints.cs:32-44`** — the `[FromQuery] string? audience` parameter,
   validated against `ProseAudience` (unknown → 400 `{ error = "unknown_audience" }`, not an empty
   result) and threaded to the repository. `GetLatestAccepted` (`:52-65`) is untouched (D4).

8. **MODIFY the two count pins** — `DocumentTypeKeyTests.cs:20` and `DocumentTypeRegistryTests.cs:37`,
   `+1` on whatever the count is when this lands (10 → 11 alone; 16 → 17 if 41-1b merged first), each with
   a one-line reason naming this story. **Do NOT touch** `WorkflowInterfaceGraphTests.cs:45` — prose has
   no producing workflow yet; each of 41-4/41-5/41-9/41-22/41-24/41-25/41-26 owns its own edge `+1`.

9. **CREATE the tests** (see Test Plan), then run `dotnet test` + `dotnet ef migrations
   has-pending-model-changes`.

## Test Plan

NUnit + FluentAssertions; the store/lineage halves on the existing 39-11 Testcontainers fixture.

- **`DocumentTypeKeyTests` / `DocumentTypeRegistryTests` (existing files).** AC1 (vocabulary half):
  `Parse("prose")` succeeds — throws `DOCUMENT.TYPE.UNKNOWN` today; `Resolve("prose")` returns
  `ProseDocumentType` — throws `DOCUMENT.TYPE.NOT_REGISTERED` today. AC8: both count pins `+1`.
  `Every_vocabulary_key_now_resolves_to_an_implementation` (`:113+`) is the C4 atomicity proof; the
  existing per-type contract loop (`:44-100`) covers deterministic contract + exact example codes with no
  edit. **Covers AC1 (half), AC8.**
- **`ProseDocumentTypeTests` (NEW, `tests/Tamma.Core.Tests/Documents/Types/`).** AC2 pinned in **both**
  directions: a body with headings in a scrambled order validates; a body with no headings at all
  validates; a body that is a single word validates; a body of `""` and a body of `"   \n\t "` are each
  rejected with **`PROSE_BODY_EMPTY`**. AC4: `audience = "marketing"` → exactly
  `PROSE_AUDIENCE_OUT_OF_VOCABULARY`; `kind = "memo"` → exactly `PROSE_KIND_OUT_OF_VOCABULARY`; both
  wrong → exactly both codes, distinct, no silent normalisation and no default. Plus `PROSE_TITLE_MISSING`
  and the two `_MISSING` cases. **Covers AC2, AC4.**
- **`ProseVocabularyDriftTests` (NEW).** D3: count pins (`ProseAudience` = 6, `ProseKind` = 10),
  `ToWire`/`Parse` round-trip for every member, unique wires, ordinal case-sensitivity
  (`Parse("ADR")` fails). The `AgentRoleTests` shape. **Covers D3.**
- **`AcceptanceDefaultsDriftTests` (existing file, extended).** AC6:
  `AcceptanceDefaults.For(DocumentTypeKey.Prose)` equals `s_techWriterRules` — `ReviewerMode.Single`,
  `ReviewerRole == "tech_writer"` — and is **not** reference-equal to `AcceptanceDefaults.Rules` (i.e. it
  did not reach the `_ => Rules` catch-all). The existing `PanelRoster` pins at `:47/:55/:56` stay
  unchanged and green — D6's row is single-reviewer precisely so `:56` survives (C5). **Covers AC6.**
- **`DocumentEnvelopeAudienceTests` (NEW/extended, `Tamma.Core.Tests`).** D2/C7: `CreateDraft` with and
  without an audience; JSON round-trip through `DocumentJson.Options` preserves it; **two envelopes
  differing only in `Audience` are NOT equal** (the C7 regression pin) and their hash codes differ;
  `WithState` preserves it.
- **`ProseStoreAndLineageTests` (integration, extends the 39-11 store fixture).** AC3: persist prose rows
  tagged `stakeholder`, `engineering` and `ops` plus a non-prose `findings` row for the same issue;
  `ListByIssueAsync(tenant, issue, audience: "stakeholder")` returns only the stakeholder row;
  `audience: null` returns all four (the unchanged-caller proof); `GET
  /api/documents/issues/{id}/lineage?audience=stakeholder` returns the same set with `audience` on each
  `LineageDocumentEntry`; `?audience=marketing` → 400. AC7: rows written **before** the migration (seeded
  directly) read back through both `ListByIssueAsync` and `GetIssueLineage` with `audience == null`; and
  a prose envelope with no audience is rejected at `InsertAsync` with `DOCUMENT.STORE.INVALID_BODY`
  (nothing persisted) — D8. AC1 (persistence half). **Covers AC1 (half), AC3, AC7.**
- **`ProseLifecycleExecutionTests` (integration, extends
  `tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleExecutionTests.cs`'s fixture).** AC1
  end-to-end + AC5: dispatch `document-lifecycle` for `documentType = "prose"` with a stub `llm-call`
  returning `{kind: "adr", audience: "engineering", title, body}`; assert draft → validate → review →
  accept → persist completes; assert the produced `Review` instance's `ParentDocumentId` is the prose
  document id; assert the accept gate published the usual `AcceptanceRequest` and suspended on the
  canonical bookmark. **Reviewer is `architect` (single), not `tech_writer`** — C5: the D6 row cannot
  execute until 41-1a adds the selector arm, so this story proves the mechanism with a reachable reviewer
  and 41-1a's AC3 proves the D6 row. A **structural** assertion completes AC5: a graph walk of
  `DocumentLifecycleWorkflow` finds no node whose id/name mentions prose — no bespoke branch.
  **Covers AC1, AC5.**
- **`WorkflowInterfaceGraphTests` (existing, NO edit).** `Declared_edge_count_is_pinned` (`:45`) stays at
  16 — the evidence that step 8's "do not touch" was honoured.

## Risks & Mitigations

- **`DocumentEnvelope.Equals` is hand-written (C7).** Forgetting the new member makes two prose documents
  with different audiences compare equal, which quietly breaks any test that round-trips an envelope.
  *Mitigation:* the explicit inequality assertion in `DocumentEnvelopeAudienceTests`, and step 3 names the
  two line ranges.
- **`ListByIssueAsync` gains a parameter and 39-10's re-entry reads through it.**
  `LifecycleReEntryService` consumes the latest-accepted read, and any accidental default-filtering would
  silently change resume behaviour. *Mitigation:* the parameter is optional and null-means-unfiltered;
  `GetLatestAcceptedAsync` (the actual re-entry read) is not touched at all; the integration test asserts
  the `audience: null` path returns every row.
- **The temptation to validate prose structure.** The whole point is that `body` is unvalidated; a
  reviewer "improving" `Validate` with a heading check silently breaks eight downstream stories.
  *Mitigation:* AC2's both-directions test is the guard, and D5 states where shape guidance *does* live.
- **D6's row is untestable end-to-end until 41-1a.** *Mitigation:* AC6 asserts the row (pure), the
  lifecycle test uses a reachable reviewer, and 41-1a's AC3 owns the end-to-end proof. Recorded in the
  story's own Related section; restated here so nobody blocks on it.
- **File collision with 41-1b** on `DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`,
  `AcceptanceDefaults.cs` and the two pins. *Mitigation:* all four edits are pure appends at known lines;
  whichever merges second rebases the pin arithmetic (AC8 already says "+1 on whatever the count is").
- **Migration on a per-tenant-schema deployment.** `document_instances` is tenant-resident; the Tenant
  migration runs per schema. *Mitigation:* additive-nullable + a partial index is the cheapest possible
  shape; no backfill, no lock-heavy rewrite; and the platform has no production users (CLAUDE.md).

## Est. Effort

**3.5 days**, matching the story's 3–4.

| Step | Work | Days |
|---|---|---|
| 1 | `Prose.cs` — two vocabularies, payload, validator, contract, examples | 0.75 |
| 2, 4, 8 | Key + registration + acceptance row + the two pins | 0.25 |
| 3 | `DocumentEnvelope.Audience` incl. `Equals`/`GetHashCode` | 0.25 |
| 5–6 | Entity + EF config + repository mapping/filter + migration | 0.5 |
| 7 | Lineage DTO + assembler + the `audience` query filter | 0.5 |
| 9 | Tests: prose validator both-directions, vocabulary drift, envelope equality, store/lineage integration, lifecycle integration | 1.0 |
| — | Gate run + review polish | 0.25 |

## Blocks / Blocked by

- **Blocked by:** Epic 39 — **39-2** (registry + envelope + drift tests), **39-6**/**39-8** (lifecycle +
  accept gate), **39-7** (review producers), **39-11** (store + lineage API). All landed.
- **Related (not blocking):** **41-1a** — its `TechWriter` arm on `RolePhaseMap.GetReviewActionForRole` is
  what makes D6's reviewer row executable. This story ships and is proven with a non-`tech_writer`
  reviewer.
- **Shares files with:** **41-1b** — `DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`,
  `AcceptanceDefaults.cs`, `DocumentTypeKeyTests.cs:20`, `DocumentTypeRegistryTests.cs:37`.
- **Blocks:** **41-4** (roadmap), **41-5** (stakeholder update — and see C5(b), it has a second problem),
  **41-8** (the retro *narrative* half; its `Findings` half needs only 41-1a), **41-9** (ADR — the Wave-1
  reference implementation of the prose path, which is why this story is scheduled before Wave 1),
  **41-22** (postmortem), **41-24** (release notes, changelog), **41-25** (user/API docs), **41-26**
  (runbook). Eight stories.
- **Does not block:** 41-1a, 41-1b, 41-29, or any of the six typed-document stories.
