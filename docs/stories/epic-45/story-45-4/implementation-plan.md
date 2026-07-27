# Implementation Plan — Story 45-4: Container Image

## Scope & Deliverable

When this story is done, `docker/Dockerfile.dashboard-user` and `docker/nginx-dashboard-user.conf`
exist, `docker build -f docker/Dockerfile.dashboard-user .` produces an image that serves the
customer SPA on port 3002 with working deep-link fallback and a healthcheck that flips to `healthy`,
and `packages/dashboard-user/public/` carries a favicon set and a `robots.txt`. Two new docker files,
one new directory, one edited `index.html`. **No file under `docker/` that already exists is
modified** — the compose wiring is 45-5's.

## Pre-Reading

- `docker/Dockerfile.dashboard:1-41` — **the template. Read every line; this story is an edited copy.**
  Note especially the `@tamma/shared` steps (`:15-16,21,24`) that come out, and the IPv4 healthcheck
  comment (`:36-37`) that stays.
- `docker/nginx-dashboard.conf:1-34` — the SPA server block. Note the upstream-outage comment
  (`:12-16`) and the trailing slash on `proxy_pass http://tamma-api:3100/` (`:18`).
- `packages/dashboard-user/package.json:16-21` — three runtime dependencies, no `@tamma/shared`
- `packages/dashboard-user/tsconfig.json` — no `references` block, unlike `packages/dashboard/tsconfig.json:12-14`
- `packages/dashboard-user/vite.config.ts:8-14` — `outDir: 'dist'`, and the pinned browser target
- `packages/dashboard-user/vite.config.ts:19` — dev port 3002, the reason for D2
- `packages/dashboard/public/` — the favicon set to reuse
- `packages/dashboard/index.html:6-10` — the icon links and title to mirror
- `packages/dashboard-user/src/api/client.ts:137-148` — why no env var is needed
- `docs/stories/epic-45/README.md` — D4, D5, D7
- **All referenced paths exist.** NOT FOUND (this story creates them):
  `docker/Dockerfile.dashboard-user`, `docker/nginx-dashboard-user.conf`,
  `packages/dashboard-user/public/`.

## Design Decisions

- **D1 — Copy `Dockerfile.dashboard` and delete, do not write from scratch.** Every line in that file
  is either a pnpm-in-Docker detail or a bug someone already paid for. Writing a "cleaner" Dockerfile
  loses the IPv4 healthcheck note (`:36-37`), the repo-root build context requirement (`:4-5`), and
  the exact `--filter …...` invocation that makes the install layer cacheable. Start from a literal
  copy and remove the three `@tamma/shared` lines.

- **D2 — Port 3002.** `vite.config.ts:19` already uses it for the dev server. The admin app is 3000
  dev / 3001 container — an existing small inconsistency there is no reason to reproduce. 3002 also
  cannot collide with the admin container if both are ever published on one host
  (`docker-compose.override.yml:34-36` publishes 3001).

- **D3 — Drop the `@tamma/shared` stage, and say why in a comment.** The admin image builds it because
  `packages/dashboard/package.json:19` depends on it and `tsconfig.json:12-14` references it. The
  customer app does neither. Copying the steps anyway would add a build, a cache layer and a failure
  mode for a package nothing imports. The Dockerfile carries a one-line comment stating the
  difference, so a future reader diffing the two files finds the reason instead of assuming an
  omission.

- **D4 — Keep the trailing `...` on the pnpm filter.** `--filter @tamma/dashboard-user...` resolves to
  the package plus its workspace dependencies, of which there are currently zero. Keeping it costs
  nothing, matches the admin image, and is what keeps the file correct the day someone adds a
  workspace dependency — which is a change that would otherwise fail at `pnpm --filter … run build`
  with a confusing resolution error.

- **D5 — No `USER tamma` directive, matching the admin image.** Both images create the 1001 account
  and neither switches to it; nginx binds as root and drops to its worker user. Adding the directive
  to one image only means the two behave differently under a config change (a privileged port, a
  volume permission), and the divergence would be discovered in production on whichever one was not
  tested. Parity now; change both together later if it is worth doing.

