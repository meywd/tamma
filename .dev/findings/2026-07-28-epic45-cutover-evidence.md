# Epic 45 / hostname cutover — operator evidence ledger

**Date**: 2026-07-28
**Type**: 📋 Ops record (the durable evidence 45-5 AC5/AC6 and 45-6 AC10/AC11 call for)
**Status**: 🟡 Partially closed — pre-deploy items evidenced; deploy-time items open

The conformance review found the operator/production ACs of 45-5/45-6 had no durable record.
This ledger is that record. Update it as each item closes.

## Evidenced (pre-deploy)

| Item | Evidence | Date |
|---|---|---|
| DNS record for `dash.tamma.dev` | Added by the owner; confirmed resolving — a live TLS probe from the dev sandbox negotiated successfully | 2026-07-28 |
| DNS record for `admin.tamma.dev` | Added by the owner in the same batch (the hostname re-layout depends on it) | 2026-07-28 |
| Edge TLS for `dash.tamma.dev` | `openssl s_client -servername dash.tamma.dev` → served certificate carries SAN `DNS:dash.tamma.dev` | 2026-07-28 |

## Open (deploy-time — close at the first qa-tagged deploy)

1. **Origin certificate SANs** (45-5 AC5): the cert file on the VPS (`secrets/origin-cert.pem`) must
   cover `dash.tamma.dev` AND `admin.tamma.dev` (matters if Cloudflare↔origin mode is Full-strict;
   a `*.tamma.dev` wildcard closes it trivially). Record the SAN list here.
2. **GitHub OAuth App callback** (rehost prerequisite): change to
   `https://admin.tamma.dev/oauth2/callback` on github.com BEFORE the deploy — owner action;
   every admin sign-in fails with a redirect_uri mismatch until done. Record when flipped.
3. **First production deploy** (45-6 AC10): qa-tag → pipeline → `post-deploy-tests.sh` output
   (the strict dash/app 200-not-302 + SPA + /api-prefix probes). Paste the run URL here.
4. **Rollback check** (45-6 AC11): re-deploy the previous image tag once, confirm health; or
   record the decision to accept forward-only with the reason.
5. **Real-email walk** (45-3 step 12 / 45-7 AC8): register → verify → reset → invite on the
   deployed app with a real mailbox. Record date + who walked it.
6. **45-0 AC4 red-CI proof**: one scratch-branch push reintroducing the TS2379 error to show the
   typecheck step actually fails red. Needs owner permission for a scratch branch push (the
   session's branch rules forbid pushing other branches unasked) — or accept as unproven here.

## Related

- `.dev/bugs/2026-07-28-epic45-review-residuals.md` (review-round fixes)
- `.dev/decisions/2026-07-28-three-host-layout.md` (target host layout)
- `docker/post-deploy-tests.sh` (the probes that verify items 3's assertions)
