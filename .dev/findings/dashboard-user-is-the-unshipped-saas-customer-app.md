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
(`ci.yml:49-50`) — ~~and per the Epic 44 survey, even those tests do not run, because
`vitest.config.ts:62` excludes them and no workflow supplies the filter.~~

> **CORRECTION — 2026-07-27 (Epic 45 audit).** The struck sentence above is **wrong, and it is
> backwards.**
> - `ci.yml:49-50` **is** the filter line (`pnpm --filter @tamma/dashboard-user test`), and it has
>   been there since the app landed. The tests run and pass: **20 files, 103 tests, all green** —
>   verified by running them.
> - The exclusion at `vitest.config.ts:64` is **deliberate and correct**: the package has its own
>   jsdom + jest-dom config, exactly as `packages/dashboard` does at `:60`.
> - The package whose tests are excluded **and** never run is **`packages/dashboard`** — the
>   *deployed admin console*, ~449 tests, excluded at `vitest.config.ts:60` with no filter line
>   anywhere in CI. That is Story 44-6's finding, and this app inherited the blame for it.
>
> What *is* true and was missed: `packages/dashboard-user` **fails `tsc`** (one error,
> `TenantAlertFeed.tsx:63`), and nothing would catch it — the root `typecheck` script
> (`package.json:24`) covers five packages and neither dashboard, no workflow typechecks either, and
> `vite build` does not typecheck. Story 45-0 fixes the error and adds the CI step.
>
> See `docs/stories/epic-45/README.md` for the full audit, including five further gaps this finding
> did not reach — chiefly that the API emails **six** URLs into this app and it implements **none** of
> them.

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

> **ANSWERED — 2026-07-27.** Scoped as **Epic 45: Ship the customer application**
> (`docs/stories/epic-45/`) — 8 stories, 16 person-days, an 8-day critical path.
>
> The audit that produced it also changes this finding's framing: **"None of it is hard" is true of
> the deployment half and not of the whole.** Roughly 60% of the epic is the infrastructure listed
> above and it is exactly as mechanical as this finding says. The other 40% is that the customer
> signup journey has **six front doors and none of them opens** — `/verify` (the app has
> `/verify-email`), `/reset-password`, `/invites/accept`, `/invites/pending`, `/onboarding/success`
> and `/onboarding/error`, all emitted by the API, none routed, with no catch-all so every one renders
> a blank pane. A deployment-only epic would put a working billing page behind a registration flow
> whose verification email 404s.

If the answer is "not yet", then 39-19 and 44-6 must be re-targeted at the admin console *with that
stated*, rather than inheriting a dependency nobody has scheduled.

## Related

- `packages/dashboard-user/` · `packages/dashboard/` · `docker/nginx-proxy.conf.template`
- `docs/stories/epic-44/README.md` (open question 1 — corrected by this finding)
- Story 39-19 (orchestrator chat), Story 44-6 (tracker UI), Epic 34-9 (pricing)
