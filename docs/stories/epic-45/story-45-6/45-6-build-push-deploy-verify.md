# Story 45-6: Build, Push, Deploy, Verify — `docker-publish.yml`, `deploy.yml` and the Smoke Tests

Status: code-complete — conformance-reviewed 2026-07-28; first production deploy + rollback check are deploy-time, tracked in .dev/findings/2026-07-28-epic45-cutover-evidence.md items 3-4

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform engineer**,
I want the customer application built, pushed to GHCR and deployed by the same pipeline that ships
everything else,
So that it reaches production on every merge rather than by hand, and so a broken customer app fails
the deploy instead of surfacing as a customer report.

## Priority

P0. The last link of the deployment chain. After this the app is live.

## Architectural Context (READ FIRST)

**Every change mirrors the admin console's equivalent. Nothing here is a new pattern.**

- **`docker-publish.yml` is the real pipeline** — it builds, pushes and deploys. `deploy.yml` is
  manual re-deploy without rebuilding (`deploy.yml:1-5` says so explicitly).
- **The build job to copy** — `.github/workflows/docker-publish.yml:139-206`, `build-dashboard`:
  checkout, `docker/setup-buildx-action@v3` with a **retry pair** (`:148-159` — attempt 1
  `continue-on-error`, `sleep 60`, attempt 2), GHCR login (`:161-166`), `docker/metadata-action@v5`
  with six tag rules (`:168-178`: branch, `pr-` prefixed PR, three semver forms, `sha-` prefixed),
  then `docker/build-push-action@v6` **also as a retry pair** (`:180-206`, `sleep 30` between) with
  `cache-from: type=gha` / `cache-to: type=gha,mode=max`.
- **The retry pairs are not boilerplate.** They exist because buildx setup and GHCR pushes fail
  transiently in this repo often enough that someone wrote the same defensive shape into every build
  job. Copy them.
- **The image-override block** — `docker-publish.yml:447-467` and `deploy.yml:133-152` each generate a
  `docker-compose.images.yml` with one entry per service:
  ```yaml
  tamma-dashboard:
    image: ghcr.io/${OWNER}/tamma-dashboard:${IMAGE_TAG}
    build: !reset null
  ```
  `build: !reset null` clears the `build:` stanza 45-5 added so compose pulls instead of building.
  **Both files need the new entry** — they are separate generators and drift between them means a
  manual re-deploy silently rebuilds from source.
- **The layer-4 start step** — `docker-publish.yml:679` `up -d --force-recreate tamma-engine
  tamma-dashboard elsa-studio`, and `deploy.yml:250`.
- **The layer-4 verify step** — `docker-publish.yml:701-730`, added deliberately (its comment at
  `:701-709` records that layer-4 services were started but never verified, so a restart-looping
  container survived a deploy and surfaced as a user-visible 500 after merge). It polls
  `docker inspect` state and restart count 3× at 10 s per service (`:718`).
- **The final health list** — `docker-publish.yml:824` (nine services) and `deploy.yml:310` (seven).
- **`docker-smoke-test.yml:57`** iterates `postgres rabbitmq tamma-api tamma-dashboard`.
- **`post-deploy-tests.sh`** already carries the customer host's probes — **Story 45-5 added them**.
  This story ensures they run.

## Acceptance Criteria

1. **A `build-dashboard-user` job in `docker-publish.yml`**, placed immediately after
   `build-dashboard` (`:206`), a copy with `images:` → `ghcr.io/…/tamma-dashboard-user` and
   `file:` → `docker/Dockerfile.dashboard-user`. **Both retry pairs preserved** — buildx setup and
   build-push — with their `continue-on-error`, sleeps and `if:` conditions intact. `context: .`
   unchanged (the build needs the repo root, per 45-4).

2. **The job is wired into whatever gates the deploy.** Check how `build-dashboard` reaches the deploy
   job — a `needs:` list, a matrix, or nothing — and mirror it exactly. **A build job that pushes an
   image nothing waits for produces a deploy that races its own artefact**, and the failure is
   intermittent and tag-dependent, which is the worst kind.

3. **`docker-publish.yml`'s image-override block gains the entry** (`:447-467`), with
   `build: !reset null`.

4. **`deploy.yml`'s image-override block gains the same entry** (`:133-152`). Same text. Drift here
   means the manual re-deploy path rebuilds from source while the automated path pulls — two
   different artefacts from one tag.