- **D6 — No build arg for the API URL.** Neither SPA has one; both work because their own nginx
  proxies `/api/` same-origin (`nginx-dashboard.conf:17-24`). The customer app's `client.ts:142-147`
  falls back to `''` and its paths are absolute, so it issues same-origin `/api/...` exactly like the
  admin app's `'/api'` fallback. Introducing a build arg here makes this the only SPA image in the
  repo with a second config mechanism. Epic README D7.

- **D7 — Two separate nginx confs, not one shared include.** They are 34-line files differing in one
  integer. An include would couple two independently-built, independently-deployed images so that a
  change to one silently rebuilds the other's behaviour. Eleven lines of duplication is the cheaper
  side of that trade.

- **D8 — `robots.txt` with `Disallow: /`.** The admin app has none because `app.tamma.dev` is behind
  oauth2-proxy and has never been reachable by a crawler. This host will be public. A signed-in-only
  SPA has nothing to index and everything to leak from an indexed error page. It is one file and it is
  reversible in one line if a marketing surface later wants a carve-out (epic README open question 3).

## Implementation Steps

1. **Copy `docker/Dockerfile.dashboard` → `docker/Dockerfile.dashboard-user`.** Literal copy first,
   then edit. Do not retype.
2. **Edit the header comment** — name the file and the build command
   (`docker build -f docker/Dockerfile.dashboard-user -t tamma-dashboard-user .`), keeping the
   "build from the repo root" note.
3. **Remove the three `@tamma/shared` lines** (`:16`, `:21`, `:24`) and add D3's one-line comment
   stating the customer app has no workspace dependencies.
4. **Repoint the four package paths** — the `package.json` copy, the `--filter`, the source copy, the
   build, and the `COPY --from=build … dist`.
5. **Change `EXPOSE 3001` → `3002` and the healthcheck URL to `http://127.0.0.1:3002/`.** Copy the
   IPv4 comment verbatim.
6. **Point the runtime nginx conf copy at the new file** —
   `COPY docker/nginx-dashboard-user.conf /etc/nginx/conf.d/default.conf`.
7. **Copy `docker/nginx-dashboard.conf` → `docker/nginx-dashboard-user.conf`;** change `listen 3001`
   → `listen 3002`. Change nothing else — the `/api/` proxy, its trailing slash, the four forwarded
   headers, the asset-caching block and the two security headers are all correct as they stand, and
   the upstream-outage comment stays.
8. **Create `packages/dashboard-user/public/`** and copy the favicon set from
   `packages/dashboard/public/`. Same product, same marks.
9. **Add `public/robots.txt`** — `User-agent: *` / `Disallow: /`, with a comment naming epic README
   open question 3 so a later reversal is an informed decision.
10. **Update `packages/dashboard-user/index.html`** — the four icon links mirroring
    `packages/dashboard/index.html:6-9`. (The `<title>` and description are Story 45-2's AC9; if 45-2
    has not landed, leave them and note the overlap rather than doing it twice.)
11. **Build.** `docker build -f docker/Dockerfile.dashboard-user -t tamma-dashboard-user .` from the
    repo root. Record the image size and layer count in the PR alongside the admin image's, so a
    future unexplained divergence has a baseline.
12. **Run and verify** — `docker run --rm -p 3002:3002 tamma-dashboard-user`, then the four probes in
    the test plan.
13. **Verify the healthcheck** — `docker inspect --format '{{.State.Health.Status}}'` reaches
    `healthy` within the start period.

## Data & Migrations

None.

## Events

None.

## Test Plan

These are container probes, not unit tests. There is no jsdom equivalent for "does nginx serve a deep
link", and that is the failure this story exists to prevent.

