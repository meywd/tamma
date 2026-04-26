# KEK Rotation Runbook

**Story**: 28-12
**Audience**: platform operators
**Severity**: critical change — every per-tenant connection string is re-encrypted; a misstep risks lockout from every tenant DB
**Estimated downtime**: zero (rotation is online; both KEKs valid during the overlap window)
**Last reviewed**: 2026-04-26

---

## What this rotates

Tamma encrypts every per-tenant database connection string at rest with AES-256-GCM. The Key Encryption Key (KEK) is loaded from environment variables at process startup. This runbook walks through replacing the active KEK with a new one and re-encrypting every `tenants.EncryptedConnectionString` row to use the new key.

**Triggers for rotation**:
- Quarterly cadence (compliance baseline — pick one calendar day per quarter and stick to it)
- KEK material is suspected leaked or improperly handled
- Personnel change where someone with KEK access has left
- Pre-audit hygiene (SOC 2 Type II evidence collection)

**This runbook does NOT cover**:
- Per-tenant database password rotation — see `rotate-tamma-app-password.sh`
- Application-key rotation (sk_live_, ghp_, etc.) — those live in the secrets cabinet (Story 29-2)
- KMS migration (`OpenBao` etc.) — see Story 28-13 (deferred)

---

## Architecture refresher (read before first rotation)

The KEK is loaded via two configuration keys on every Tamma process. The
ASP.NET Core configuration system maps each `:`-separated key to either
an env var with `__` separators (e.g. `Cranl__EncryptionKey`) or to an
appsettings.json entry. Pick whichever your deployment substrate
prefers.

| Configuration key | Env-var form | Slot | Purpose |
|---|---|---|---|
| `Cranl:EncryptionKey` | `Cranl__EncryptionKey` | primary | Encrypts new + re-encrypts existing rows. Decrypts rows tagged with the matching `KekVersion`. |
| `Tamma:Kek:Secondary` | `Tamma__Kek__Secondary` | secondary | Fallback decrypt path. Lets the rotation overlap window decrypt rows tagged with the previous version. |
| `Tamma:Kek:ActiveVersion` | `Tamma__Kek__ActiveVersion` | meta | Operator-managed integer; the rotation worker bumps this after promotion. Default 1. |
| `Tamma:Kek:RetainedHistorySize` | `Tamma__Kek__RetainedHistorySize` | meta | How many retired KEKs the cabinet keeps in memory. Default 2. R2-H13. |

Each `tenants.EncryptedConnectionString` row carries an integer
`KekVersion` that names which version encrypted it. R2-H13: when the
caller passes a `kekVersion` to the decryptor, the cabinet looks up
THAT exact slot (primary / secondary / retired-history); only legacy
rows with `kekVersion=null` use the primary-then-secondary fallback
heuristic.

**The encrypted envelope shape** (`AesGcmConnectionStringDecryptor`):

```
[1 byte version=0x01]
[1 byte kek_slot (0=primary, 1=secondary)]
[12 bytes nonce]
[ciphertext bytes]
[16 bytes GCM auth tag]
```

The byte format is forward-compatible — bumping the version byte is the migration path for an algorithm change (e.g. AES-GCM-SIV).

---

## Pre-rotation checklist

- [ ] **All Tamma API pods are healthy and on the same release.** A rolling deploy mid-rotation can leave one pod with the new KEK env and another with only the old — the dual-slot path handles this, but it's safer to start clean.
- [ ] **`platform_events` is being consumed.** The rotation writes `KEK.ROTATION.STARTED` + `KEK.ROTATION.COMPLETED` events; if the consumer is down you'll lose the audit trail.
- [ ] **Backup the `tenants` table.** A bug in the rotation flow (or a partial rotation that you decide to roll back) is much easier to recover from with a known-good snapshot. The encrypted column changes; everything else stays identical.
- [ ] **Schedule a 30-minute window.** Rotation itself is fast (seconds per tenant) but the operator should babysit it.
- [ ] **Have the new KEK material ready.** 32 random bytes, base64-encoded:
      `openssl rand -base64 32` (use a hardware RNG if available).

---

## Rotation steps

### Step 1 — Stage the new KEK as the secondary slot

Rolling deploy / config update across every Tamma pod (env-var form
shown; appsettings.json equivalents work too):

```
Cranl__EncryptionKey=<old-kek-base64>          # unchanged
Tamma__Kek__Secondary=<new-kek-base64>         # NEW
```

Verify each pod loads both keys at startup. The log line is:

```
KEK provider loaded primaryVersion=N secondaryVersion=N+1
```

If only `primaryVersion` shows, the pod didn't pick up the new env — fix before continuing.

### Step 2 — Promote the new KEK to primary, demote the old one

Same env update across every Tamma pod:

