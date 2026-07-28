# Story 45-4: Container Image — `docker/Dockerfile.dashboard-user` and `docker/nginx-dashboard-user.conf`

Status: done (code) — conformance-reviewed 2026-07-28; in-container /api proxy preserves the prefix (review fix — the AC's bare-/ form was the defect); container run probes are deploy-time

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
I want `packages/dashboard-user` to build into a runnable nginx-served container image,
So that there is an artefact to deploy at all — and so that it is built the same way the admin
console is built, not a new way invented for one app.

## Priority

P0 — Wave 0. Head of the deployment chain: 45-5 (compose + vhost) and 45-6 (build/push/deploy) both
depend on this image existing. It has no dependency on any application-code story, so it can run
fully in parallel with 45-0 … 45-3.

## Architectural Context (READ FIRST)

**Everything in this story mirrors an existing, deployed file. Nothing is invented.**

- **The template: `docker/Dockerfile.dashboard`** — two stages. Build (`:9-25`):
  `node:22-alpine`, `corepack enable && corepack prepare pnpm@latest --activate` (`:10`), copy
  lockfile + workspace + root `package.json` (`:13`), copy the target package's `package.json`
  (`:14`), `pnpm install --frozen-lockfile --filter @tamma/dashboard...` (`:18`), copy `tsconfig.json`
  and sources (`:20-22`), build (`:24-25`). Runtime (`:28-40`): `nginx:1.27-alpine`, a non-root
  `tamma` user at uid/gid 1001 (`:29`), the SPA nginx conf to
  `/etc/nginx/conf.d/default.conf` (`:31`), `dist` to `/usr/share/nginx/html` (`:32`),
  `EXPOSE 3001` (`:34`), a `HEALTHCHECK` (`:38-39`), `CMD ["nginx", "-g", "daemon off;"]`.
- **The one structural difference, and it makes the file simpler.**
  `Dockerfile.dashboard:15-16,21,24` copy and build `@tamma/shared` because the admin app depends on
  it (`packages/dashboard/package.json:19`, and `tsconfig.json:12-14` declares a project reference).
  **`packages/dashboard-user/package.json:17-21` declares exactly three runtime dependencies —
  `react`, `react-dom`, `react-router-dom` — and no `@tamma/shared`**, and its `tsconfig.json` has no
  `references` block. So the shared copy, the shared build step and the shared `package.json` copy all
  come out. Nothing else changes.
- **The healthcheck comment is load-bearing and must be copied verbatim.**
  `Dockerfile.dashboard:36-37`: *"127.0.0.1, not localhost: nginx listens IPv4-only and busybox wget
  resolves localhost to ::1 without falling back -> permanent unhealthy"*. That is a bug someone
  already paid for. Copy the comment with the line.
- **The nginx template: `docker/nginx-dashboard.conf`** — 34 lines. `listen 3001` + `server_name _`
  (`:1-2`), SPA fallback `try_files $uri $uri/ /index.html` (`:8-10`), an `/api/` proxy to
  `http://tamma-api:3100/` with the four standard forwarded headers (`:17-24`), one-year immutable
  caching on hashed static assets (`:27-30`), and `X-Frame-Options: SAMEORIGIN` +
  `X-Content-Type-Options: nosniff` (`:33-34`).
- **The `/api/` proxy is what makes the SPA work without any env var.** Neither app sets
  `VITE_API_URL` or `VITE_API_BASE_URL` anywhere in the repo — not in a Dockerfile, compose file,
  workflow or `.env`. The admin app's clients fall back to `'/api'`; the customer app's
  `client.ts:142-147` falls back to `''` and its paths are already absolute (`/api/auth/me`). Both
  therefore issue **same-origin** `/api/...` requests, which this nginx block proxies. **This is the
  mechanism; do not add a build arg to "fix" it** — see the epic README's D7.
- **The nginx conf comment at `nginx-dashboard.conf:12-16` records a real outage**: the upstream was
  `tamma-api-dotnet:3000`, that service no longer exists, and nginx fails DNS resolution at start-up,
  restart-looping the container. The correct upstream is `tamma-api:3100`. Copy that comment too.
- **Port 3002, not 3001.** `packages/dashboard-user/vite.config.ts:19` already uses 3002 for its dev
  server; the admin app uses 3000 dev / 3001 container. 3002 keeps dev and container agreed and avoids
  a collision with the admin container on the same host.
- **`packages/dashboard-user` has no `public/` directory.** The admin app's
  `packages/dashboard/public/` carries seven files (`favicon.ico`, three PNG sizes,
  `apple-touch-icon.png`, `android-chrome-*`, `logo.png`) and `packages/dashboard/index.html:6-9`
  declares four icon links. The customer app declares none and will 404 on `/favicon.ico`.

## Acceptance Criteria

1. **`docker/Dockerfile.dashboard-user` exists** and is `Dockerfile.dashboard` with the
   `@tamma/shared` stage removed and the filter/paths repointed:
   - `COPY packages/dashboard-user/package.json packages/dashboard-user/`
   - `RUN pnpm install --frozen-lockfile --filter @tamma/dashboard-user...`
   - `COPY packages/dashboard-user packages/dashboard-user`
   - `RUN pnpm --filter @tamma/dashboard-user run build`
   - `COPY --from=build /app/packages/dashboard-user/dist /usr/share/nginx/html`
   No `packages/shared` copy, no `@tamma/shared` build step.
2. **The header comment names the build command**, matching `Dockerfile.dashboard:4-5`:
   `docker build -f docker/Dockerfile.dashboard-user -t tamma-dashboard-user .` — built from the repo
   root, because the build stage needs the workspace lockfile.
3. **`EXPOSE 3002`** and the healthcheck probes `http://127.0.0.1:3002/`, **with the IPv4 comment
   copied verbatim**.
4. **The runtime stage creates the same non-root user** — `addgroup -g 1001 tamma && adduser -u 1001
   -G tamma -s /bin/sh -D tamma` (`Dockerfile.dashboard:29`). Note honestly that, as in the admin
   image, nginx still starts as root to bind and drops to its own worker user; the account is created
   for parity and for any future `USER` directive. **Do not add a `USER tamma` line the admin image
   does not have** — a divergence here means one image fails to bind a privileged port under a config
   change the other survives, and debugging that is worse than the parity.
5. **`docker/nginx-dashboard-user.conf` exists**, a copy of `nginx-dashboard.conf` with `listen 3002`,
   and both its comments preserved (the upstream-outage note at `:12-16` and the security headers).
6. **The `/api/` proxy targets `http://tamma-api:3100/`** — the same upstream, the same trailing
   slash, the same four forwarded headers. The trailing slash is significant (it strips the `/api`
   prefix before proxying); copy the line rather than retyping it.
7. **`packages/dashboard-user/public/` exists** with a favicon set, and `index.html` declares the icon
   links. Reuse the admin app's assets — `packages/dashboard/public/` — rather than commissioning new
   ones; this is the same product.
8. **`public/robots.txt` exists with `Disallow: /`.** The admin app has none and never needed one
   (`app.tamma.dev` is behind oauth2-proxy and has never been crawlable). This host will be publicly
   reachable, and a signed-in-only SPA has nothing to index. Flagged as open question 3 in the epic
   README — `Disallow: /` is the safe default and is trivially reversible.
9. **The image builds and serves locally.**
   `docker build -f docker/Dockerfile.dashboard-user -t tamma-dashboard-user .` succeeds, and
   `docker run -p 3002:3002` serves `/` with a 200 and `/some/deep/route` with a 200 (SPA fallback,
   AC10). The `/api/` proxy will 502 without `tamma-api` on the network — that is expected and is
   verified in 45-5, not here.
10. **The SPA fallback is proven**, not assumed. `curl -s -o /dev/null -w '%{http_code}'
    http://localhost:3002/settings/billing` returns 200 and serves `index.html`. Without
    `try_files ... /index.html` every deep link 404s on refresh — the single most common way a
    correctly-built SPA image is broken.
11. **The healthcheck reports healthy.** `docker inspect --format '{{.State.Health.Status}}'` is
    `healthy` within the start period. 45-6's deploy gate polls exactly this.

## Technical Notes

- **Why the shared-package removal is safe and worth stating.** It is tempting to copy
  `Dockerfile.dashboard` verbatim "for parity". Doing so would copy and build a package the app does
  not import, adding a build step, a cache layer and a failure mode for nothing. The dependency lists
  and the absent `references` block are the evidence; both are cited in AC1's context.
- **`pnpm install --filter @tamma/dashboard-user...`** — the trailing `...` includes the package's
  workspace dependencies. It has none today. Keep the `...` anyway: it matches the admin image's
  invocation, and it is what makes the file still correct the day someone adds `@tamma/shared`.
- **Do not introduce a build arg for the API URL.** Both SPAs are build-time-only with no runtime
  config injection, and both work because their nginx serves the API same-origin. Adding a
  `VITE_API_URL` build arg to one image makes it the only SPA in the repo with a second mechanism, and
  the first person to deploy the other one will not know it exists. Epic README D7.
- **Static-asset caching is safe because Vite hashes filenames** — `dist/assets/index-DoLswaze.js`
  from the audit build. The one-year immutable header applies only to the hashed-extension pattern at
  `nginx-dashboard.conf:27-30`; `index.html` is deliberately outside it.

## Dependencies

- **Blocked by:** nothing. Pure infrastructure; no application-code story gates it.
- **Blocks:** **45-5** (the compose service references this Dockerfile and this port) and **45-6**
  (the build/push job references this Dockerfile path).

## Blocks / Blocked by

- **Blocks:** 45-5, 45-6.
- **Blocked by:** nothing.

## Out of Scope

- The compose service, the vhost, TLS, DNS — 45-5.
- The GHCR build/push job and the deploy steps — 45-6.
- Any change to `docker/Dockerfile.dashboard`, `docker/nginx-dashboard.conf`, or the admin app.
- Runtime config injection, build args, `window.__CONFIG__` — Epic README D7.
- Multi-arch builds. The admin image is single-arch; parity.
- Sharing the two nginx confs via an include. They are 34 lines that differ in one number; an include
  would couple two independently-deployed images to save eleven lines.

## Estimated Effort

**1.5 days.** The two files are an hour — they are edited copies. The rest is AC9–AC11: building the
image, running it, proving the SPA fallback on a deep link, proving the healthcheck flips to
`healthy`, and sourcing the favicon set. Each is a small thing that is only ever discovered in
production if it is not done here.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
