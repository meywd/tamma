# Implementation Plan — Story 45-6: Build, Push, Deploy, Verify

## Scope & Deliverable

When this story is done, every merge builds `ghcr.io/<owner>/tamma-dashboard-user`, pushes it with
the same six tag rules the admin image uses, deploys it alongside layer 4, verifies it is not
restart-looping, includes it in the final health gate and the smoke-test list, and runs
`post-deploy-tests.sh`'s customer-host probes against it. Then a human opens
`https://dash.tamma.dev` and registers.

## Pre-Reading

- `.github/workflows/docker-publish.yml:139-206` — `build-dashboard`. **The template. Read all 67
  lines**, including both retry pairs.
- `.github/workflows/docker-publish.yml:447-467` — image-override heredoc #1
- `.github/workflows/docker-publish.yml:670-682` — the layer-4 start step
- `.github/workflows/docker-publish.yml:701-730` — the layer-4 verify step **and its comment**, which
  records the incident that motivated it
- `.github/workflows/docker-publish.yml:820-830` — the final health list (nine services)
- `.github/workflows/deploy.yml:1-6` — what this workflow is for versus `docker-publish.yml`
- `.github/workflows/deploy.yml:126-152` — image-override heredoc #2. **Note it already differs from
  #1**: it has `tamma-api-dotnet` (`:147`), it lacks `elsa-studio`.
- `.github/workflows/deploy.yml:243-252` and `:305-315` — start and health list
- `.github/workflows/docker-smoke-test.yml:50-60` — the service loop
- `docker/post-deploy-tests.sh:120-150` — the probe block 45-5 extended
- `docs/stories/epic-45/story-45-5/…` — what already exists by the time this starts
- **All referenced paths exist.** This story creates no file; it edits four.

## Design Decisions

- **D1 — Copy `build-dashboard` literally and change two strings.** The job is 67 lines of which ~40
  are two retry pairs (buildx setup at `:148-159`, build-push at `:180-206`). Those exist because both
  operations fail transiently in this repo. A "cleaner" job without them fails intermittently and the
  failures will be attributed to the new application rather than to the missing retry. Change
  `images:` and `file:`; change nothing else.

- **D2 — Add the entry to both image-override generators, and do not reconcile them.**
  `docker-publish.yml:447` and `deploy.yml:133` are independent heredocs and **have already drifted**
  — `deploy.yml` lists `tamma-api-dotnet`, `docker-publish.yml` lists `elsa-studio`. That drift is a
  real finding and reconciling it is a separate change touching six services' deploy behaviour.
  Folding it in makes this story's diff unreviewable and couples "ship the customer app" to "audit the
  deploy overrides". **Add the one entry to both; file the drift.**

- **D3 — The service goes in the layer-4 *verify* loop, not only the start list.** The verify step's
  own comment (`:701-709`) records that layer-4 services were started and never verified, so a
  restart-looping container survived a deploy and surfaced as a user-visible 500 after merge. Adding a
  new customer-facing service to the start list but not the verify loop reproduces precisely that,
  for the app with the most exposure.

- **D4 — Confirm how the build job gates the deploy rather than assuming.** If `build-dashboard`
  reaches the deploy job through a `needs:`, the new job needs the same edge. If it does not, the
  deploy races the push and pulls a stale or missing tag — intermittently, and only for tags that have
  not been built before, which makes it look like a registry problem. **Read the job graph before
  writing the job.**

- **D5 — AC10 is a human, and that is the point.** The pipeline can assert containers are healthy and
  URLs return 200. It cannot assert a customer can register, receive a verification email, click its
  link, and reach a billing page. One person doing that once, against production, is what turns "the
  app is deployed" into "the product is reachable" — and it is the only test that exercises the six
  entry points 45-2 and 45-3 built.