```
Cranl__EncryptionKey=<new-kek-base64>          # was secondary; now primary
Tamma__Kek__Secondary=<old-kek-base64>         # was primary; now secondary
```

After this rolling deploy, every NEW write is encrypted with the new KEK. Existing rows are still readable (they carry the old `KekVersion`, decryptor uses the secondary slot via the version-explicit path R2-H13 added).

### Step 3 — Trigger the re-encrypt loop

R2-H3: the live route is `/api/admin/kek/rotate/start`, NOT
`/api/admin/secrets/rekey/*`. The runbook used to reference the
secrets-rekey path that was never wired.

```bash
curl -X POST https://api.tamma.dev/api/admin/kek/rotate/start \
  -H "Authorization: Bearer $OWNER_JWT"
```

Returns 202 with the rotation snapshot. The coordinator:

1. Acquires the cluster-wide `pg_try_advisory_lock(KekRotationCoordinator.AdvisoryLockKey)` so two pods cannot stage different KEKs (R2-H14).
2. Persists the staged secondary KEK into the new `kek_rotations` table — encrypted by the OLD primary so a process crash mid-rotation can resume by reloading the row.
3. Emits `SECRETS.KEK.ROTATION.STARTED` to `platform_events`.
4. Iterates every `tenants` row, decrypts with the OLD primary, re-encrypts with the NEW key, updates the row + `KekVersion`, evicts the resolver pool cache for the tenant.
5. Emits `TENANT.CONNECTION_STRING_ROTATED.SUCCESS` per tenant.
6. Emits `SECRETS.KEK.ROTATION.COMPLETED` with the row counts on the terminal step (or `SECRETS.KEK.ROTATION.FAILED` if any row failed).

Watch progress via:
- `GET /api/admin/kek/rotate/status` for the coordinator state
- Tailing `SECRETS.KEK.*` + `TENANT.CONNECTION_STRING_ROTATED.*` events on the SSE stream

A typical 100-tenant rotation completes in under a minute.

### Step 4 — Verify every row is on the new KEK version

```sql
SELECT KekVersion, COUNT(*)
FROM tenants
WHERE DeletedAt IS NULL
GROUP BY KekVersion;
```

**All rows** should report the new version. If any row is still on the old version:
- Check `KEK.ROTATION.STEP_FAILED` events for that tenant
- Most common cause: row's `KekVersion` mismatch with the envelope's slot byte → decrypt failed, the coordinator skipped the row
- Re-run the coordinator after fixing — it's idempotent and skips already-rotated rows

### Step 5 — Drop the old KEK from the secondary slot

After every row has been re-encrypted (Step 4 confirms), the old KEK is no longer needed:

```
Cranl__EncryptionKey=<new-kek-base64>     # unchanged
# Tamma__Kek__Secondary removed entirely
```

Roll out across every pod. Verify the startup log now shows only `primaryVersion=N+1` (no secondary).

R2-H13 note: even after dropping the secondary env var, the cabinet
keeps the previous primary in its in-memory retired ring (default
`Tamma:Kek:RetainedHistorySize=2`) so any tenant row still tagged with
the older `KekVersion` remains decryptable. The `kek-cabinet` health
check refuses to mark "ready" if a tenant row is older than the ring
size — operators see this fail before traffic hits an undecryptable
row.

### Step 6 — Securely destroy the old KEK material

The old KEK has zero value once the secondary slot is gone. Destroy the off-disk copies:

- Remove from secrets manager / vault entry where it was stored
- Wipe operator clipboards / local copies
- Note the destruction timestamp + operator name in your ops log
- Update the rotation tracker (next-rotation-due date)

---

## Failure recovery

### "Some tenants still on old KekVersion after Step 3"

1. Check `SECRETS.KEK.ROTATION.FAILED` + per-row warning logs for the affected tenant ids
2. If the failure was a transient DB error: re-run the coordinator via `POST /api/admin/kek/rotate/retry` (R2-H3). The retry endpoint re-uses the staged secondary KEK that was persisted in `kek_rotations` rather than minting a fresh one — this keeps idempotency: rows already re-encrypted under the failed run's secondary stay valid.
3. If the failure was a decrypt failure: the row's envelope is corrupted or its `KekVersion` is wrong — investigate the row directly; do NOT proceed to Step 5 until resolved

### "API pods can't decrypt any tenant after Step 5"

Almost certainly Step 4's check was wrong — some row was still on the old KEK and the cabinet's retired-ring size doesn't cover it. Recovery:

1. Re-add `Tamma__Kek__Secondary=<old-kek-base64>` to the env on every pod
2. Roll restart
3. Re-run the coordinator: `POST /api/admin/kek/rotate/retry` (R2-H3; idempotent re-attempt of the failed run)
4. Repeat Step 4 verification

### "Rotation coordinator is stuck partway"

