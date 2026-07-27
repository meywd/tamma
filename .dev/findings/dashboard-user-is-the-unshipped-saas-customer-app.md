# Finding: `packages/dashboard-user` is the SaaS customer app, and it has never been deployed

**Date**: 2026-07-25
**Type**: 🚨 Known Issue
**Category**: Product / Infrastructure
**Status**: 🔍 Open — needs a product decision, and it is bigger than a docs question

## What it actually is

Not an abandoned experiment. **It is the customer-facing half of the SaaS product**, built in three
commits on 2–4 July 2026 under Epic 34-9 (pricing & plan management):

```
a4f02d4  2026-07-02  (added)
676995a  2026-07-04  feat(epic-34-9): pricing & plan management dashboard UI
0de428e  2026-07-04  fix(epic-34-9): gate change-plan to owner/sole-user …
```

47 source files with tests. Its routes are a complete signup-to-billing journey:

| Route | What it is |
|---|---|
| `/login`, `/register`, `/verify-email` | customer authentication |
| `/onboarding/platforms` | connecting a git platform |
| `/` | dashboard home |
| `/alerts`, `/settings/alerts` | tenant alert feed + config |
| `/settings/billing` | plan pricing, upgrade modal, entitlement bar, cost estimate |

## The problem

**It has no way to reach a customer.** No Dockerfile, no compose service, no GHCR image, no deploy
step, no nginx vhost, no domain. Its only appearance outside its own directory is a CI test line
(`ci.yml:49-50`) — and per the Epic 44 survey, even those tests do not run, because
`vitest.config.ts:62` excludes them and no workflow supplies the filter.

So someone built the billing UI, wrote tests for it, and stopped immediately before shipping.

## Why this matters beyond one app

1. **The two apps are admin vs customer, not real vs dead.** `packages/dashboard` is the admin
   console and is deployed. Framing `dashboard-user` as "a second app we might delete" is wrong and
   was the framing in the Epic 44 open questions — corrected there.
2. **Epic 39-19's orchestrator chat targets it.** That story is blocked on infrastructure nobody has
   scheduled.
3. **Epic 44's tracker UI (44-6) has the same question.** A customer-facing board belongs where
   customers already are. If `dashboard-user` never ships, either 44-6 goes in the admin console —
   where customers do not go — or it is blocked on the same unscheduled work.
4. **Epic 34-9's own deliverable is unreachable.** Plan management and upgrade exist as code that no
   customer can open. If billing is live, customers are changing plans some other way; if it is not,
   this is a shipped-but-dark feature.

## What is missing, concretely

Mirroring what `packages/dashboard` already has: a `docker/Dockerfile.dashboard-user`, a compose
service, an image build + push in the deploy workflow, an nginx vhost/route in
`docker/nginx-proxy.conf.template`, and a hostname. Plus turning on the excluded tests.

None of it is hard. All of it is unowned.

## The decision needed

Not "should we keep this app" — it is the product. The decision is **who funds shipping it, and
when**, because at least three planned things (39-19, 44-6, and 34-9's own value) are silently
waiting on it.

If the answer is "not yet", then 39-19 and 44-6 must be re-targeted at the admin console *with that
stated*, rather than inheriting a dependency nobody has scheduled.

## Related

- `packages/dashboard-user/` · `packages/dashboard/` · `docker/nginx-proxy.conf.template`
- `docs/stories/epic-44/README.md` (open question 1 — corrected by this finding)
- Story 39-19 (orchestrator chat), Story 44-6 (tracker UI), Epic 34-9 (pricing)
