# Story 29-1 Implementation Plan — Secret Store Abstraction

**Status**: Planned (2026-04-20)
**Story brief**: [`29-1-secret-store-abstraction.md`](./29-1-secret-store-abstraction.md)
**Epic 29 phase**: Foundation — land first.
**Branch**: `feat/story-29-1-secret-store-abstraction`

---

## 1. Objective

Ship a typed `ISecretStore` abstraction, its metadata/version record
types, the `ISecretStoreBackend` driver port, and the
`ISecretAccessAuditor` event port. Nothing reads or writes real data
in this story — just the interfaces, records, validators, and xUnit
mocks that let every subsequent Epic 29 story wire admin UIs, rotation
workflows, and backend drivers to one shape. This is the seam that
keeps "Postgres today, OpenBao tomorrow" a driver swap rather than a
re-architecture.

## 2. Dependencies

Hard blockers:

- **Story 28-3** — the `TenantDbContext` factory (so tenant-scoped
  `tenant_secrets` rows will route correctly once 29-2 ships).

Soft:

- **Story 1.5-16** (Epic 1.5 crypto primitives) — same AES-256-GCM
  envelope format is reused; this story defines the C# shape.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` | Typed read/write surface. |
| `.../Services/Secrets/SecretMetadata.cs` | Record with Name, Scope, TenantId?, Purpose, ConsumerRefs, Owner, Rotation schedule, LastRotatedAt, NextRotationDueAt, ActiveVersionNumber, timestamps. |
| `.../Services/Secrets/SecretVersion.cs` | Record with SecretId, VersionNumber, Status, timestamps, CreatedByUserId. |
| `.../Services/Secrets/SecretRef.cs` | Opaque reference (scope, tenantId?, name). |
| `.../Services/Secrets/SecretPurpose.cs` | Enum: DbCredential, ApiKey, SigningKey, HmacSharedSecret, Webhook, Connection, Other. |
| `.../Services/Secrets/RotationSchedule.cs` | `None | Days(n) | Cron(expr)` union. |
| `.../Services/Secrets/ConsumerRef.cs` | `{ System, Identifier }` + lookup table. |
| `.../Services/Secrets/ISecretStoreBackend.cs` | Driver port. |
| `.../Services/Secrets/ISecretAccessAuditor.cs` | Emits `SECRET.*` events. |
| `.../Services/Secrets/RotationScheduleCalculator.cs` | `NextDue(schedule, lastRotated)`. |
| `.../Services/Secrets/SecretMetadataFactory.cs` | Guarded factory enforcing AC10 invariants. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/SecretMetadataFactoryTests.cs` | Enum × scope guard, property tests. |
| `.../Secrets/RotationScheduleCalculatorTests.cs` | DST boundaries, cron edge cases. |
| `.../Secrets/ConsumerRefLookupTests.cs` | Typed rendering. |
| `.../Secrets/MockSecretStoreBackend.cs` | Test fixture — in-memory impl used by later stories. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add `Services/Secrets/` folder. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `ISecretAccessAuditor` (null impl — 29-2 swaps for real one). |

## 5. Sequence of changes

### Step 1 — Enums + records (2h)

- `SecretPurpose`, `RotationSchedule`, `SecretVersion`, `SecretRef`.
- Exhaustive pattern-match helpers on `RotationSchedule`.
- **Commit**: `feat(secrets): value types`.

### Step 2 — Consumer map + lookup (2h)

- `ConsumerRef` + lookup table (maps `system` key to human label
  and UI link template).
- Tests: every system key renders correctly.
- **Commit**: `feat(secrets): consumer reference + lookup`.

### Step 3 — `SecretMetadata` + factory (3h)

- Metadata record.
- Factory asserts AC10 invariants (DbCredential × Tenant requires
  non-null TenantId, etc.).
- xUnit Theory over enum × scope matrix.
- **Commit**: `feat(secrets): SecretMetadata + factory`.

### Step 4 — Schedule calculator (2h)

- `NextDue(schedule, lastRotated)` for None/Days/Cron.
- NodaTime for DST-safe cron; test across spring-forward / fall-back.
- **Commit**: `feat(secrets): rotation schedule calculator`.

### Step 5 — Port interfaces + auditor (3h)

- `ISecretStore`, `ISecretStoreBackend`, `ISecretAccessAuditor`.
- Null `ISecretAccessAuditor` impl.
- **Commit**: `feat(secrets): store + backend + auditor ports`.

### Step 6 — Mock backend + fixtures (2h)

- `MockSecretStoreBackend` in-memory impl for test reuse.
- Fixture factory for later story tests.
- **Commit**: `test(secrets): in-memory mock backend`.

### Step 7 — DI + docs (2h)

- `Program.cs` registers null auditor.
- `docs/stories/epic-29/architecture-notes.md` records the
  abstraction decisions.
- **Commit**: `docs(secrets): abstraction architecture notes`.

## 6. Test strategy

### Unit

- Factory invariants (AC10).
- Schedule calculator DST tests (at least 6 boundary cases).
- Consumer-ref lookup (render every system type).
- Pattern-match exhaustiveness on `RotationSchedule`.

### Integration

- None required — this is an interface-only story.

## 7. Rollback plan

- **Revert**: single chain; no persistent data, no migration.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Value types | 2 |
| 2. Consumer map | 2 |
| 3. Metadata + factory | 3 |
| 4. Schedule calculator | 2 |
| 5. Ports + auditor | 3 |
| 6. Mock backend | 2 |
| 7. DI + docs | 2 |
| **Total** | **16** (matches brief). |

## 9. Open questions

- **Unified with Epic 1.5-16's `ISecretStore`?** Brief says "a future
  consolidation pass may unify them". Confirm interface naming so the
  consolidation is painless — currently both use `ISecretStore` which
  is a collision waiting to happen. Plan: name the C# one
  `ISecretCabinet` internally, alias to `ISecretStore` for matching
  research-note vocabulary. Open for user review.
- **`RotationSchedule.Cron(expr)` syntax**: standard 6-field cron or
  Quartz? Plan: Cronos library (C# standard).
- **Audit event data shape**: fixed JSON or typed record? Plan: typed
  record serialised per `DomainEvent.Payload` JSON format.
- **Cross-tenant consumer linking**: a secret's consumer could point
  at another tenant's resource (unusual but possible). Current
  design scopes consumer-ref interpretation to the secret's own
  scope; cross-tenant linking is explicitly out.
