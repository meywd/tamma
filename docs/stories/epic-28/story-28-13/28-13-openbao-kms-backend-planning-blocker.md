# Story 28-13 Planning Blocker — OpenBao KMS Backend

**Status**: Deferred (2026-04-20) — no implementation plan authored.
**Story brief**: [`28-13-openbao-kms-backend.md`](./28-13-openbao-kms-backend.md)

---

## Why no impl plan

Story 28-13 is explicitly marked `Status: DEFERRED` in the brief and
in `~/.claude/projects/-home-meywd-tamma/memory/MEMORY.md`
(`project_epic28_kek_decision`). The epic-28 KEK decision record
states that Tamma ships with env-var KEK (Doc 01 §8.2) and **defers
OpenBao to Story 28-13 until a trigger fires**.

The brief enumerates four trigger conditions, **all of which are
currently false**:

1. First paying tenant onboarded (today: 0 — Tamma dogfoods itself).
2. Compliance finding: SOC 2 or ISO 27001 auditor flags env-var KEK.
3. 10+ tenants (today: ~0 — blast-radius argument has not shifted).
4. OpenBao reaches LF graduation **and** operators agree to adopt.

None of these trigger conditions has fired as of 2026-04-20.
Authoring a full impl plan now would prematurely lock in:

- OpenBao topology (single HA cluster vs. per-region vs. sidecar)
- Transit-engine API shape (latest OpenBao 2.x vs. 1.x differences)
- Runbook timing for the cutover (today's env-var KEK + tomorrow's
  OpenBao round-trip → latency shape unknown)
- Break-glass fallback policy (keep env-var as backup or hard-delete?)

## What unblocks this story

Three decisions must be recorded before a full impl plan is worth
writing:

1. **Product decision**: one of the four trigger conditions must fire,
   OR an operator-level policy change must override the defer (e.g.
   "we are about to sign our first enterprise contract and the
   contract requires HSM/KMS-backed KEK"). Recommended trigger:
   first paying enterprise tenant. Until then, stay on env-var KEK
   (Story 28-12).
2. **Topology decision**: OpenBao single cluster (simplest, one
   point of failure) vs. per-region (resilience, cost). Recommend
   single cluster for first ship, pivot to per-region only after a
   disaster-recovery exercise justifies it. Decision owner: Deploy
   Coordinator.
3. **Fallback policy**: during soak, keep env-var KEK as break-glass
   behind a feature flag (`Secrets:AllowEnvFallback=true`); flip
   `false` after 2-week soak. Decision owner: Security lead.

## What the stub plan would look like (for reference)

If and when unblocked, the impl plan would cover:

- Add `OpenBaoSecretsService : ISecretsService` that wraps the
  Transit-engine client (`VaultSharp` NuGet for .NET client).
- Migrate `tenants.DbConnectionCiphertext` by re-encrypting under
  an OpenBao-managed key (single rotation cycle, reusing the
  28-12 rotation worker).
- Update runbook: Transit-engine rotation is one API call vs. the
  90-minute 28-12 procedure.
- Operator docs: OpenBao deployment topology, `vault operator init`,
  unseal procedure, recovery shares distribution.

Estimated hours when unblocked: **30–45h** (per brief), broken down
as: OpenBao deployment (8h), client wiring (6h), rotation-worker
adapter (6h), integration tests (8h), operator runbook (6h), soak
(4–16h depending on fallback policy).

## Cross-references

- Story 28-12 (`28-12-postgres-roles-kek-rotation.md`) — ships the
  env-var KEK + rotation worker that 28-13 replaces.
- `memory://project_epic28_kek_decision.md` — decision record.
- Research notes §5 of
  [`secret-management-and-multi-backend-provisioning-2026.md`](../../research/secret-management-and-multi-backend-provisioning-2026.md)
  — OpenBao readiness assessment.

## Action for the caller

**Do not schedule this story until a trigger has fired and the three
decisions above are recorded.** When ready to unblock:

1. Append a "Trigger fired" section to this document with date,
   trigger, and ADR link.
2. Convert this document into a proper impl plan (rename to
   `28-13-openbao-kms-backend-impl-plan.md`) using the Epic 19
   exemplar shape.
3. Update `wave-2-impl-plan-inventory.md` to change the row action
   from `blocker` → `yes (new)`.