5. **The service is started with layer 4** — `docker-publish.yml:679` and `deploy.yml:250` both add
   `tamma-dashboard-user` to their `up -d --force-recreate` lists.

6. **The service is in the layer-4 verify loop** — `docker-publish.yml:718`'s `for svc in …`. This is
   the step that catches a restart-looping container, and its own comment (`:701-709`) records the
   incident that motivated it. A new customer-facing service not in that loop reproduces exactly the
   failure the step was written to prevent.

7. **The service is in the final health lists** — `docker-publish.yml:824` and `deploy.yml:310`.

8. **The service is in `docker-smoke-test.yml:57`.**

9. **`post-deploy-tests.sh` actually runs in the pipeline, and its new probes are green.** Confirm how
   and where the script is invoked; if the customer-host probes 45-5 added are not reached by any
   workflow, wiring them is part of this story. A probe nobody runs is a comment.

10. **A first production deploy is performed and recorded.** `https://dash.tamma.dev/` returns 200
    (not 302), a deep link returns 200, `/api/health` through the host returns 200, and the container
    is healthy. **This is the acceptance criterion the epic exists for** — everything before it is
    preparation.

11. **A rollback path is stated and verified.** The images are tagged by branch, PR, semver and SHA
    (`:172-178`), so rolling back is `deploy.yml` with an earlier `image_tag`. Confirm the new image
    carries the same tag set as `tamma-dashboard` for the same commit, and record in the PR that a
    rollback was tested — or, if not tested, that it was not, rather than assuming symmetry.

## Technical Notes

- **The two generators are the highest-risk duplication in this story.** `docker-publish.yml:447` and
  `deploy.yml:133` are independent heredocs listing the same services. `deploy.yml`'s list is already
  *different* — it carries `tamma-api-dotnet` (`:147`) which `docker-publish.yml`'s does not, and
  lacks `elsa-studio` which it does. They have already drifted. Add the entry to both, and note the
  existing drift in the PR without fixing it — that is a separate finding, and conflating them makes
  this story's diff unreviewable.
- **Do not "improve" the retry pairs.** They look like copy-paste noise. They are a recorded response
  to transient buildx and GHCR failures, and they are in every build job in the file. A job without
  them fails intermittently and will be blamed on the new app.
- **AC10 needs a person.** The workflow cannot assert "a customer can register", and jsdom cannot
  either. One human walking register → verify → login → open billing, once, against production, is
  the only thing that closes this epic.
- The customer app's tests already run in CI (`ci.yml:49-50`) and its typecheck is added by 45-0.
  **Nothing about CI test wiring belongs in this story** — that is 45-0's, and the finding that said
  otherwise is corrected there.

## Dependencies

- **Blocked by:** **45-4** (the Dockerfile path the build job references) and **45-5** (the compose
  service the deploy starts, and the probes AC9 runs).
- **Blocks:** nothing in this epic — but it is what unblocks **Story 39-19**, **Story 44-6** and
  **Epic 34-9's delivered value**, none of which can reach a customer until this lands.
- **Soft:** 45-1, 45-2, 45-3 and 45-7 should land before AC10's production deploy. Deploying with the
  entry points still broken (45-2) or the billing warning still silent (45-1) ships a known-defective
  customer surface. **This is a sequencing preference, not a technical block** — see the execution
  plan.

## Blocks / Blocked by

- **Blocks:** nothing in Epic 45. Unblocks 39-19, 44-6, 34-9's value.
- **Blocked by:** 45-4, 45-5.

## Out of Scope

- The Dockerfile and nginx conf — 45-4.
- Compose, vhost, DNS, TLS — 45-5.
- Fixing the existing drift between the two image-override generators — noted, not fixed.
- Adding the customer app to `e2e/` Playwright suites. The admin app has `e2e/tests/dashboard.spec.ts`;
  a customer equivalent is worth having and is not this story.
- Any change to `ci.yml` — 45-0 owns it.
- Multi-arch builds, image signing, SBOM — the admin image has none; parity.

## Estimated Effort

**2 days.** The YAML is a few hours of careful mirroring across seven touch points in three workflow
files. The rest is AC10 and AC11 — a real production deploy, a real click-through, and a rollback
check — plus the iteration a first deploy of a new service always needs.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