| # | Probe | Asserts |
|---|---|---|
| 1 | `docker build -f docker/Dockerfile.dashboard-user .` | exits 0 — AC9 |
| 2 | `curl -o /dev/null -w '%{http_code}' localhost:3002/` | 200, and the body is the SPA `index.html` |
| 3 | `curl -o /dev/null -w '%{http_code}' localhost:3002/settings/billing` | **200** — the SPA fallback (AC10). Without `try_files` this is 404 and every refresh on a deep link breaks. |
| 4 | `curl -o /dev/null -w '%{http_code}' localhost:3002/favicon.ico` | 200 — AC7 |
| 5 | `curl -o /dev/null -w '%{http_code}' localhost:3002/robots.txt` | 200 and `Disallow: /` — AC8 |
| 6 | `curl -I localhost:3002/assets/<hashed>.js` | `Cache-Control: public, immutable`, `expires` one year |
| 7 | `curl -I localhost:3002/` | `X-Frame-Options: SAMEORIGIN`, `X-Content-Type-Options: nosniff` |
| 8 | `docker inspect --format '{{.State.Health.Status}}'` | `healthy` — AC11, and the exact gate 45-6 polls |
| 9 | `curl localhost:3002/api/health` with no `tamma-api` on the network | 502 — **expected**; the proxy is wired and the upstream is absent. Verified properly in 45-5. |

Probes 3 and 8 are the two that matter. Probe 3 is the most common way a working SPA image is
silently broken; probe 8 is the gate that decides whether 45-6's deploy passes.

## Definition of Done

- Both new files exist; a `diff` against their templates shows **only** the port, the package paths,
  the removed `@tamma/shared` steps and the header comments.
- All nine probes pass, with output recorded in the PR.
- Image size and layer count recorded next to the admin image's.
- `packages/dashboard-user/public/` has the favicon set and `robots.txt`; `index.html` declares the
  icon links.
- The IPv4 healthcheck comment and the `tamma-api:3100` upstream comment are present verbatim in the
  new files.
- **No existing file under `docker/` is modified** (grep-checked) — compose wiring is 45-5's.
- **No `ARG`/`ENV` for an API URL** (grep-checked) — D6.
- **No `USER` directive** (grep-checked) — D5.

## Dependencies & Sequencing

- **Blocked by:** nothing. Runs in parallel with the entire application-code half of the epic.
- **Blocks:** 45-5 (compose references this Dockerfile and this port), 45-6 (the GHCR job references
  this Dockerfile path).
- **Shared-edit register:** `packages/dashboard-user/index.html` is also touched by **45-2** (AC9,
  title and description). Trivial merge; if both are in flight, one takes the whole file and the other
  drops its line.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The SPA fallback is missing or wrong** and every deep link 404s on refresh — invisible on a homepage-only smoke test. | Probe 3 targets a real deep route (`/settings/billing`) and is in the DoD. 45-6 adds the same probe to the post-deploy suite so it is checked again against the real host. |
| **The healthcheck uses `localhost` and the container is permanently unhealthy.** Already happened once — `Dockerfile.dashboard:36-37`. | AC3 requires the comment copied verbatim with the line; probe 8 proves it flips to `healthy` before the story closes. |
| **The `/api/` proxy's trailing slash is dropped**, so requests arrive at `tamma-api` as `/api/...` instead of `/...` and every call 404s. | Step 7 says change only the port; the DoD's diff check makes any other change visible in review. 45-5's probe against a live `tamma-api` is the functional proof. |
| **Someone adds a `VITE_API_URL` build arg** believing the app cannot work without one. | D6 and the epic README's D7 both state the mechanism; the DoD greps for `ARG`/`ENV`. The evidence is that the admin app has shipped this way for months. |
| **The `@tamma/shared` removal is later mistaken for an omission** and re-added by someone diffing the two Dockerfiles. | D3's inline comment in the file itself, not only in this plan. |
| **A future workspace dependency breaks the install layer** because the filter was narrowed. | D4 keeps the `...`, which is what makes that case work without a Dockerfile change. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–7 (both docker files, edited from their templates) | 0.25 |
| Steps 8–10 (favicon set, `robots.txt`, `index.html` icon links) | 0.25 |
| Steps 11–13 + the nine probes | 0.5 |
| Review, size/layer baseline, PR write-up | 0.5 |
| **Total** | **1.5** |

The files are an hour. The day is proving the image actually serves a deep link and actually reports
healthy — the two things that are cheap here and expensive in 45-6.
