# Story 29-1 secret store abstraction — design decisions

**Date**: 2026-04-27
**Status**: locked-in (the abstraction is already implemented in `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/`; this ADR records the calls so 29-2..29-10 reviewers and a future OpenBao driver author have one place to read the rationale).

The Story 29-1 brief says "ship a typed `ISecretStore` abstraction, its metadata/version record types, the `ISecretStoreBackend` driver port, and the `ISecretAccessAuditor` event port — interfaces, records, validators, xUnit mocks; no real data movement." The author of every Epic 29 follow-up (29-2 Postgres backend, 29-3 reveal-once UX, 29-4/29-5 admin UIs, 29-6/29-7/29-8 rotation workflows, 29-9 stopgap migration, 29-10 stopgap deletion) needs to know which calls were locked in here vs. which calls were intentionally deferred. Six substantive decisions had to be made:

1. Secret-identity shape — path/URN/(scope, tenantId, name) tuple?
2. Versioning model — how many versions kept, soft- vs hard-delete?
3. Encryption envelope split — what lives on the metadata record vs. the version record vs. the backend's storage?
4. Audit event shape — what fields, what storage stream, how many events per backend call?
5. Multi-tenancy scoping — platform-only, tenant-only, or both?
6. Naming collision with Epic 1.5-16's TypeScript `ISecretStore` — same name, separate file paths, or rename?

## Decisions

### #1 — Secret identity: `(scope, tenantId?, name)` tuple, opaque `SecretRef` value type

**Context**: AC7 says "secret `Name` is unique per `(scope, tenantId?)` — two tenants can both have `db/app-role`, and the platform can have `db/app-role` without collision." The choice was between (a) the tuple as the natural key, (b) a path-style URN like `tenant:<id>/db/app-role`, or (c) a UUID-only id with the tuple as a side index.

**Decision**: tuple. `SecretRef` is a sealed record with `Scope`, `TenantId?`, `Name`; `SecretMetadata.Id` is a separate UUID v7-style guid for foreign keys but every public API takes a `SecretRef`. The constructor enforces "`TenantId` non-null iff `Scope == Tenant`"; `SecretRef.ForPlatform(name)` and `SecretRef.ForTenant(tenantId, name)` are the ergonomic call sites. `ToStorageKey()` renders `platform:<name>` / `tenant:<guid>:<name>` for log fields and audit-event tags but the result is **not parsed back** — the tuple is the authority.

**Rationale**:
- Matches the existing `SecretScope` enum + `Guid? TenantId` pattern used elsewhere in the codebase (`AgentConfigRepository`, `PromptRepository`).
- Lets the Postgres driver (29-2) use a composite UNIQUE index on `(scope, tenant_id, name)` directly rather than parsing a string.
- A future OpenBao driver can map the tuple to whatever path style its KV engine prefers without burdening callers.
- Audit events still carry both `Reference` (the tuple) and `ToStorageKey()` (the rendered string) so dashboards have both shapes.

**Alternatives considered**:
- (b) URN string. Rejected: every consumer would have to validate + parse the same string, and the OpenBao mapping would be backwards (URN → KV path) instead of forwards (tuple → driver-native).
- (c) UUID-only id surface. Rejected: every admin endpoint would need a "look up the secret by name first, then call by id" round-trip; the tuple is already the natural key for `(scope, tenantId, name)` URLs.

### #2 — Versioning: monotonic 1-based, four-state lifecycle, scrub-but-keep-row deletion

**Context**: AC3 specifies a `SecretVersion` record with `Status ∈ {Pending, Active, RetiredGrace, Revoked}`. Open calls were (i) version-number type (int vs. timestamp vs. ulid), (ii) retention policy on revoked versions (drop the row vs. scrub the ciphertext but keep the row), (iii) how many `Active` versions can exist at once (one vs. zero-or-one).

