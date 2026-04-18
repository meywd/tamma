# Story 28.13: OpenBao KMS Backend for Tenant KEK (Deferred)

**Epic**: Epic 28 — Database-per-Tenant Isolation
**Category**: Operations / Security
**Status**: **DEFERRED** — do not schedule until a trigger condition
(§ "Trigger Conditions" below) is met. Story 28-12 ships with the
env-var KEK design from Doc 01 §8.2 instead; this story is the
follow-on that swaps the backend when the cost/benefit shifts.
**Priority**: Low (today) / High (once a trigger fires)
**Estimated Effort**: L–XL (30–45h, depending on topology decision —
§ AC1)

## User Story

As a **security engineer operating Tamma at scale**, I want **the
master tenant-KEK moved out of the API pod's environment into an
OpenBao instance that exposes a Transit-engine encrypt/decrypt API**,
so that **a compromise of the API pod's memory, filesystem, or env
does not leak the key that protects every tenant's Postgres
credentials — and so that rotation becomes a single OpenBao API call
instead of a 90-minute bespoke runbook**.

## Why Not Now (the simpler decision)

Story 28-12 keeps the KEK directly in `TAMMA_TENANT_KEK_PRIMARY` /
`TAMMA_TENANT_KEK_SECONDARY` env vars, sourced from Hetzner sealed
secrets. That design is **deliberately simpler** than OpenBao for
the following reasons — each of which must flip before this story
gets scheduled:

1. **No paying tenants yet.** Breach blast radius is bounded to
   Tamma's own dogfood data. The threat model that justifies a
   hardened KMS (attacker compromises the API, dumps memory, walks
   away with the KEK, decrypts every tenant's connection string
   offline) is real but not proportional to current exposure.
2. **`ISecretsService` is already a seam** (Story 28-12 AC2).
   Swapping the backend from env-var to OpenBao Transit is a
   targeted code change, not an architectural migration. We lose
   nothing by deferring.
3. **OpenBao's governance is still maturing.** Five org-wide
   maintainers as of early 2026, Dev WG charter ratified late 2024,
   IBM's dual role (owns Vault post-HashiCorp acquisition,
   contributes to OpenBao) creates near-term strategic uncertainty.
   Linux Foundation stewardship protects the license, not the
   contribution velocity.
4. **Operational surface cost.** Adopting OpenBao means running it,
   monitoring it, backing up its storage, rotating *its* unseal keys,
   and making it part of the DR runbook. Realistic ongoing cost:
   2–4 hours/month of operator attention + one more pager target.
5. **Hetzner sealed secrets is adequate** for the current threat
   model. The KEK lives in process memory, yes — but the API pod
   runs on a private-network VPS behind Cloudflare, with SSH locked
   to key auth, and the env var is not written to disk. An attacker
   who can dump the pod's memory has already achieved code
   execution in the trust boundary that owns the Postgres cluster
   anyway — they don't need the KEK to cause harm.
6. **Concrete complexity avoided**: no OpenBao client dependency
   in `Tamma.Api.csproj`, no `BaoSharp` / `VaultSharp` version
   churn, no bootstrap paradox (how does the API authenticate to
   OpenBao on first boot?), no second storage backend to back up,
   no additional container in docker-compose.

**If any trigger below fires, reopen this story.** Until then, the
env-var KEK is the correct choice.

## Trigger Conditions (Definition of Ready)

Schedule this story when **any one** of the following becomes true:

- [ ] Tamma signs its **first paying tenant with a data-breach
      notification clause** (typically SOC 2 Type II, ISO 27001,
      HIPAA BAA, or GDPR processor agreement with art. 32 wording
      about "state of the art" key management).
- [ ] A compliance auditor **explicitly flags** the env-var KEK as a
      finding requiring remediation.
- [ ] Tamma crosses **10 paying tenants**, at which point the
      aggregate breach cost justifies the operational overhead.
- [ ] The threat model changes materially — e.g. Tamma moves to a
      multi-tenant shared-cluster host where "dump the pod's memory"
      becomes a lower bar.
- [ ] OpenBao reaches **Linux Foundation stage 3 (graduated)** or
      equivalent, resolving the governance-maturity risk in § "Why
      Not Now" point 3.

Record the trigger that fired in the commit that un-defers this
story, so the "why now" reasoning is traceable.

## Acceptance Criteria (once un-deferred)

### AC1: Topology decision (single-VPS vs separated)