- **D6 — Verify the rollback rather than infer it.** The tag rules (`:172-178`) mean an earlier
  `image_tag` through `deploy.yml` should roll back. "Should" is doing work in that sentence: the new
  image must actually carry the same tag set for the same commit, and the manual path must actually
  pull it (which is D2's concern). Test it, or record that it was not tested. An assumed rollback is
  discovered false during an incident.

## Implementation Steps

1. **Map the job graph (D4).** Determine what `build-dashboard` feeds and how. Write it in the PR.
2. **Add `build-dashboard-user`** — `docker-publish.yml`, immediately after `:206`. Copy
   `build-dashboard`; change `images:` to `ghcr.io/${{ github.repository_owner }}/tamma-dashboard-user`
   and both `file:` occurrences to `docker/Dockerfile.dashboard-user`. Preserve both retry pairs, all
   `continue-on-error` and `if:` conditions, the sleeps, the six tag rules and the gha cache config.
3. **Wire the gate** per step 1.
4. **Extend heredoc #1** — `docker-publish.yml:447-467`. Two lines plus `build: !reset null`.
5. **Extend heredoc #2** — `deploy.yml:133-152`. Identical text. Note the pre-existing drift in the
   PR (D2); do not fix it.
6. **Add to the layer-4 start lists** — `docker-publish.yml:679` and `deploy.yml:250`.
7. **Add to the layer-4 verify loop** — `docker-publish.yml:718`'s `for svc in …` (D3). Check whether
   `deploy.yml` has an equivalent loop; if it does, add it there too.
8. **Add to the final health lists** — `docker-publish.yml:824` and `deploy.yml:310`.
9. **Add to `docker-smoke-test.yml:57`.**
10. **Confirm `post-deploy-tests.sh` runs** (AC9). Find its invocation. If the customer-host probes
    45-5 added are not reached by any workflow, wire the script in — and note that the *existing*
    probes were also not running, which is a finding in its own right.
11. **Dry run on a branch or PR.** Confirm the image builds and pushes with `pr-N` / `sha-` tags, and
    that the tag set matches `tamma-dashboard`'s for the same commit (D6).
12. **Deploy to production** (AC10). Watch the layer-4 verify step specifically — it is the one most
    likely to catch a first-deploy problem, and the one whose failure mode is a healthy-looking deploy
    if the service was omitted from it.
13. **Run the probes** — `post-deploy-tests.sh` in full, and confirm the three customer-host probes
    pass, particularly `/` → **200 not 302**.
14. **Human click-through** (D5): open `https://dash.tamma.dev`, register a fresh account, receive the
    verification email, click the link, log in, open `/settings/billing`. **Record each step's
    outcome, including anything that worked but looked wrong.**
15. **Test the rollback** (D6): `deploy.yml` with the previous `image_tag`; confirm the customer app
    reverts and comes back healthy. Then roll forward.

## Data & Migrations

None. The SPA is a static bundle behind nginx.

## Events

None.

## Test Plan

| # | Check | Asserts |
|---|---|---|
| 1 | Branch/PR run of `docker-publish.yml` | `build-dashboard-user` succeeds; image in GHCR |
| 2 | GHCR tag list | same six tag forms as `tamma-dashboard` for the same commit — D6 |
| 3 | Job graph | the new job gates the deploy the same way `build-dashboard` does — D4 |
| 4 | Deploy run | compose pulls the image; **does not build** — proves `build: !reset null` |
| 5 | Layer-4 verify step | includes `tamma-dashboard-user`; passes — D3 |
| 6 | Final health gate | includes it; passes |
| 7 | `docker-smoke-test.yml` | includes it; passes |
| 8 | `post-deploy-tests.sh` | runs, and the three customer-host probes pass |
| 9 | `https://dash.tamma.dev/` | **200, not 302** — the no-oauth2-proxy proof, now against production |
| 10 | `https://dash.tamma.dev/settings/billing` | 200 — SPA fallback in production |
| 11 | `https://dash.tamma.dev/api/health` | 200 |
| 12 | `https://app.tamma.dev/` | still 302 — the admin console is unaffected |
| 13 | **Human click-through** (step 14) | register → verify → login → billing, end to end |
| 14 | Rollback (step 15) | previous tag deploys and comes back healthy |

Check 13 is the acceptance criterion the epic exists for. Check 12 is the regression guard on a shared
deploy path.

## Definition of Done

- `build-dashboard-user` exists with **both retry pairs intact** (diff-checked against
  `build-dashboard`: only `images:` and `file:` differ).
- The entry is in **both** image-override generators; the pre-existing drift is noted in the PR and
  **not** fixed.
- The service is in: both start lists, the layer-4 verify loop, both final health lists,
  `docker-smoke-test.yml`.
- `post-deploy-tests.sh` demonstrably runs in the pipeline; its customer-host probes are green.
- A production deploy has happened and check 9 passes against the real host.
- The human click-through is recorded step by step in the PR.
- The rollback is tested, or explicitly recorded as untested.
- **No change to `.github/workflows/ci.yml`** (45-0 owns it) and none to `docker/Dockerfile.dashboard-user`
  or the compose files (45-4/45-5 own them) — grep-checked.

## Dependencies & Sequencing

- **Blocked by:** 45-4 (Dockerfile path), 45-5 (compose service, probes).
- **Blocks:** nothing in Epic 45. **Unblocks Story 39-19, Story 44-6 and Epic 34-9's delivered value.**
- **Sequencing preference, not a block:** land 45-1, 45-2, 45-3 and 45-7 before step 12's production
  deploy. Deploying with the six entry points still dead (45-2/45-3), the billing warning still silent
  (45-1) or verification emails still pointing at the admin console (45-7) ships a known-defective
  customer surface — and once it is live, "it looks shipped" starts costing real customers rather than
  hypothetical ones. Steps 1–11 can and should proceed in parallel with those stories; only step 12
  waits.
- **Shared-edit register:** `deploy.yml` and `docker-publish.yml` are this story's alone.
  `docker/post-deploy-tests.sh` is shared with **45-5** — 45-5 adds the probes, this story ensures
  they run. Sequence 45-5 fully first.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The build job does not gate the deploy**, so the deploy races the push and pulls a stale tag — intermittently, and only for new tags. | D4 / step 1 makes reading the job graph the first action, before the job is written, with the finding in the PR. |
| **Only one image-override generator is updated**, so the automated deploy pulls and the manual re-deploy rebuilds from source — two artefacts from one tag. | AC3+AC4 name both; the DoD requires both; the story file states the generators have already drifted once, so the failure mode is demonstrated rather than hypothetical. |
| **The service is started but not verified**, so a restart-looping customer app survives the deploy — exactly the incident `docker-publish.yml:701-709` documents. | D3, AC6 and check 5. |
| **The retry pairs are dropped as boilerplate**, and the job fails transiently forever. | D1 and the DoD's diff-check against `build-dashboard`. |
| **The first production deploy needs iteration** and the story is treated as done at merge. | AC10 and step 12 are in the story, not "follow-up". Two days is sized for a first deploy of a new service, not for a green YAML diff. |
| **The rollback is assumed rather than tested** and is discovered false during an incident. | D6 / step 15 — test it, or record that it was not tested. Recording an untested rollback is an acceptable outcome; assuming a tested one is not. |
| **The app goes live with dead entry points**, so customers meet a signup flow whose verification link 404s. | The sequencing preference above, stated in both the story and the execution plan: steps 1–11 parallel, step 12 gated on 45-2/45-3/45-7. |

## Effort Breakdown

| Task | Days |
|---|---|
| Step 1 (map the job graph) + step 2 (the build job) | 0.5 |
| Steps 4–10 (seven touch points across three workflow files) | 0.5 |
| Step 11 (dry run, tag parity) | 0.25 |
| Steps 12–13 (production deploy, probes, first-deploy iteration) | 0.5 |
| Steps 14–15 (human click-through, rollback test) + PR | 0.25 |
| **Total** | **2.0** |

Half the story is YAML across three files. The other half is the first deploy of a new service and
the one thing no pipeline can assert: that a person can sign up.
