# Implementation Plan — Story 39-2: Document Core — Envelope, Type Registry, Lineage, Drift Tests

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Documents/` exists as a new, storage-free namespace containing: the immutable `DocumentEnvelope` record (identity, type, schema version, lineage, producer provenance, lifecycle state, JSON payload); the `DocumentState` enum plus a data-driven `DocumentStateMachine`; the closed `DocumentLifecycleOutcome` enum (`ReviewUndecidable | AmbiguityAboveThreshold | RoundsExhausted | ValidationExhausted`); the `IDocumentType` contract (`Validate`/`RenderContract`/`Examples`) with its supporting `DocumentValidationResult`/`DocumentViolation`/`DocumentExample` records; and a fail-loud static `DocumentTypeRegistry` with a compile-time `DocumentTypeKey` vocabulary and static workflow `consumes/produces` interface declarations. Everything is drift-tested in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/` in the exact style of `RolePhaseMapTests`/`AgentRoleTests` (count pins, wire round-trips, fail-loud on unknowns). Zero EF entities, zero migrations, zero endpoints, zero Elsa activities — only `Tamma.Core` and `Tamma.Core.Tests` change.

## Pre-Reading

- `docs/stories/epic-39/story-39-2/39-2-document-core-envelope-type-registry-lineage-drift-tests.md` — the story (source of truth for ACs)
- `docs/stories/epic-39/README.md` — settled design principles ("Vocabulary static, composition dynamic"; "Issues are anchors, not documents"; the 10-type table; the lifecycle diagram)
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs`, `AgentAction.cs` — the `[Wire]` enum vocabulary pattern to copy exactly
- `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs` — `WireAttribute` + `EnumWire<TEnum>` bidirectional map (already validated at static init; reuse, do not reimplement)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` — the FrozenDictionary compile-time composition map pattern (`s_eligibleActions`, `AssertValid*` throw style)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs`, `RolePhaseMapTests.cs` — the drift-test style: count pins (`HaveCount(79)`), wire round-trips, case-sensitivity, throw-on-unknown
- `apps/tamma-elsa/src/Tamma.Core/TammaError.cs` — the fail-loud typed error (Code/Context/Retryable/Severity) the registry and state machine throw
- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — the DCB event row documents flow through (camelCase `issueId` tag convention; note its UUID-v4 remark: net8 has no `Guid.CreateVersion7`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — `DefinitionId = "llm-call"`, inputs `agentRole`/`action`/`variables`: the dispatch vocabulary `ProducedBy` must be expressible in
- `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs` + `PromptFileLoader.cs` — the fail-loud facade pattern (`Build` is a pure, test-drivable core; static init throws `TammaError` codes like `PROMPT.SEED.UNKNOWN_CELL`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the build-time contract gate 39-16 flips onto `RenderContract()`; also the `KnownContractViolations` ratchet-allowlist pattern this plan reuses for not-yet-implemented types
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — the dispatch-pair reflection 39-1 seeds edges from
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Tamma.Core.Tests.csproj` — existing NUnit 3.14 + FluentAssertions test project (target for new tests; no package changes needed)
- `.dev/findings/epic-39-workflow-io-lifecycle-audit.md` — **NOT FOUND** (the 39-1 audit deliverable; 39-1 is still `drafted`). See Design Decision D6 for how the plan proceeds without it.

## Design Decisions

