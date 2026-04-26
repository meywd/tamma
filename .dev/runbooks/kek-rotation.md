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

The KEK is loaded via two environment variables on every Tamma process:

| Variable | Slot | Purpose |
|---|---|---|
| `TAMMA_TENANT_KEK` | primary | Encrypts new + re-encrypts existing rows. Decrypts rows tagged with the matching `KekVersion`. |
| `TAMMA_TENANT_KEK_SECONDARY` | secondary | Fallback decrypt path. Lets the rotation overlap window decrypt rows tagged with the previous version. |

Each `tenants.EncryptedConnectionString` row carries a one-byte `KekVersion` that names which slot encrypted it. The decryptor tries the slot named by the version first; on auth-tag mismatch it tries the other slot. Both slots present → both versions decryptable.

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

Rolling deploy / config update across every Tamma pod:

```
TAMMA_TENANT_KEK=<old-kek-base64>          # unchanged
TAMMA_TENANT_KEK_SECONDARY=<new-kek-base64>  # NEW
```

Verify each pod loads both keys at startup. The log line is:

```
KEK provider loaded primaryVersion=N secondaryVersion=N+1
```

If only `primaryVersion` shows, the pod didn't pick up the new env — fix before continuing.

### Step 2 — Promote the new KEK to primary, demote the old one

Same env update across every Tamma pod:

```
TAMMA_TENANT_KEK=<new-kek-base64>           # was secondary; now primary
TAMMA_TENANT_KEK_SECONDARY=<old-kek-base64> # was primary; now secondary
```

After this rolling deploy, every NEW write is encrypted with the new KEK. Existing rows are still readable (they carry the old `KekVersion`, decryptor falls back to the secondary slot).

### Step 3 — Trigger the re-encrypt loop

```bash
curl -X POST https://api.tamma.dev/api/admin/secrets/rekey \
  -H "Authorization: Bearer $OWNER_JWT" \
  -H "X-Admin-Confirm: rekey" \
  -d '{"reason": "Q1 2026 quarterly rotation"}'
```

Returns a coordinator id. The coordinator:

1. Emits `KEK.ROTATION.STARTED` to `platform_events`
2. Iterates every `tenants` row, decrypts with whatever slot works, re-encrypts with the new primary, updates the row + `KekVersion`
3. Emits `KEK.ROTATION.STEP_COMPLETED` per tenant batch
4. Emits `KEK.ROTATION.COMPLETED` with a summary on the terminal step

Watch progress via:
- `GET /api/admin/secrets/rekey/status` for the coordinator state
- `tamma.kek_rotation.tenants_processed_total` metric
- Tailing `KEK.ROTATION.*` events on the SSE stream

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
TAMMA_TENANT_KEK=<new-kek-base64>       # unchanged
# TAMMA_TENANT_KEK_SECONDARY removed entirely
```

Roll out across every pod. Verify the startup log now shows only `primaryVersion=N+1` (no secondary).

### Step 6 — Securely destroy the old KEK material

The old KEK has zero value once the secondary slot is gone. Destroy the off-disk copies:

- Remove from secrets manager / vault entry where it was stored
- Wipe operator clipboards / local copies
- Note the destruction timestamp + operator name in your ops log
- Update the rotation tracker (next-rotation-due date)

---

## Failure recovery

### "Some tenants still on old KekVersion after Step 3"

1. Check `KEK.ROTATION.STEP_FAILED` events for the affected tenant ids
2. If the failure was a transient DB error: re-run the coordinator (`POST /api/admin/secrets/rekey/retry`)
3. If the failure was a decrypt failure: the row's envelope is corrupted or its `KekVersion` is wrong — investigate the row directly; do NOT proceed to Step 5 until resolved

### "API pods can't decrypt any tenant after Step 5"

Almost certainly Step 4's check was wrong — some row was still on the old KEK and the secondary slot was needed. Recovery:

1. Re-add `TAMMA_TENANT_KEK_SECONDARY=<old-kek>` to the env on every pod
2. Roll restart
3. Re-run the coordinator (it'll skip already-rotated rows + handle the laggards)
4. Repeat Step 4 verification

### "Rotation coordinator is stuck partway"

The coordinator records progress in `platform_events`. To inspect:

```sql
SELECT type, tags, data, created_at
FROM platform_events
WHERE type LIKE 'KEK.ROTATION.%'
ORDER BY sequence_number DESC
LIMIT 50;
```

If the last event is `STEP_STARTED` for a tenant (not followed by COMPLETED or FAILED), the coordinator likely crashed mid-tenant. Restart the API pod — the coordinator picks up where it left off via the next manual `POST /api/admin/secrets/rekey/retry`.

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
