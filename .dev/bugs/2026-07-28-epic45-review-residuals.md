# Epic 45 ship-lane review residuals (2026-07-28)

**Status**: 📋 Record — fixed items listed for the audit trail; unfixable item documented.

The adversarial review of the Epic 45 lane (commits `f2b6a9a` + `3a4e460`) produced seven
findings. Fixed in the review-fix commit:

1. **Compose default defeated the CustomerUrl→Url fallback** (`docker-compose.yml` `:-https://dash.tamma.dev`
   → `:-`): a self-hosted install setting only `DASHBOARD_URL` would have emailed its users'
   one-time verify/reset/invite tokens to Tamma's production host. `.env.example` now ships the
   value commented out with the warning.
2. **CORS**: `AllowCredentials()` added (customer app auth is the `tamma_session` cookie with
   `credentials: 'include'`, not an Authorization header — the old comment claimed otherwise);
   origins are normalized to scheme://host[:port] with a startup warning on path-bearing config.
   *(Lands in the provider-backend review-fix commit, which owns Program.cs.)*
3. **Both in-container nginx confs stripped the /api prefix** (`proxy_pass …:3100/` → `…:3100/api/`):
   every container-direct API call except `/api/health` 404'd — masked because `/api/health`'s
   stripped form lands on the accidental twin `/health`. The customer conf inherited the admin
   conf's defect; both fixed.
4. **Probe hardening** (`post-deploy-tests.sh`): the SPA-fallback and /api probes' regression
   symptom is exactly a 404, which the lenient helper converts to WARN — they could never fail
   the deploy. New `test_endpoint_strict` (404 = FAIL) used for all three dash probes, and the
   /api probe now targets `/api/v1/auth/me` (an /api-ONLY route, expected 401): a stripped
   prefix now produces a hard failure instead of a coincidental pass via `/health`.

## Not fixable in-place (recorded)

**Commit-attribution drift**: the three dash.tamma.dev probe additions in `post-deploy-tests.sh`
were committed in `0422397` ("feat(tracking): 44-0 …", an unrelated lane) rather than `f2b6a9a`
whose message claims them — two parallel implementation lanes wrote in the same window and the
44-0 lane's `git add` caught the file. History is immutable on this branch (no rewrites of
pushed commits); this note is the correction. Consequence to be aware of: reverting `0422397`
would silently remove the probes; reverting `f2b6a9a` would not.

## Related

- `docker/docker-compose.yml`, `docker/.env.example`, `docker/nginx-dashboard-user.conf`,
  `docker/nginx-dashboard.conf`, `docker/post-deploy-tests.sh`
- The review itself: PR #506 review round, 2026-07-28.