Before implementation, the team must pick one:

- [ ] **Co-located** — OpenBao runs as a sibling container on the
      same Hetzner VPS, behind the private Docker network. Pros:
      simple deploy, zero new infrastructure. Cons: a host
      compromise still gets both the API and OpenBao. **Marginal
      security improvement over env-var** — OpenBao still isolates
      the key to its own process memory, but the host-level trust
      boundary is shared.
- [ ] **Separated VPS** — OpenBao runs on a dedicated Hetzner box
      reachable only over Hetzner private network (no public
      ingress). Pros: meaningful security boundary — API
      compromise does not reach OpenBao. Cons: +1 box to run,
      network-partition DR scenario to design, latency hop of ~1ms
      per encrypt/decrypt.
- [ ] Decision recorded in `.dev/decisions/` with rationale tying
      back to the trigger condition that opened this story.

Recommendation: **if the trigger is a paying-tenant breach clause,
pick separated.** Co-located is not meaningfully better than the
env-var design it replaces for that threat.

### AC2: `OpenBaoKekProvider` implementation of `ISecretsService`

- [ ] New file
      `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/OpenBaoKekProvider.cs`
      implements the same `ISecretsService` contract as the
      env-var `KekProvider` from Story 28-12.
- [ ] Uses OpenBao Transit engine via HTTPS (not the deprecated
      `raw` API):
      - `POST /v1/transit/encrypt/tamma-tenant-kek` with
        `{"plaintext": "<base64>"}` → returns `{"ciphertext":
        "vault:v1:abc..."}`
      - `POST /v1/transit/decrypt/tamma-tenant-kek` with
        `{"ciphertext": "vault:v1:abc..."}` → returns
        `{"plaintext": "<base64>"}`
- [ ] **Never reads the KEK bytes itself** — the key stays in
      OpenBao. This is the point.
- [ ] Connection pooling: one `HttpClient` per
      `OpenBaoKekProvider` instance, registered as singleton.
      Configured with 10s timeout, retry once on transient
      (network / 503), no retry on auth failure.
- [ ] Thread-safe. The existing `AesGcmSecretsService` expects
      concurrent callers from the connection-resolver LRU cache.
- [ ] Behind the same `ISecretsService` interface, selectable via
      `Tamma:Secrets:Backend` config (`envvar` | `openbao`).
      Default remains `envvar` until ops explicitly flips it per
      host.

### AC3: Bootstrap authentication (the chicken-and-egg problem)

The API pod needs a credential to talk to OpenBao. That credential
must itself be bootstrapped. Pick **one**:

- [ ] **AppRole auth** (OpenBao built-in). Two secrets on the API
      pod: `OPENBAO_ROLE_ID` (public, fine in env) and
      `OPENBAO_SECRET_ID` (rotating, sealed). API uses these to
      fetch a short-lived token at startup. **Recommended** — it's
      the pattern Vault ecosystem has used for a decade.
- [ ] **Cloudflare/Hetzner identity** — if either service adds a
      workload-identity feature (not currently available), use it.
      Removes the static `SECRET_ID`. Not viable today.
- [ ] **Kubernetes service-account token** — not applicable while
      Tamma is on Docker compose.

Whichever is chosen, document in `.dev/decisions/openbao-auth.md`.

### AC4: Data migration from env-var envelopes to OpenBao ciphertexts

- [ ] One-shot migration workflow
      `Workflows/Admin/RewrapToOpenBaoWorkflow.cs`:
      1. For each row in `tenants`:
         - Decrypt `EncryptedConnectionString` under the env-var KEK
           (old backend).
         - Encrypt plaintext via `OpenBaoKekProvider.EncryptAsync`
           (new backend).
         - Write result back with envelope version byte `0x02` to
           indicate "OpenBao-wrapped" (distinct from `0x01`
           env-var-wrapped).
      2. Emit `TENANT.KEK_REWRAPPED` to `platform_events` per row.
      3. Final event `PLATFORM.KEK_MIGRATION.COMPLETE` with
         summary (count, duration, any failures).
- [ ] Migration runs **online** — tenants stay reachable throughout.
      `DecryptTenantConnectionString` dispatches on the version
      byte, so a mixed fleet (some rows `0x01`, some `0x02`) works.
- [ ] After migration completes, `Tamma:Secrets:Backend` flips to
      `openbao` and the env-var KEK secret is **revoked** in
      Hetzner sealed secrets. Rotation runbook updated.