**Decision**:
- Version numbers are **monotonic 1-based ints**, never reused. `ActiveVersionNumber = 0` means "row exists but no plaintext minted yet" (legal — covers the AC2 "freshly-created secret with no rotation" state).
- Lifecycle is exactly the four states from AC3. **Exactly one `Active` version per secret at any time** (enforced by the backend driver in 29-2; the in-memory backend doesn't enforce it because the facade does).
- `RetiredGrace` exists so in-flight requests don't fail mid-rotation; the grace timer flips to `Revoked` after `RotateSecretRequest.GraceWindow` (default 5 minutes).
- `Revoked` rows **stay in the table for audit history; only the ciphertext is scrubbed**. The backend `DeleteVersionAsync` is a scrub-but-keep-row op and is **idempotent** — calling it on an already-scrubbed row is a no-op. `GetVersionPlaintextAsync` returns `null` (not throw) for a scrubbed row, but throws `KeyNotFoundException` for a row that never existed.

**Rationale**:
- Monotonic 1-based ints match Postgres `IDENTITY GENERATED ALWAYS AS IDENTITY` and EF Core's `[DatabaseGenerated]` defaults — no surprises in 29-2.
- "Scrub but keep" matches the audit-trail compliance posture (SOC2 / ISO27001 / GDPR) the project already commits to: an event-sourced platform must not silently drop the existence of a secret version. The keep-row policy lets `ListVersionsAsync` show "v3 — Revoked at 2026-04-27" forever without holding the plaintext.
- Distinguishing "scrubbed" (null payload) from "never existed" (KeyNotFoundException) is the seam future tests need to assert "an attacker who guesses a version number cannot tell if it was ever issued" without leaking timing info — the facade can flatten both to "not authorised" before the HTTP boundary.

**Alternatives considered**:
- Timestamp / ULID version ids: rejected as overkill. The tuple `(secretId, versionNumber)` is small and dense; timestamps would force admin UIs to render long opaque strings.
- Hard-delete on revoke: rejected for the audit-trail reason above.
- Multiple concurrent `Active` versions: rejected; complicates rotation handlers (which one do they push?) without a real use case.

### #3 — Encryption envelope: backend owns the bytes, metadata stays plaintext

**Context**: AC4 describes `ISecretStoreBackend` with `PutVersionAsync(secretId, versionNumber, plaintext)` / `GetVersionPlaintextAsync` / `DeleteVersionAsync`. The open call was where the AES-GCM envelope lives — does `SecretMetadata` carry encrypted blobs, does `SecretVersion` carry them, or are they entirely on the backend's side?

**Decision**: **only the backend driver wraps + unwraps**. `SecretMetadata` and `SecretVersion` records carry **zero ciphertext** — they are pure descriptive metadata (name, scope, status, timestamps, owner, schedule, consumer refs). Plaintext flows through the in-process `ISecretStoreBackend` interface as a `string` (not `byte[]`) for ergonomics, never crosses an HTTP boundary, and is only handed to **registered rotation handlers** via the out-of-band `ISecretStore.RotateAsync` callback. The backend implementations are responsible for envelope encryption (29-2: KEK from env, AES-256-GCM per-version DEK, ciphertext + IV + tag in `secret_version_payload` table; 28-13 future: OpenBao KV engine).

**Rationale**:
- Keeps the audit + invariant-enforcement surface on the facade (`ISecretStore`) and the byte-handling surface on the driver (`ISecretStoreBackend`). A future OpenBao driver doesn't have to invent its own metadata model — it just implements `Put / Get / Delete` over a `(secretId, versionNumber)` key.
- Lets the Postgres driver split into two tables (`secrets` for metadata, `secret_version_payload` for ciphertext+iv+tag) without leaking that split into the C# domain model. 29-2 can change the on-disk layout (e.g. add a separate `kek_id` column for KEK rotation) without touching the abstraction.
- `string` over `byte[]` for plaintext: every Tamma secret today is a UTF-8 password / URL / token — never a binary blob. If a future use case needs binary, a sibling `PutVersionBytesAsync` overload is cheaper than rewriting every caller.

**Alternatives considered**:
- Encrypted blobs on the metadata record. Rejected: forces every caller (admin UI, list endpoint) to handle ciphertext even when it doesn't want it; a leaked log line of `SecretMetadata.ToString()` would still leak the wrapped key.
- Wrap/unwrap on the facade with the backend doing pure key-value storage. Rejected: every backend would need its own "should I trust the facade's wrapping" answer; centralising wrap on the driver lets OpenBao defer to its native KMS without an extra layer.

### #4 — Audit event shape: typed record, one event per backend call, dual-stream routing

**Context**: AC5 names seven event types (`SECRET.READ`, `SECRET.WRITE`, `SECRET.ROTATE.{STARTED|SUCCESS|FAILED}`, `SECRET.REVEAL`, `SECRET.VERSION.REVOKED`) and says "Every backend driver method call emits exactly one event." Open calls: (i) typed record vs. free JSON payload, (ii) which fields are mandatory, (iii) where do tenant-scoped vs. platform-scoped events get persisted, (iv) what about events that 29-9 needs (migrated)?

**Decision**:
- **Typed record** `SecretAuditEvent(EventType, Reference, ActorUserId, VersionNumber?, Outcome, Detail?, OccurredAt)`. Event types are **string constants** on `SecretAuditEventTypes` so dashboards / alert rules pattern-match against the constants rather than ad-hoc literals.
- **Outcome flag** is a coarse `Success | Failure` enum so dashboards can group "started/succeeded/failed" without parsing the event-type string. `Detail` is a free-form string for machine-readable reason codes (`backend_unavailable`, `handler_threw`); **never carries plaintext** (CredentialRedactor in Wave C.4 already covers this).
- `ActorUserId = Guid.Empty` for system-initiated events (scheduled auto-rotation, cleanup workers).
- **Dual-stream routing** (deferred-but-named): the `Reference` carries `Scope` + `TenantId?`, so the 29-2 `PostgresSecretAccessAuditor` writes platform-scoped events to `platform_events` and tenant-scoped events to that tenant's `domain_events`. Story 29-1 ships `NullSecretAccessAuditor` (drops events on the floor); 29-2 wires the real impl.
- **One event per backend call** is enforced by the facade (`ISecretStore` calls the auditor exactly once per `Get / Put / Rotate / Retire / Revoke`). The auditor must **not throw** on persistence failure — caller has already mutated state and an audit-log outage shouldn't roll that back; instead log the failure to the application log.
- **Extra event types** for stories beyond 29-1 are added to `SecretAuditEventTypes` as constants so 29-9 can emit `SECRET.MIGRATED.{SUCCESS|FAILED|SKIPPED}` without touching the abstraction. Already added.

**Rationale**:
- Typed record is the same shape `IPlatformEventRepository` expects today (no impedance mismatch in 29-2).
- "Auditor must not throw" matches the project's general "log and continue" pattern for non-critical observability paths and avoids silent data loss when the audit table is briefly unreachable.
- Dual-stream routing matches Doc-01 §5's platform-events / tenant-events split and means tenant admins viewing their secret-audit log only see their own tenant's events without an extra access-control filter.

**Alternatives considered**:
- Free JSON payload: rejected; bypasses C# type-checking and forces every consumer to parse.
- Single shared `domain_events` stream: rejected; tenant admins would see platform-event noise and platform admins would have to fan-out queries across every tenant DB.
- Throwing auditor: rejected per "audit outage shouldn't roll back the operator action" — operationally worse than dropping an event and logging the drop.

### #5 — Multi-tenancy: both scopes share one cabinet, name unique per `(scope, tenantId?)`

**Context**: AC2 lists `Scope ∈ {platform, tenant}` and `TenantId?`. AC7 spells out the namespacing rule. The open call was whether to keep platform secrets and tenant secrets in **one** logical cabinet (one `ISecretStore`, one backing table, one set of audit events) or **two** (`IPlatformSecretStore` + `ITenantSecretStore`, like the existing `IPlatformX` repository pattern).

**Decision**: **one cabinet, two scopes**. A single `ISecretStore` interface; `SecretRef` carries the scope; the Postgres driver (29-2) uses one `secrets` table with a `scope` enum column and a partial-unique index `(scope, tenant_id, name)`. Tenant-admin endpoints filter `Scope = Tenant AND TenantId = <session tenant>`; platform-admin endpoints filter `Scope = Platform` for the platform view and can list across tenants for the cross-tenant admin view. RLS on `secret_versions` (29-2 + 19-6) is the second line of defence.

**Rationale**:
- Most stopgaps Epic 29 migrates have **both** flavours: `tamma_app` is platform DB credential, per-tenant DB roles are tenant DB credentials. Forcing two separate stores would push the same rotation handler into two parallel implementations.
- Admin UI shape (29-4 platform / 29-5 tenant): both UIs lift from the same `ISecretStore` calls, just with different filters. Splitting would mean two service classes, two endpoint sets, two tests of the same shape.
- Backend-driver story is simpler: 29-2 ships **one** `PostgresSecretStoreBackend` that doesn't know about scope at all (it just sees `secretId`); the facade owns the scope filter on metadata reads.

**Alternatives considered**:
- Two interfaces (`IPlatformSecretStore` / `ITenantSecretStore`): rejected; doubles the surface area without buying any safety the existing scope filter doesn't already give.
- Tenant-only cabinet (platform secrets stay in `appsettings.json` env vars): rejected explicitly per the epic README — `TAMMA_SHARED_SECRET`, `Cranl:ApiKey`, `Cranl:EncryptionKey`, GitHub App key all need rotation + audit, which is exactly what the cabinet provides.

### #6 — Naming collision with Epic 1.5-16: keep both `ISecretStore`, separate namespaces

**Context**: Epic 1.5-16 ships a TypeScript `ISecretStore` for LLM-safe byte-oriented ops (commitment hashes, no plaintext to LLM). Epic 29 ships a C# control-plane `ISecretStore` for typed metadata + admin UX. Same name, different shapes. The plan's open question was whether to rename the C# one to `ISecretCabinet` (with an `ISecretStore` alias) to keep vocabulary aligned with the research notes.

**Decision**: **keep both named `ISecretStore`**. They live in different runtimes (TypeScript vs. C#), different file paths (`packages/intelligence/src/secrets/...` vs. `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/...`), and serve different callers (Elsa workflows + broker HTTP service vs. C# Minimal API + admin dashboards). The two will share the **same Postgres rows** at the row level (29-2 ships the schema; both surfaces read from `secrets` + `secret_version_payload`); a future consolidation pass may unify them but the seam is a row-level adapter in 29-2, not a type-level rename in 29-1.

**Rationale**:
- "Tomorrow we might unify them" is not a reason to introduce a name today. A `ISecretCabinet` alias would be visible in every C# call site and would never go away.
- The two already have separate import paths; there is no compile-time collision.
- Research-note vocabulary (`ISecretStore`) is what reviewers will already have in their head when they read 29-2..29-10; renaming would force every reader to learn a Tamma-specific term.

**Alternatives considered**:
- Rename C# to `ISecretCabinet`: rejected per above.
- Rename TS to `ISecretBox` / similar: out of scope for 29-1; would require coordinated changes in Epic 1.5.

## Out of scope (deferred to later stories)

Each of these is a real call but explicitly **not** Story 29-1's deliverable. Listing here so 29-2..29-10 reviewers know where to look.

- **AES-GCM envelope details (KEK source, DEK lifecycle, KEK rotation)** — Story 29-2 (`PostgresSecretStoreBackend`, `EnvKekProvider`, `KekRotationCoordinator`).
- **Reveal-once-on-create UX, rate-limited exchange endpoint** — Story 29-3.
- **Cron-expression parser (Cronos integration)** — Story 29-2; the calculator already has a `RegisterCronEvaluator` seam so 29-1 doesn't take a Cronos dependency.
- **Rotation saga orchestration (Elsa activities, retry policy, alert on failure)** — Story 29-6 + the Wave C.4 `SECRET.ROTATION.FAILED` alert.
- **Postgres role-password rotation handler** — Story 29-7.
- **Cranl env-var rotation handler (push + restart)** — Story 29-8.
- **Stopgap migration mechanics** — Story 29-9 (`StopgapSecretMigrator` already has the `SECRET.MIGRATED.*` event types reserved).
- **Stopgap deletion** — Story 29-10.
- **OpenBao backend driver** — Story 28-13 (deferred until a trigger fires per the KEK decision memory).
- **Cross-tenant consumer linking** (a tenant-A secret used by a tenant-B resource): explicitly out per the research notes §6 + the `ConsumerRef.cs` doc-comment.
- **Admin-UI authorisation (who can list / read / rotate which scope)** — Story 29-4 + 29-5; 29-1's facade returns `null` rather than throwing on "not authorised" so the admin endpoint can layer auth without leaking existence.

## Files shipped (already merged in `feat/wave-b`)

```
apps/tamma-elsa/src/Tamma.Api/Services/Secrets/
  ISecretStore.cs                — facade with 7 methods (AC1)
  ISecretStoreBackend.cs         — driver port (AC4)
  ISecretAccessAuditor.cs        — auditor port + NullSecretAccessAuditor
                                   + SecretAuditEventTypes constants
                                   + SecretAuditEvent record
                                   + SecretAuditOutcome enum (AC5)
  SecretMetadata.cs              — record (AC2)
  SecretVersion.cs               — record + SecretVersionStatus enum (AC3)
  SecretRef.cs                   — opaque (scope, tenantId?, name) tuple
                                   + ForPlatform / ForTenant factories (AC7)
  SecretScope.cs                 — Platform | Tenant enum
  SecretPurpose.cs               — DbCredential | ApiKey | SigningKey
                                   | HmacSharedSecret | Webhook | Connection
                                   | Other (AC2)
  SecretRequests.cs              — CreateSecretRequest, RotateSecretRequest,
                                   SecretListFilter, SecretValue records
  RotationSchedule.cs            — None | Days(n) | Cron(expr) discriminated
                                   union + TryParse / ToString round-trip
  RotationScheduleCalculator.cs  — pure NextDue function with cron-evaluator
                                   seam (Story 29-2 plugs in Cronos) (AC6)
  ConsumerRef.cs                 — (System, Identifier) record (AC8)
  ConsumerRefLookup.cs           — system-key → human label / link template
  SecretMetadataFactory.cs       — Create / WithRotation / WithEdits guarded
                                   factories enforcing AC10 invariants
  InMemorySecretStoreBackend.cs  — test fixture (AC9)
  SecretsServiceCollectionExtensions.cs
                                 — AddTammaSecrets() DI extension wiring
                                   the null auditor + in-memory backend

apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/
  ConsumerRefLookupTests.cs               — every system key renders correctly
  InMemorySecretStoreBackendTests.cs      — 13 tests; the contract suite
                                            29-2's Postgres driver also satisfies
  NullSecretAccessAuditorTests.cs         — null auditor drops + canonical event types
  RotationScheduleCalculatorTests.cs      — DST + leap-year + cron-evaluator seam
  SecretMetadataFactoryTests.cs           — AC10 enum × scope matrix
  SecretStoreBackendMockingTests.cs       — AC9 swap-resilience: ISecretStore +
                                            ISecretStoreBackend + ISecretAccessAuditor
                                            are all mockable via Moq, the swap
                                            from one mock backend to another routes
                                            through the same facade unchanged
```

## Test posture (verified 2026-04-27)

`dotnet test --filter "Secrets"` passes 475 tests (includes downstream
29-2..29-10 tests that share the Secrets folder; the 29-1-specific
files are the ~50 tests above). No regressions on the full suite.
