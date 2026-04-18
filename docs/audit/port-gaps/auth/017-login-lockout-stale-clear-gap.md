# Finding 017: Login lockout stale-lockout not cleared on RecordFailedAttempt

**Scope**: auth
**Severity**: P3 (edge case; low-impact)
**Status**: Behavioral drift
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/login-lockout.ts`.

- File: `packages/api/src/auth/login-lockout.ts:70-92`.
- Contract: When `recordFailedAttempt` is called, if a prior lockout has expired, it is cleared before evaluating the new attempt. So an account that was locked 40 minutes ago (lockout expired 10 min ago) gets a clean slate: the new failed attempt is attempt #1 in a fresh window, not an `attempts.length + 1` in the old list.
- Key code:

```typescript
// packages/api/src/auth/login-lockout.ts:63-92 (9e9a57c~1)
recordFailedAttempt(email: string): boolean {
  const normalized = email.toLowerCase().trim();
  const now = Date.now();

  let record = this.attempts.get(normalized);
  if (!record) {
    record = { timestamps: [], lockedUntil: null };
    this.attempts.set(normalized, record);
  }

  // If currently locked, don't add more attempts
  if (record.lockedUntil !== null && now < record.lockedUntil) {
    return true;
  }

  // Clear expired lockout
  if (record.lockedUntil !== null && now >= record.lockedUntil) {
    record.lockedUntil = null;
    record.timestamps = [];
  }

  // Add current attempt and prune old ones outside the window
  record.timestamps.push(now);
  const windowStart = now - this.config.windowMs;
  record.timestamps = record.timestamps.filter((t) => t >= windowStart);

  if (record.timestamps.length >= this.config.maxAttempts) {
    record.lockedUntil = now + this.config.lockoutMs;
    return true;
  }
  return false;
}
```

- The two distinct branches at lines 77 and 82 give clean semantics: *locked now* → no-op and return true; *was locked, expired* → reset and treat as fresh.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/LoginLockoutService.cs:21-42`.
- Contract: Appends the new attempt, prunes attempts older than the window, then checks count. Does NOT clear an expired prior lockout before counting. The `IsLocked` method clears expired lockouts — but `RecordFailedAttempt` runs in a separate code path.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/LoginLockoutService.cs:21-42
public bool RecordFailedAttempt(string email)
{
    var key = email.ToLowerInvariant();
    var entry = _entries.GetOrAdd(key, _ => new LockoutEntry());

    lock (entry)
    {
        // Clean old attempts
        var cutoff = DateTime.UtcNow.AddMinutes(-WindowMinutes);
        entry.Attempts.RemoveAll(a => a < cutoff);

        entry.Attempts.Add(DateTime.UtcNow);

        if (entry.Attempts.Count >= MaxAttempts)
        {
            entry.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
            return true;
        }
    }
    return false;
}
```

- No check on `entry.LockedUntil`. If a previous lockout-cycle left `LockedUntil = 2026-04-17T10:00:00` and it is now `10:30:00` (30 min after expiry), the lockout is unresolved in `RecordFailedAttempt` — its clearing only happens if `IsLocked` was called in between.

## 3. The gap

Interaction sequence exposing the bug:

1. User fails 5 attempts in 15 min → `LockedUntil` set to now+30min.
2. 31 minutes pass — lockout has expired in absolute time.
3. User attempts login again. The Login endpoint calls `IsLocked(email)` — returns false AND clears the state (lines 55-59 of LoginLockoutService.cs). So far so good.
4. Now user submits a wrong password. Handler calls `RecordFailedAttempt(email)`. At this moment `entry.LockedUntil` is already null (cleared in step 3). So this specific path works.

**But the bug surfaces when the `IsLocked` call is skipped**:

1. User fails 5 attempts → locked for 30 min.
2. 31 min pass.
3. **Handler enters with a wrong password (via attacker bypass of the `IsLocked` check at the top of Login, or via a race where a concurrent caller clears state while another is recording)**.
4. `RecordFailedAttempt` runs: prunes `Attempts` older than 15 min → list becomes `[lastNew]`. Adds current attempt → `[lastNew, thisNew]`.
5. If between `IsLocked` and `RecordFailedAttempt` the `Attempts` list has 5 entries from the *previous* lockout cycle that are still within 15 minutes (specifically: if the 5 failures happened 14:45, 14:46, 14:47, 14:48, 14:49, lockout was set at 14:49+30min=15:19, and a new attempt arrives at 15:20 — the 5 original timestamps are still within the 15-minute window from 15:20 (cutoff at 15:05) when checking 14:45? no, 14:45 < 15:05 → pruned. 14:49 > 15:05 → kept. So only 1 of 5 remains.
6. In practice this off-by-one edge case: if the 5 failed timestamps span a compressed window (all 5 within the last minute), and the new attempt arrives ~minutes later but still within 15 min of ALL 5 → all 5 stay → the 6th attempt immediately relocks.

More concrete scenario: user fails at `14:50, 14:51, 14:52, 14:53, 14:54` → locked at 14:54+30=15:24 → but crucially the 5 timestamps are within ±5 seconds. At `15:25` (one minute past expiry), the new attempt arrives. Cutoff is `15:10`. All 5 original timestamps (`14:50..14:54`) are **older than 15:10** → pruned. So `Attempts.Count` is 1. Fine.

At `15:10` (exactly 16 minutes after the first, 31 minutes after lock set — **wait, lock set at 14:54, 15:10 is just 16 min after, < 30min, still locked**). OK so in the narrow band (30min to 45min after lock-set, which is 15min to 30min after lock-expired), the old timestamps are partially pruned. Depending on the original spacing, 1-4 old timestamps may remain.

If **2 old timestamps remain + this new one = 3**, not over threshold. So actual bug surface is small: requires a very specific previous failure pattern where 4+ failures happened in a 1-minute spike within the last 15min window, and the new attempt arrives just after the lockout expires but still within that 15-minute window.

Still, the TS version's "clear on expiry" line made this impossible by construction.

Error paths:
- TS: After lockout expiry, user has fresh counter.
- C#: After lockout expiry, user may carry residual un-pruned timestamps that could re-trip the lock on the very next failure.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Subtask 2.2 (line 39): *"Track failed attempts per email in-memory ... 5 failures in 15 min = 30 min lockout"*.
- Subtask 2.4 (line 41): *"Write unit tests for lockout timing, reset on success, concurrent attempts"*.
- No explicit requirement about stale-lockout clearing. TS handled it defensively; story did not specify.
- Story alignment:
  - [x] Matches TS behavior (TS is defensively stricter)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story (for this specific sub-behavior)

## 5. Status

- **Classification**: Behavioral drift (edge case not explicitly spec'd).
- **What's needed to finish**:
  1. At the top of `RecordFailedAttempt` (after the `lock (entry)` block opens), add:
     ```csharp
     if (entry.LockedUntil.HasValue && entry.LockedUntil.Value <= DateTime.UtcNow)
     {
         entry.LockedUntil = null;
         entry.Attempts.Clear();
     }
     if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > DateTime.UtcNow)
         return true;  // still locked
     ```
  2. This mirrors the TS semantic: lock-expired → fresh counter.
- **Is it "just a stub" or is scope missing?** Scope was not spec'd; TS implementation was defensive. Classify as drift relative to the TS reference.
- **Blockers**: None.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/LoginLockoutService.cs`.
- Files to create: None.
- Tests to add:
  - `LoginLockoutService_AfterLockoutExpires_NextFailedAttempt_IsCountedInFreshWindow`.
  - `LoginLockoutService_AfterLockoutExpires_FirstAttemptDoesNotImmediatelyRelock`.
- Estimated effort: 0.5h

## References

- TS source: `packages/api/src/auth/login-lockout.ts:63-92` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/LoginLockoutService.cs:21-42`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (subtask 2.2, 2.4)