### AC5: Key rotation via OpenBao

OpenBao Transit handles key versioning natively — this is the
operational win:

- [ ] Rotation: `POST /v1/transit/keys/tamma-tenant-kek/rotate` —
      creates a new key version, old versions remain usable for
      decrypt.
- [ ] Re-wrap background task reads every `tenants` row, calls
      `POST /v1/transit/rewrap/tamma-tenant-kek` per ciphertext
      (OpenBao decrypts with the version the ciphertext was
      created under, re-encrypts with the latest). App code does
      not need to know which key version any row uses.
- [ ] Once rewrap is complete, `POST /v1/transit/keys/
      tamma-tenant-kek/config` with
      `{"min_decryption_version": <latest>}` to disable old key
      versions cryptographically.
- [ ] Full rotation runbook target: **<15 minutes of operator
      attention**, down from Story 28-12's 90 minutes.

### AC6: Audit integration

- [ ] OpenBao audit backend (`file` or `socket`) configured to emit
      every encrypt/decrypt operation.
- [ ] A lightweight tailer service (new `Tamma.Api/Services/Audit/
      OpenBaoAuditTailer.cs`) reads the audit stream and emits
      corresponding `SECRETS.KEK_OP.SUCCESS` /
      `SECRETS.KEK_OP.FAILED` rows to `platform_events` with
      `{tenant_id, operation, token_hash, timestamp}` — token
      bytes never logged, only a hash for correlation.
- [ ] Audit integrity check: a nightly workflow asserts every
      `platform_events` decrypt event has a matching OpenBao audit
      entry. Drift = alert.

### AC7: Disaster recovery and failure modes

- [ ] **OpenBao unreachable at API startup** → API refuses to
      serve any tenant-scoped request (returns 503 with
      `X-Secrets-Backend-Status: unavailable`). Unauthenticated
      endpoints (health check, registration which uses CP outbox)
      still work.
- [ ] **OpenBao unreachable mid-flight** → the connection-resolver
      LRU cache keeps existing tenant connections alive. New
      tenants, connection-pool misses, and rotations fail until
      OpenBao recovers. Acceptable — this is the same behaviour as
      "tenant DB unreachable" today.
- [ ] **OpenBao storage corruption** → recovery from OpenBao's
      Raft snapshot (if using integrated storage) or from the file
      storage backup. Runbook documented.
- [ ] **OpenBao unseal keys lost** → catastrophic. Document the
      M-of-N unseal-key custody model (typically 3-of-5 with keys
      held by distinct operators, offline).
- [ ] DR test: a `make openbao-dr-drill` target stops OpenBao,
      restores from snapshot, unseals, verifies API comes back
      green. Run quarterly.

### AC8: Testing

- [ ] Unit tests `OpenBaoKekProviderTests.cs` with mocked HTTP
      responses covering: successful encrypt, successful decrypt,
      network error, 401 (token expired), 403 (wrong policy), 503
      (OpenBao sealed), malformed response.
- [ ] Integration test `OpenBaoIntegrationTests.cs` using
      Testcontainers to spin up a real OpenBao instance per test
      class. Verifies end-to-end encrypt → decrypt → rotate →
      rewrap cycle.
- [ ] `DecryptTenantConnectionString` dispatch test: a row with
      envelope version `0x01` (env-var backend) and a row with
      version `0x02` (OpenBao backend) both decrypt correctly when
      both backends are wired up concurrently during migration.
- [ ] Load test: 1000 concurrent decrypt calls; p95 <15ms on the
      separated-VPS topology (AC1), <5ms on co-located. (LRU cache
      should absorb most of this; OpenBao is only hit on cache
      miss.)

## Files

**New files:**
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/OpenBaoKekProvider.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/IOpenBaoClient.cs`
  (thin wrapper over `HttpClient` for testability)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/OpenBaoAuthHandler.cs`
  (AppRole login + token refresh)
- `apps/tamma-elsa/src/Tamma.Api/Services/Audit/OpenBaoAuditTailer.cs`
- `apps/tamma-elsa/src/Tamma.Api/Workflows/Admin/RewrapToOpenBaoWorkflow.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/Secrets/OpenBaoKekProviderTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/Secrets/OpenBaoIntegrationTests.cs`
- `docker/openbao/docker-compose.yml` (new compose overlay for
  OpenBao service + storage volume)