The coordinator records progress in `platform_events` AND in the new `kek_rotations` CP table (R2-H14). To inspect:

```sql
SELECT id, status, version_old, version_new, started_at, completed_at, failure_reason
FROM kek_rotations
ORDER BY started_at DESC
LIMIT 10;
```

If the latest row is `running` and there's no API pod actually running, the previous coordinator crashed mid-rotation. Two recovery paths:

1. **Resume**: restart any API pod. On startup the coordinator scans for non-terminal `kek_rotations` rows and resumes the in-flight rotation by re-loading the staged secondary from the row's `staged_secondary_protected` blob (encrypted by the OLD primary, so it's readable across restarts). The advisory lock is connection-scoped — a crashed pod's lock is released by Postgres automatically.
2. **Manual retry**: if the row is `failed`, run `POST /api/admin/kek/rotate/retry`. It re-uses the staged secondary in the row.

```sql
SELECT type, tags, data, created_at
FROM platform_events
WHERE type LIKE 'SECRETS.KEK.%' OR type LIKE 'TENANT.CONNECTION_STRING_ROTATED.%'
ORDER BY sequence_number DESC
LIMIT 50;
```

---

## Rollback

R2-H14 + R2-H3: if a rotation fails partway through, the operator has two
recovery levers depending on the failure shape.

### Path A — Re-attempt via /retry (preferred)

This is the right call when the failure was transient (DB blip, network
hiccup) and the staged secondary in `kek_rotations.staged_secondary_protected`
is still valid.

```bash
curl -X POST https://api.tamma.dev/api/admin/kek/rotate/retry \
  -H "Authorization: Bearer $OWNER_JWT"
```

Returns 202 when the retry kicks off (re-using the persisted secondary)
or 409 with a `reason` field when the current phase is not `failed`
(e.g. another rotation is currently running, or the previous one
completed cleanly). The retry never mints a fresh KEK — that would
orphan rows already re-encrypted under the failed run's secondary.

Watch the same `GET /api/admin/kek/rotate/status` endpoint as in
step 3.

### Path B — Drop the staged secondary and start fresh

This is the right call when the staged secondary itself is bad (e.g.
the operator suspects the secondary KEK material was tampered with, or
the encrypted blob in `kek_rotations` is corrupt).

The schema is intentionally minimal so an operator can drop the row
manually:

```sql
-- Identify the failed row.
SELECT id, status, version_old, version_new, failure_reason, started_at
FROM kek_rotations
WHERE status = 'failed'
ORDER BY started_at DESC
LIMIT 1;

-- Mark it cancelled and zero the staged secondary blob. Do NOT DELETE
-- the row — the audit trail must survive for SOC2 evidence.
UPDATE kek_rotations
SET status = 'cancelled',
    staged_secondary_protected = NULL,
    failure_reason = COALESCE(failure_reason, 'manually cancelled') || ' (operator dropped staged secondary)',
    completed_at = now()
WHERE id = '<failed-id>';
```

After the row is `cancelled`, kick off a fresh rotation via `POST
/api/admin/kek/rotate/start` — the coordinator generates a new
secondary KEK and runs the loop from scratch.

Operator note: tenant rows that were already re-encrypted under the
failed run's secondary now hold envelopes that no live KEK can decrypt
(both secondary slots have been retired). Recover them from the most
recent `tenants` table backup (Pre-rotation checklist step 3).

---

## Compensating controls

- **Audit trail**: every rotation lands `KEK.ROTATION.STARTED` / `STEP_*` / `COMPLETED` events tagged with the operator's user id. Retention: indefinite (the events table is the audit log of record).
- **Encryption-at-rest**: the KEK never touches disk on the API host. Env vars live in the orchestrator's secret store.
- **Slot isolation**: primary and secondary KEKs are kept in separate env vars so a leak of one doesn't leak the other.
- **Forward compatibility**: the envelope's version byte (currently `0x01`) is reserved for an algorithm change. Bumping the version is a flag-day rotation but the envelope shape is ready.

---

## Open questions / future enhancements

- **Per-tenant KEK** — Doc 01 §8.2 sketches a future where each tenant has its own KEK, derived from the cluster KEK via HKDF + the tenant id. Strong tenant-isolation property; doubles the implementation cost. Triggered by SOC 2 evidence requirements or a per-tenant compromise.
- **Hardware-backed KEK** — moving the cluster KEK into HashiCorp Vault Transit / AWS KMS / OpenBao. Tracked under Story 28-13 (deferred). The `IConnectionStringDecryptor` interface is shaped to swap in a KMS-backed implementation drop-in.
- **Auto-rotation scheduler** — currently manual quarterly. A cron-fired scheduler that runs Step 3 automatically would close the "operator forgot to rotate" gap. Punt until the manual cadence proves boring.