- **D1 — Namespace is `Tamma.Core.Documents`, not the legacy `Tamma.Api.Services.Agents`.** The Agents files carry an explicit NOTE that their namespace is preserved legacy churn-avoidance and "a future cleanup story may realign to Tamma.Core.Agents". New code starts clean: `namespace Tamma.Core.Documents;`. Consequence: Documents files need `using Tamma.Api.Services.Agents;` to reach `WireAttribute`/`EnumWire<T>`/`AgentRoleExtensions` — acceptable; those types live in the same `Tamma.Core` assembly.
- **D2 — Type-key wire convention is kebab-case** (AC5b says "decided here, one convention"). Kebab matches the `AgentAction` wire style (`decompose-issue`) and the workflow DefinitionIds (`issue-decomposition`). Keys: `findings`, `ambiguity-assessment`, `clarification`, `decomposition`, `plan`, `design`, `review`, `triage-decision`, `diagnosis`, `test-spec`. SCREAMING dot-path stays reserved for event constants (`DOCUMENT.*`), keeping the two vocabularies visually distinct.
- **D3 — Two-layer registry: key vocabulary now, implementations later.** 39-3/39-4 provide `IDocumentType` implementations; 39-2 must still be drift-testable. Ship a `DocumentTypeKey` enum (10 members, `[Wire]`, count-pinned at 10) as the closed compile-time vocabulary, and a `DocumentTypeRegistry` whose key→`IDocumentType` map starts **empty** (registered-implementation count pin = 0, consciously bumped +4 in 39-3 and +6 in 39-4 — exactly what story 39-3 AC1 expects). `Resolve` distinguishes two typed failures: unknown key string → `DOCUMENT.TYPE.UNKNOWN`; valid key with no registered implementation yet → `DOCUMENT.TYPE.NOT_REGISTERED`. Both throw `TammaError`; no nulls, no defaults.
- **D4 — Escalated is reachable from every non-terminal state.** The lifecycle's typed unhandleable outcomes fire from different stages (`ValidationExhausted` from Draft, `ReviewUndecidable` from Validated, `RoundsExhausted` from Reviewed), and each escalates rather than rejects (README: escalation "with the full document lineage attached, never a bare failure"). Legal map: `Draft → {Validated, Escalated}`, `Validated → {Reviewed, Escalated}`, `Reviewed → {Accepted, Rejected, Escalated}`, terminals → ∅. Revision does **not** need `Reviewed → Draft`: a revision mints a NEW envelope (`SupersedesDocumentId` chain), it never rewinds an existing one.
- **D5 — `DocumentLifecycleOutcome` ships here, in `Tamma.Core/Documents`.** The story's own ACs don't list it, but the epic canon assigns the closed outcome enum to document core, and 39-6 AC3 says "closed outcome enum in `Tamma.Core/Documents`" with a drift test pinning the set. It is four members and zero risk; shipping it in 39-2 gives 39-6 a compile-time target. (No story/canon conflict — the story is simply silent; canon fills the gap.)
- **D6 — Workflow interface declarations: structure + graph test now, ratcheted seed.** The 39-1 audit document does not exist yet. Ship the `WorkflowDocumentInterface` record and graph-walk drift test now; seed the declaration list from the README's document-type table mapped to real DefinitionIds observed in `Tamma.ElsaServer/Workflows/` (`research`→`findings`, `ambiguity-scoring`→`ambiguity-assessment`, `clarifying-questions`→`clarification`, `issue-decomposition`→`decomposition`, `plan-generation`+`task-creation`→`plan`, `design-proposal`→`design`, `plan-review`/`task-review`/`code-review`→`review`, `triage-po-decision`→`triage-decision`, `blocker-diagnosis`+`debugging`→`diagnosis`, `test-case-creation`→`test-spec`). Each entry carries a `Provisional` flag until reconciled against the landed 39-1 edge list; a follow-up commit (or the 39-1 PR) flips flags off. Declarations are keyed by `DocumentTypeKey` enum members, so a declaration cannot reference a nonexistent type — the "declared producer with unregistered type key" failure the story requires is compile-time for the vocabulary, plus a ratchet allowlist (`ContractBindingTests.KnownContractViolations` style) for keys whose `IDocumentType` implementation is pending 39-3/39-4.
- **D7 — Producer provenance validates role/action strictly, workflow id structurally.** `DocumentProducer.Create(role, action, workflowDefinitionId)` parses role/action through `AgentRoleExtensions.Parse`/`AgentActionExtensions.Parse` (throw on unknown — the story's "not free strings" requirement) and additionally asserts the pair via `RolePhaseMap.IsRoleEligibleForPhase`. The workflow definition id is validated as non-empty kebab (`^[a-z0-9]+(-[a-z0-9]+)*$`) only: `Tamma.Core` cannot reference `Tamma.ElsaServer` to enumerate real DefinitionIds (dependency direction), and AC8 forbids cross-project test scope. Full workflow-id-against-catalog validation is deferred to the 39-16-era build test that already lives beside the workflow assemblies.
- **D8 — Explicit `[JsonPropertyName]` on every wire property; one canonical `JsonSerializerOptions`.** AC6 says the wire format is "a deliberate contract, not an accident of C# naming". Every envelope property carries an explicit camelCase `[JsonPropertyName("issueId")]`-style attribute (matching the DCB tag convention), and a static `DocumentJson.Options` holds the converters. Missing `IssueId`/`Type` rejection uses C# `required` members (net8 STJ throws `JsonException` on absent required properties); unknown extra fields are tolerated by STJ's default skip behavior (pinned by test). Timestamps serialize via a converter to `yyyy-MM-ddTHH:mm:ss.fffZ` (ISO 8601, millisecond precision, UTC). `DocumentState` serializes as its `[Wire]` string via a small generic `WireEnumJsonConverter<TEnum>`.
- **D9 — UUID v7 needs a local helper.** net8 has no `Guid.CreateVersion7` (already noted in `Tamma.Activities/Core/TammaActivity.cs`). Ship a ~25-line `UuidV7.NewGuid()` (RFC 9562: 48-bit unix-ms timestamp + version/variant bits + random) in `Tamma.Core/Documents/`. Pin the version nibble and time-ordering property in a test. Do not take a package dependency for this.
- **D10 — Envelope immutability via record + guarded transition helper.** `DocumentEnvelope` is a `sealed record` with init-only/required members. `envelope.WithState(next, now)` consults `DocumentStateMachine.AssertTransition` and returns a **new** record instance with updated `State`/`UpdatedAt` — the single seam 39-6 uses, mutation-free by construction.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs`** — copy the `AgentRole.cs` file shape verbatim (enum + extensions class):

   ```csharp
   namespace Tamma.Core.Documents;
   public enum DocumentTypeKey
   {
       [Wire("findings")]             Findings,
       [Wire("ambiguity-assessment")] AmbiguityAssessment,
       [Wire("clarification")]        Clarification,
       [Wire("decomposition")]        Decomposition,
       [Wire("plan")]                 Plan,
       [Wire("design")]               Design,
       [Wire("review")]               Review,
       [Wire("triage-decision")]      TriageDecision,
       [Wire("diagnosis")]            Diagnosis,
       [Wire("test-spec")]            TestSpec,
   }
   public static class DocumentTypeKeyExtensions
   {
       public static string ToWire(this DocumentTypeKey key);        // EnumWire<DocumentTypeKey>.ToWire
       public static DocumentTypeKey Parse(string input);            // throws TammaError DOCUMENT.TYPE.UNKNOWN
       public static bool TryParse(string input, out DocumentTypeKey key);
   }
   ```

   Precedent: `Tamma.Core/Agents/AgentRole.cs` (but throw `TammaError` with code `DOCUMENT.TYPE.UNKNOWN`, not bare `ArgumentException` — the registry-facing error is part of AC4/AC5d).

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentState.cs` and `DocumentLifecycleOutcome.cs`** — two small `[Wire]` enums, same file shape as step 1:
   - `DocumentState`: `[Wire("draft")] Draft`, `[Wire("validated")] Validated`, `[Wire("reviewed")] Reviewed`, `[Wire("accepted")] Accepted`, `[Wire("rejected")] Rejected`, `[Wire("escalated")] Escalated` (+ `ToWire`/`Parse` extensions).
   - `DocumentLifecycleOutcome`: `[Wire("review-undecidable")] ReviewUndecidable`, `[Wire("ambiguity-above-threshold")] AmbiguityAboveThreshold`, `[Wire("rounds-exhausted")] RoundsExhausted`, `[Wire("validation-exhausted")] ValidationExhausted`.

3. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentStateMachine.cs`** — static class mirroring `RolePhaseMap`'s `FrozenDictionary` style:

   ```csharp
   public static class DocumentStateMachine
   {
       private static readonly FrozenDictionary<DocumentState, FrozenSet<DocumentState>> s_legal; // per D4
       public static bool CanTransition(DocumentState from, DocumentState to);
       public static void AssertTransition(DocumentState from, DocumentState to);
       // throws TammaError("DOCUMENT.STATE.ILLEGAL_TRANSITION",
       //   $"Illegal document state transition: '{from.ToWire()}' -> '{to.ToWire()}'. ...")
       public static IReadOnlyDictionary<DocumentState, FrozenSet<DocumentState>> LegalTransitions { get; } // data for 39-6
       public static bool IsTerminal(DocumentState state);
   }
   ```

4. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs`** — `public static class UuidV7 { public static Guid NewGuid(); }` per D9 (48-bit big-endian unix-ms prefix, version nibble 7, RFC 4122 variant, `RandomNumberGenerator` tail).

5. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentProducer.cs`** — provenance record per D7:

   ```csharp
   public sealed record DocumentProducer
   {
       [JsonPropertyName("role")]     public required string Role { get; init; }
       [JsonPropertyName("action")]   public required string Action { get; init; }
       [JsonPropertyName("workflow")] public required string WorkflowDefinitionId { get; init; }
       public static DocumentProducer Create(string role, string action, string workflowDefinitionId);
       // Parse via AgentRoleExtensions/AgentActionExtensions; assert RolePhaseMap.IsRoleEligibleForPhase;
       // throws TammaError DOCUMENT.PRODUCER.INVALID on any failure.
   }
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentEnvelope.cs`** — the core record (AC1):

   ```csharp
   public sealed record DocumentEnvelope
   {
       [JsonPropertyName("id")]            public required Guid Id { get; init; }                  // UuidV7.NewGuid()
       [JsonPropertyName("type")]          public required string Type { get; init; }              // DocumentTypeKey wire
       [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
       [JsonPropertyName("issueId")]       public required string IssueId { get; init; }           // DCB tag convention
       [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
       [JsonPropertyName("parentDocumentId")]     public Guid? ParentDocumentId { get; init; }
       [JsonPropertyName("supersedesDocumentId")] public Guid? SupersedesDocumentId { get; init; }
       [JsonPropertyName("producedBy")]    public required DocumentProducer ProducedBy { get; init; }
       [JsonPropertyName("state")]         public required DocumentState State { get; init; }      // wire via converter
       [JsonPropertyName("createdAt")]     public required DateTimeOffset CreatedAt { get; init; } // ms-precision converter
       [JsonPropertyName("updatedAt")]     public required DateTimeOffset UpdatedAt { get; init; }
       [JsonPropertyName("payload")]       public required JsonElement Payload { get; init; }

       public static DocumentEnvelope CreateDraft(DocumentTypeKey type, int schemaVersion,
           string issueId, string correlationId, DocumentProducer producedBy, JsonElement payload,
           Guid? parentDocumentId = null, Guid? supersedesDocumentId = null, DateTimeOffset? now = null);
       public DocumentEnvelope WithState(DocumentState next, DateTimeOffset? now = null); // D10
   }
   ```

   `CreateDraft` throws `TammaError DOCUMENT.ENVELOPE.INVALID` on empty `IssueId`/`CorrelationId`. Timestamps are always truncated to millisecond precision at construction so round-trips are exact.

7. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentValidationResult.cs`** — the validator↔repair-ring interface (AC3, technical notes):

   ```csharp
   public sealed record DocumentViolation(
       [property: JsonPropertyName("code")]    string Code,     // stable, e.g. "DANGLING_DEPENDS_ON"
       [property: JsonPropertyName("message")] string Message); // domain-phrased, never a bare schema path
   public sealed record DocumentValidationResult(bool IsValid, IReadOnlyList<DocumentViolation> Violations)
   {
       public static DocumentValidationResult Valid();
       public static DocumentValidationResult Invalid(params DocumentViolation[] violations); // throws if empty
   }
   ```

8. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentExample.cs` and `IDocumentType.cs`** (AC3):

   ```csharp
   public sealed record DocumentExample(string Name, bool IsValid, string PayloadJson);
   public interface IDocumentType
   {
       string Key { get; }                 // a DocumentTypeKey wire string (drift-tested)
       int SchemaVersion { get; }
       Type PayloadClrType { get; }
       DocumentValidationResult Validate(JsonElement payload);
       string RenderContract();            // deterministic ordering — 39-16 diffs this in CI
       IReadOnlyList<DocumentExample> Examples { get; } // ≥1 valid + ≥1 invalid (enforced by registry drift test)
   }
   ```

9. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentJson.cs`** — `public static class DocumentJson { public static JsonSerializerOptions Options { get; } }` wiring `WireEnumJsonConverter<DocumentState>` (a private generic `JsonConverter<TEnum>` over `EnumWire<TEnum>`, throwing `JsonException` on a non-wire string) and a `MillisecondIso8601Converter : JsonConverter<DateTimeOffset>` writing `yyyy-MM-ddTHH:mm:ss.fffZ`. `Serialize(DocumentEnvelope)` / `Deserialize(string)` convenience wrappers so every caller uses the same options. Precedent for "one canonical options object": `Tamma.Api/Services/ElsaWorkflowService.cs` and peers use `JsonNamingPolicy.CamelCase`; here explicit `[JsonPropertyName]` makes the policy irrelevant (D8).

10. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/WorkflowDocumentInterface.cs` and `DocumentTypeRegistry.cs`** (AC4, D3, D6). Copy the facade+pure-core split from `SystemPrompts`/`PromptFileLoader.Build`:

    ```csharp
    public sealed record WorkflowDocumentInterface(
        string WorkflowDefinitionId,
        IReadOnlyList<DocumentTypeKey> Consumes,
        DocumentTypeKey? Produces,
        bool Provisional); // D6 — flips off when reconciled with the landed 39-1 audit
    public static class DocumentTypeRegistry
    {
        public static IReadOnlyList<IDocumentType> All { get; }                    // empty at 39-2; +4 (39-3), +6 (39-4)
        public static IDocumentType Resolve(string key);
        // unknown wire      -> TammaError DOCUMENT.TYPE.UNKNOWN
        // known, unimplemented -> TammaError DOCUMENT.TYPE.NOT_REGISTERED
        public static IDocumentType Resolve(DocumentTypeKey key);                  // NOT_REGISTERED only
        public static IReadOnlyList<WorkflowDocumentInterface> WorkflowInterfaces { get; } // D6 seed
        internal static IReadOnlyDictionary<string, IDocumentType> BuildIndex(IEnumerable<IDocumentType> types);
        // pure core (PromptFileLoader.Build style): throws DOCUMENT.TYPE.DUPLICATE_KEY on collision,
        // DOCUMENT.TYPE.KEY_NOT_IN_VOCABULARY when a type's Key is not a DocumentTypeKey wire string.
    }
    ```

    The static ctor builds `All`/index from a (currently empty) compile-time list — 39-3/39-4 append registrations here — and validates via `BuildIndex`, so a bad registration refuses to load, same posture as `PromptFileLoader`.

11. **CREATE the test files** in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/` (see Test Plan). No csproj edits expected: `Tamma.Core.csproj` already references `System.Text.Json` and the test project already carries NUnit + FluentAssertions and a project reference to `Tamma.Core`.

12. **Verify AC8 by construction**: run `dotnet test apps/tamma-elsa/tests/Tamma.Core.Tests` locally (no Docker); confirm `git status` shows only `Tamma.Core/Documents/*`, `Tamma.Core.Tests/Documents/*` (and this plan's story directory).

## Data & Migrations

None. AC8 explicitly forbids EF entities, migrations, and endpoints — persistence is Story 39-11.

## Events

None emitted or consumed. The `DOCUMENT.*` event constants (`DOCUMENT.PRODUCED`, `DOCUMENT.VALIDATED`, …) are Story 39-6 scope (`DocumentEvents.cs`); this story ships only the state/outcome vocabulary those events will reference. `DocumentEnvelope` JSON is *designed* to travel inside `DomainEvent.Data` with `issueId` mirrored into `DomainEvent.Tags`, but no event code changes here.

## Test Plan

All NUnit + FluentAssertions, in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/`, no Testcontainers/Docker (AC8). Style precedent: `Tamma.Api.Tests/Agents/AgentRoleTests.cs` and `RolePhaseMapTests.cs`.

- **`DocumentTypeKeyTests.cs`** (drift) — pins: exactly 10 members; each member's exact wire string (TestCase per member, `AgentRoleTests.ToWire_returns_canonical_string` style); wire round-trip for every member; `Parse` throws `TammaError` with code `DOCUMENT.TYPE.UNKNOWN` on unknown/empty/wrong-case input. **Covers AC5b.**
- **`DocumentStateMachineTests.cs`** — asserts every pair in the D4 legal map allowed (exhaustive loop over `LegalTransitions`); asserts a representative illegal set rejected (`Draft→Accepted`, `Draft→Reviewed`, `Validated→Accepted`, `Accepted→*`, `Rejected→*`, `Escalated→*`, any self-transition) with `TammaError.Message` containing **both** state wire names; pins terminal set = `{accepted, rejected, escalated}`; pins the state enum at exactly 6 members with exact wire strings; pins `DocumentLifecycleOutcome` at exactly 4 members with exact wire strings (the 39-6 drift anchor). **Covers AC2, AC7 (and D5).**
- **`DocumentTypeRegistryTests.cs`** (drift, AC5) — pins: (a) `DocumentTypeRegistry.All.Should().HaveCount(0)` with a comment block in the `RolePhaseMapTests.ValidActions_Should_Contain_Seventy_Nine_Actions` narrative style explaining the 39-3 (+4) / 39-4 (+6) bumps; (b) for every registered type: `Key` parses via `DocumentTypeKeyExtensions.Parse`, keys are unique, `RenderContract()` non-empty and deterministic (called twice, identical), `Examples` has ≥1 valid + ≥1 invalid, every valid example passes its own `Validate` and every invalid example fails it (loop is live now, bites when 39-3 lands); (c) `Resolve("nope")` throws `TammaError` code `DOCUMENT.TYPE.UNKNOWN`; `Resolve(DocumentTypeKey.Decomposition)` throws `DOCUMENT.TYPE.NOT_REGISTERED` while unimplemented; (d) `BuildIndex` with two fake `IDocumentType`s sharing a key throws `DOCUMENT.TYPE.DUPLICATE_KEY`, and a fake whose `Key` is `"not-a-type"` throws `DOCUMENT.TYPE.KEY_NOT_IN_VOCABULARY` (fakes are small local classes — Moq optional). **Covers AC3 (contract exercised via fakes), AC4, AC5a–d.**
- **`WorkflowInterfaceGraphTests.cs`** — the graph-walk build test (AC4, D6): every `WorkflowDocumentInterface.WorkflowDefinitionId` is non-empty kebab and unique; every declared `Produces` key either has a registered implementation **or** is listed in the in-test `PendingImplementations` ratchet allowlist (which starts as all 10 keys and may only shrink — a stale entry, i.e. a pending key that is now registered, fails the test, `KnownContractViolations` ratchet style); pins the exact declared edge count so adding/removing a declaration is a conscious edit. **Covers AC4's graph-walk clause + technical note.**
- **`DocumentEnvelopeTests.cs`** — `CreateDraft` mints version-7 `Id` (version nibble == 7; two ids minted across a ≥2 ms gap sort ascending as strings/bytes); `CreateDraft` throws on empty `IssueId`/`CorrelationId`; `DocumentProducer.Create` accepts `("senior_developer", "decompose-issue", "issue-decomposition")` and throws `DOCUMENT.PRODUCER.INVALID` on unknown role, unknown action, ineligible pair (`("tech_writer", "decompose-issue", …)`), and malformed workflow id (`"Not Kebab"`); `WithState(Validated)` returns a **new** instance (original unchanged — record non-identity + original `State` still `Draft`) with `UpdatedAt` advanced; `WithState(Accepted)` from `Draft` throws `DOCUMENT.STATE.ILLEGAL_TRANSITION`. **Covers AC1 (identity, provenance validation, immutability) and the AC2 enforcement seam.**
- **`DocumentEnvelopeSerializationTests.cs`** — serialize a fully-populated envelope with `DocumentJson.Options` and pin the **exact** property-name set (`id`, `type`, `schemaVersion`, `issueId`, `correlationId`, `parentDocumentId`, `supersedesDocumentId`, `producedBy` (with `role`/`action`/`workflow`), `state`, `createdAt`, `updatedAt`, `payload`) by parsing the output as `JsonDocument`; round-trip loses nothing (`deserialized.Should().Be(original)` — record value equality; payload compared via `JsonElement` raw text); `createdAt` matches `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$`; `state` serializes as `"draft"` not `"Draft"`/`0`; input JSON with an unknown extra field (`"futureField": 1`) deserializes fine (forward compatibility); input missing `issueId` or `type` throws `JsonException`. **Covers AC6.**

## Definition of Done

| AC | Satisfied by | Verified by |
|---|---|---|
| 1 — `DocumentEnvelope` record with identity/lineage/provenance/state/payload, immutable | Steps 4, 5, 6 | `DocumentEnvelopeTests` (identity, provenance, immutability), `DocumentEnvelopeSerializationTests` (field completeness) |
| 2 — `DocumentState` enum + legal-transition map, data for 39-6 | Steps 2, 3 | `DocumentStateMachineTests` (legal map exhaustive, terminals, enum pins) |
| 3 — `IDocumentType` with `Validate`/`RenderContract`/`Examples`, domain-phrased violations | Steps 7, 8 | `DocumentTypeRegistryTests` part (b) loop + fake-driven contract exercise |
| 4 — `DocumentTypeRegistry` static/immutable, typed throw on unknown, workflow interface declarations | Step 10 | `DocumentTypeRegistryTests` (c)(d), `WorkflowInterfaceGraphTests` |
| 5 — drift tests: count pin, key convention round-trip, contract/example checks, unknown throws | Step 11 | `DocumentTypeRegistryTests` (a)–(d), `DocumentTypeKeyTests` |
| 6 — serialization round-trip, property-name pins, extra-field tolerance, missing-required rejection | Steps 6, 9 | `DocumentEnvelopeSerializationTests` |
| 7 — state machine tests, illegal transitions rejected naming both states | Step 3 | `DocumentStateMachineTests` |
| 8 — no storage/no I/O; only Tamma.Core + Tamma.Core.Tests change; `dotnet test` passes without Docker | Steps 1–12 (nothing else planned) | Step 12 diff inspection + test-run check; reviewer verifies file list |

## Dependencies & Sequencing

- **39-1 (audit)** is a formal prerequisite but its deliverable (`.dev/findings/epic-39-workflow-io-lifecycle-audit.md`) does not exist yet. This plan does not block on it: the `WorkflowDocumentInterface` seed is derived from the README table + real DefinitionIds and flagged `Provisional` (D6). Reconciling flags against the landed audit is a small follow-up (½ day, can ride the 39-1 PR).
- **In place already:** the agent taxonomy (`AgentRole`/`AgentAction`/`RolePhaseMap` + `EnumWire`), `TammaError`, the PR #475 prompt registry, and the `Tamma.Core.Tests` project — all verified present.
- **Stubbing of later stories:** nothing from 39-3+ is pulled in. `IDocumentType` implementations are exercised only via in-test fakes (Step 11); the registry ships empty (D3); `DocumentEvents.cs`, persistence, and the lifecycle workflow are explicitly out (AC8).
- **Lockstep partners:** 39-3 bumps the registered-count pin 0→4 and shrinks the `PendingImplementations` ratchet by 4; 39-4 takes it to 10/empty. 39-6 consumes `DocumentStateMachine.LegalTransitions` + `DocumentLifecycleOutcome`; 39-16 diffs `RenderContract()` — its determinism is pinned here so that contract holds from day one.

## Risks & Mitigations

- **Envelope shape wrong = epic's most expensive mistake (story's own warning).** Mitigation: every field maps to a named consumer (lineage → 39-8/39-11; provenance → llm-call dispatch vocabulary; state → 39-6; payload as `JsonElement` → events/API). Anything without a consumer is left out — notably, no policy knobs (autonomy, rounds) on the envelope, per the technical notes and the "autonomy is a dial" invariant.
- **Provisional workflow-interface seed diverges from the eventual 39-1 audit.** Mitigation: `Provisional` flag + edge-count pin makes every later correction a conscious, reviewed test edit; declarations are enum-keyed so no correction can introduce an invalid type key.
- **Wire/JSON contract accidentally coupled to C# names.** Mitigation: explicit `[JsonPropertyName]` everywhere + the property-name pin test; renaming a C# property without touching the attribute changes nothing on the wire.
- **`EnumWire` lives in the legacy `Tamma.Api.Services.Agents` namespace** — mildly surprising imports in new clean-namespace files. Mitigation: one `using` per file plus a file-header comment pointing at the Agents relocation NOTE; do **not** move `EnumWire` in this story (churn outside AC8's diff budget).
- **UUID v7 hand-roll subtly wrong.** Mitigation: generator is ~25 lines against RFC 9562's simplest layout; tests pin version nibble, variant bits, and time-ordering; if .NET is ever bumped to 9+, swap to `Guid.CreateVersion7` behind the same static.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | `DocumentTypeKey`, `DocumentState`, `DocumentLifecycleOutcome` enums + extensions | 0.5 |
| 3 | `DocumentStateMachine` | 0.5 |
| 4–6 | `UuidV7`, `DocumentProducer`, `DocumentEnvelope` | 1.0 |
| 7–9 | `DocumentValidationResult`, `DocumentExample`, `IDocumentType`, `DocumentJson` converters | 0.5 |
| 10 | `DocumentTypeRegistry` + `WorkflowDocumentInterface` seed + fail-loud core | 0.75 |
| 11 | Six test classes (drift, state machine, envelope, serialization, graph walk) | 1.25 |
| 12 | AC8 verification, doc comments, review polish | 0.5 |
| **Total** | | **5.0** (story estimate: 4–5 days) |