- `docker/openbao/config.hcl` (OpenBao config with Transit engine,
  AppRole auth, file audit backend)
- `scripts/openbao-bootstrap.sh` (one-shot: init, unseal, create
  `tamma-tenant-kek` transit key, create AppRole, output role_id
  and secret_id)
- `.dev/decisions/openbao-topology.md`
- `.dev/decisions/openbao-auth.md`
- `docs/ops/openbao-runbook.md` (rotation, DR, incident response)

**Modified files:**
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register
  `OpenBaoKekProvider` behind `Tamma:Secrets:Backend=openbao`
  config, keep env-var as default.
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/AesGcmSecretsService.cs`
  — extend envelope version-byte dispatch to include `0x02`
  (OpenBao-wrapped).
- `docker/docker-compose.yml` — optionally include OpenBao compose
  overlay.
- `docs/stories/plans/db-per-tenant/01-control-plane-split.md` §8 —
  amend to note OpenBao as the successor backend once this story
  ships.

## Dependencies

- **Required**: Story 28-12 (env-var KEK provider + `ISecretsService`
  seam) must be shipped and stable.
- **Required**: Story 28-5 (tenant provisioning workflow) must exist
  so the rewrap workflow has a template.
- **Nice-to-have**: Story 28-10 (`platform_analytics_hourly`) for
  KEK-operation metrics dashboards.

## Risks / Open Questions

- **Bootstrap token custody**: the `SECRET_ID` for AppRole auth is a
  static-ish secret the API pod needs to read. If it leaks, attacker
  can get a short-lived token. Mitigation: rotate `SECRET_ID` on a
  schedule (OpenBao supports this natively via `secret_id_ttl`).
- **OpenBao unseal-key custody**: this is the new single point of
  loss. Losing the unseal keys is worse than losing the env-var KEK
  was, because you *also* lose all ciphertext. Document the M-of-N
  split before adopting.
- **Latency on cache miss**: the LRU connection cache has ~99%+ hit
  rate in steady state (typical tenant sessions). But on cold start,
  every tenant's connection gets decrypted once. With 100 tenants
  and ~10ms per OpenBao round-trip, that's 1s of startup delay.
  Acceptable; just design for it.
- **Backup coupling**: OpenBao's storage backend now holds
  data-recovery-critical material. Its backup cadence and retention
  must match the Postgres cluster's. New ops invariant.
- **BaoSharp / VaultSharp maturity**: .NET client libraries for
  OpenBao/Vault have been less actively maintained than Go/Python/
  Java counterparts. Verify the chosen client supports the exact
  Transit API calls used above, with decent test coverage, at the
  time this story is picked up.

## Estimate Breakdown

| Task | Hours |
|------|-------:|
| Topology decision + ADR (AC1) | 3 |
| `OpenBaoKekProvider` + HTTP client + AppRole auth (AC2, AC3) | 8 |
| Docker compose + bootstrap script + config (ops plumbing) | 4 |
| Rewrap workflow + migration test (AC4) | 6 |
| Rotation integration + rewrap background task (AC5) | 4 |
| Audit tailer + integrity check (AC6) | 4 |
| DR runbook + quarterly drill target (AC7) | 3 |
| Unit + integration + load tests (AC8) | 6 |
| Docs + runbook + decision records | 3 |
| **Total (co-located topology)** | **~41** |
| **Incremental if separated topology** | +6 (networking, DR scenarios) |

## Deferral Record

This story was created on 2026-04-17 alongside Story 28-12, which
intentionally ships the simpler env-var KEK design. Rationale is
recorded in full under § "Why Not Now" above. Un-defer only when a
trigger condition (§ "Trigger Conditions") fires. The `ISecretsService`
seam in Story 28-12 guarantees that deferral costs nothing at the
time of adoption — the swap is local to the secrets module.

## Sources

- OpenBao project: https://openbao.org/
- OpenBao Transit engine docs (historical Vault Transit applies):
  https://developer.hashicorp.com/vault/docs/secrets/transit
- OpenBao maintainers (as of 2026-04-17):
  https://github.com/openbao/openbao/blob/main/MAINTAINERS.md
- Tamma Doc 01 §8 (current envelope-encryption design):
  `docs/stories/plans/db-per-tenant/01-control-plane-split.md`
- Story 28-12 (the baseline this story replaces):
  `docs/stories/epic-28/story-28-12/28-12-postgres-roles-kek-rotation.md`
