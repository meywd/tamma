# Story 29-1: Secret Store Abstraction + Typed Data Model

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want a typed `ISecretStore` abstraction with a rich metadata model (name, scope, purpose, consumers, owner, rotation schedule, last-rotated-at),
so that the next nine stories can wire admin UIs, rotation workflows, and backend drivers to one shape — and so a future swap to OpenBao or a cloud KMS is a driver swap instead of a re-architecture.

## Acceptance Criteria

1. `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` defines the read/write surface: `GetAsync`, `ListAsync`, `CreateAsync`, `RotateAsync`, `RetireAsync`, `GetVersionAsync`, `ListVersionsAsync`. None of them return a plaintext value through the HTTP-visible API; plaintext is returned only from the in-process `ISecretStore` to a registered rotation handler.
2. A `SecretMetadata` record captures: `Id`, `Name` (unique-per-scope slug), `Scope` (`platform` | `tenant`), `TenantId?`, `Purpose` (enum: `DbCredential`, `ApiKey`, `SigningKey`, `HmacSharedSecret`, `Webhook`, `Connection`, `Other`), `ConsumerRefs` (list of `{ system, identifier }` — e.g. `{ "postgres", "role=tamma_app" }`, `{ "cranl", "app_id=..." }`), `OwnerUserId`, `RotationSchedule` (`None` | `Days(n)` | `Cron(expr)`), `LastRotatedAt`, `NextRotationDueAt`, `ActiveVersionNumber`, `CreatedAt`, `UpdatedAt`.
3. A `SecretVersion` record captures: `SecretId`, `VersionNumber` (monotonic), `Status` (`Pending`, `Active`, `RetiredGrace`, `Revoked`), `CreatedAt`, `ActivatedAt?`, `RetiredAt?`, `CreatedByUserId`. Plaintext bytes are **not** in this record — they live in the backend driver's storage; only `RotationHandler`s receive them through an out-of-band `RotateAsync` path.
4. An `ISecretStoreBackend` port defines the driver contract: `PutVersionAsync(SecretId, VersionNumber, Plaintext, Ct)`, `GetVersionPlaintextAsync(SecretId, VersionNumber, Ct)`, `DeleteVersionAsync(SecretId, VersionNumber, Ct)`. The Postgres driver (Story 29-2) is one implementation; a future OpenBao driver is another.
5. An `ISecretAccessAuditor` port emits `SECRET.READ`, `SECRET.WRITE`, `SECRET.ROTATE.STARTED|SUCCESS|FAILED`, `SECRET.REVEAL`, `SECRET.VERSION.REVOKED` events via `platform_events` (platform-scoped) or tenant `domain_events` (tenant-scoped). Every backend driver method call emits exactly one event.
6. `RotationScheduleCalculator` computes `NextRotationDueAt` from a schedule + `LastRotatedAt`; `None` schedules return null. Unit-tested across DST boundaries with dayjs-equivalent NodaTime fixtures.
7. Namespacing: secret `Name` is unique per `(scope, tenantId?)` — two tenants can both have `db/app-role`, and the platform can have `db/app-role` without collision.
8. `ConsumerRef` supports typed consumers via a small lookup table so the admin UI can render "Used by: Tamma API (TammaAppDbContext)", "Cranl app `app_xyz`", "GitHub webhook", etc., rather than raw identifiers.
9. xUnit test fixtures mock `ISecretStoreBackend` so rotation / admin-service tests in later stories do not require a real Postgres.
10. No runtime consumer may construct a `SecretMetadata` with `Purpose = DbCredential` and `Scope = tenant` unless it also sets a non-null `TenantId`. Guarded by a factory method + a xUnit `Theory` covering the full enum × scope matrix.

## Technical Context

### Why a separate abstraction from Epic 1.5's `ISecretStore`

Epic 1.5-16 defines a TypeScript `ISecretStore` focused on LLM-safe
byte-oriented operations (commitment hashes, platform mirrors, no
plaintext to LLM). Epic 29's `ISecretStore` is a C# control-plane
cabinet with typed metadata, rotation handlers, and admin UX as
first-class concerns. They share the same crypto primitives (both use
AES-256-GCM envelope) and will share the root vault store's schema at
the row level, but the callers differ: Epic 1.5 is consumed by Elsa
workflows and the broker HTTP service; Epic 29 is consumed by the C#
Minimal API + admin dashboards.

A future consolidation pass may unify them. For now the guardrail is:
both interfaces delegate to the same Postgres rows in Story 29-2, and
tests assert that a value written through one surface is retrievable
through the other (as a byte array, not as typed metadata).

### `ISecretStore` sketch

```csharp
public interface ISecretStore
{
    Task<SecretMetadata> CreateAsync(CreateSecretRequest req, CancellationToken ct);
    Task<SecretMetadata?> GetAsync(SecretRef refId, CancellationToken ct);
    Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretListFilter filter, CancellationToken ct);
    Task<SecretMetadata> RotateAsync(SecretRef refId, RotateSecretRequest req, CancellationToken ct);
    Task<SecretMetadata> RetireVersionAsync(SecretRef refId, int version, CancellationToken ct);
    Task<SecretVersion?> GetVersionAsync(SecretRef refId, int version, CancellationToken ct);
}
```

`CreateSecretRequest` contains the `SecretMetadata` fields plus an
optional initial plaintext (for imports). `RotateSecretRequest`
contains an optional `NewPlaintext` (operator-supplied) or a
`GenerateLength` (auto-generate). Generator uses `RandomNumberGenerator.GetBytes`.

### Non-goals for this story

- No UI (stories 29-4 and 29-5).
- No Postgres schema (29-2).
- No rotation workflow (29-6..29-8).
- No migration of existing secrets (29-9).

## Estimated hours

16 — interface + types + access auditor + schedule calculator + mocks
+ unit tests; no real storage or UI.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/` (new folder)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/` (new folder)

## References

- Epic 29 README: [`./README.md`](./README.md)
- Research notes: [`../research/secret-management-and-multi-backend-provisioning-2026.md`](../research/secret-management-and-multi-backend-provisioning-2026.md) §4
- Epic 1.5-16 crypto primitives we reuse: `docs/stories/epic-1.5/story-1.5-16/1.5-16-secret-store-interface.md`
