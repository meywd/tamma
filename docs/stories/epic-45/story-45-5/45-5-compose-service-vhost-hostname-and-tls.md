# Story 45-5: Compose Service, `dash.tamma.dev` vhost, TLS and DNS

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **customer**,
I want `https://dash.tamma.dev` to load the Tamma application,
So that the product exists at an address — one that does **not** put a GitHub OAuth wall in front of
the registration form.

## Priority

P0. The middle link of the deployment chain: it consumes 45-4's image and is consumed by 45-6's
deploy job.

## Architectural Context (READ FIRST)

**Every change here mirrors the admin console's equivalent entry. Nothing is invented.**

- **Compose service** — `docker/docker-compose.yml:308-320`:
  ```yaml
    tamma-dashboard:
      build:
        context: ..
        dockerfile: docker/Dockerfile.dashboard
      restart: unless-stopped
      depends_on:
        tamma-api:
          condition: service_started
      networks:
        - tamma-net
  ```
  No published ports — reachable through `nginx-proxy` only.
- **Prod resource limits** — `docker/docker-compose.prod.yml:92-99`: `restart: unless-stopped`,
  `cpus: "0.25"`, `memory: 256M`, and the comment *"No exposed ports — reachable via nginx-proxy
  only"*.
- **Dev port publish** — `docker/docker-compose.override.yml:34-36`: `"3001:3001"`.
- **`nginx-proxy` hard-depends on it** — `docker-compose.yml:449-451`, with the comment at `:428-430`
  explaining that dashboard and API are the critical path while oauth2-proxy and
  opensearch-dashboards are soft.
- **The vhost** — `docker/nginx-proxy.conf.template:81-173` is the `app.tamma.dev` block. **Most of it
  must NOT be copied.** It contains the nav-bar aliases (`:85-100`), the oauth2-proxy endpoints
  (`:105-127`), the `/sign-out` cookie clear (`:130-133`), the `@oauth2_redirect` named location
  (`:146-152`), and — critically — `location /` gated by `auth_request /oauth2/auth` (`:155-172`).
- **THE decision: the customer vhost has no `auth_request`.** The customer app ships its own
  `/login`, `/register` and cookie-session `AuthProvider` (`src/hooks/useAuth.tsx:57-118`) and must be
  reachable anonymously. Dropping it behind the existing block puts a GitHub OAuth wall in front of a
  registration form for an account that does not exist yet. The block to model is
  **`api.tamma.dev`** (`:177-201`) — a plain 443 server with no auth — plus the `/api/` proxy shape
  from `app.tamma.dev:139-145`.
- **Port-80 redirect** — `:44-49`, `server_name app.tamma.dev api.tamma.dev elsa.tamma.dev
  logs.tamma.dev; return 301 https://$host$request_uri;`. The new host joins that list.
- **TLS** — one Cloudflare origin cert pair mounted into `nginx-proxy`
  (`docker-compose.yml:444-445`: `../secrets/origin-cert.pem`, `origin-key.pem`). Every 443 block
  references the same pair. **Whether it covers a fourth subdomain is the one genuinely unknown in
  this story** — see AC5.
- **The session cookie already works cross-subdomain.** `tamma_session` is set with
  `Domain=.tamma.dev` (`docs/stories/epic-16/16-4-unified-navigation-impl-plan.md:278`, and the
  nginx clear at `nginx-proxy.conf.template:130-132`). So a new `*.tamma.dev` host inherits the
  session with no cookie change.
- **CORS is not in play for the SPA's own calls**, because 45-4's nginx serves `/api/` same-origin.
  It *is* in play for `Dashboard:Url` — that is 45-7.
- **`post-deploy-tests.sh`** probes each host with `--resolve` and an explicit `Host:` header
  (`:129-130`, `:140-148`), with a comment at `:17-19` explaining why SNI must match. The new host
  joins it here; 45-6 wires it into the workflow.

## Acceptance Criteria

1. **`tamma-dashboard-user` service in `docker/docker-compose.yml`**, placed immediately after
   `tamma-dashboard` (`:308-320`) and structurally identical: `build.context: ..`,
   `dockerfile: docker/Dockerfile.dashboard-user`, `restart: unless-stopped`,
   `depends_on: tamma-api: {condition: service_started}`, `networks: [tamma-net]`, **no published
   ports**. A header comment matching the admin service's (`:306-308`), naming it the customer SPA.

2. **`docker/docker-compose.prod.yml` gains the matching limits block** — `cpus: "0.25"`,
   `memory: 256M`, and the "No exposed ports" comment. Same shape as `:92-99`. Also update the
   memory-budget table in that file's header comment (`:14-31`), which enumerates every service and
   totals them; adding a service without updating it makes the table wrong on day one.

3. **`docker/docker-compose.override.yml` publishes `"3002:3002"`** for local dev, beside the admin
   app's `3001:3001` (`:34-36`).

4. **`nginx-proxy` depends on it** — add to `docker-compose.yml:449-451`. The customer app is at least
   as critical-path as the admin console, so it belongs in the hard-dependency set, and the comment at
   `:428-430` is updated to say so.

5. **TLS coverage for `dash.tamma.dev` is verified before the vhost is written.** Inspect the mounted
   origin certificate's SANs
   (`openssl x509 -noout -text -in secrets/origin-cert.pem | grep -A1 'Subject Alternative Name'`).
   - If it is a `*.tamma.dev` wildcard → nothing to do; **record the SAN list in the PR** so the next
     subdomain does not repeat this check.
   - If it enumerates hosts → a re-issued Cloudflare origin certificate is a prerequisite, and it is
     an **operator action with a lead time**, not a code change. Say so in the PR and block the vhost
     on it rather than shipping a block that fails TLS.
   **This is the one item in the story that can genuinely stall it**, and it is why it is AC5 and not
   an afterthought.

6. **DNS: a `dash` record for the VPS, proxied through Cloudflare exactly as the existing hosts are.**
   Also an operator action, also with a propagation lead time. Record what was created and where in
   the PR — DNS is the piece of this deployment with no representation in the repository, and the only
   durable record of it will be that description.

7. **A `dash.tamma.dev` server block in `docker/nginx-proxy.conf.template`**, modelled on
   `api.tamma.dev` (`:177-201`), containing:
   - `listen 443 ssl; server_name dash.tamma.dev;` and the same cert directives as its siblings.
   - `location /api/ { proxy_pass http://tamma-api:3100/api/; … }` — copied from
     `app.tamma.dev:139-145`, **including the trailing `/api/` on the upstream**, which differs from
     the in-container conf's trailing `/` and is easy to get wrong by copying the wrong sibling.
   - `location / { proxy_pass http://tamma-dashboard-user:3002; … }` with the same forwarded headers
     and the `Upgrade`/`Connection` pair the admin block carries (`:170-171`).
   - **No `auth_request`. No `/oauth2/` location. No `@oauth2_redirect`. No `/sign-out`. No nav-bar
     aliases.** A header comment states, in one sentence, that the absence of `auth_request` is
     deliberate and why.

8. **`dash.tamma.dev` joins the port-80 redirect** at `:44-49`.

9. **The routing table in the template's header comment (`:16-22`) gains its row.** That comment is
   the only routing map anyone reads before editing this file; a host missing from it is a host the
   next editor breaks.

10. **`post-deploy-tests.sh` gains probes for the new host**, in the file's existing
    `--resolve` + `Host:` style: `/` returns **200, not 302** (the assertion that proves it is *not*
    behind oauth2-proxy — the inverse of `:142`'s assertion for `app.tamma.dev`), a deep link such as
    `/settings/billing` returns 200 (SPA fallback against the real proxy chain), and `/api/health`
    through this host returns 200 (proves the `/api/` proxy).

11. **The whole stack comes up locally.** `docker compose -f docker-compose.yml -f
    docker-compose.override.yml up -d` brings the new service to healthy, `curl localhost:3002/`
    serves the app, and `curl localhost:3002/api/health` returns 200 — **proving the `/api/` proxy
    against a live `tamma-api`**, which 45-4 could only leave at 502.

## Technical Notes

- **AC10's 200-not-302 probe is the most valuable assertion in this story.** The single most likely
  way to get this wrong is to copy the `app.tamma.dev` block wholesale — it is the nearest, most
  complete example, and it is 90 lines of oauth2-proxy machinery. If that happens, the symptom is a
  customer being asked to sign in with GitHub before they can register, and it will look like an
  intentional SSO feature to anyone who did not read this story.
- **Cookie-domain interaction, worth knowing and not changing.** `_oauth2_proxy` is issued for
  `.tamma.dev` (`nginx-proxy.conf.template:27`), so it will be *sent* to `dash.tamma.dev`. That is
  harmless — nothing on this vhost reads it. `tamma_session` is the cookie that matters and it is
  already `.tamma.dev`.
- **The `/api/` upstream differs between the two nginx layers and both are correct.** In-container
  (`nginx-dashboard.conf:18`): `proxy_pass http://tamma-api:3100/` — trailing slash strips `/api`.
  In the proxy (`nginx-proxy.conf.template:140`): `proxy_pass http://tamma-api:3100/api/` — preserves
  it. Copying the wrong one produces a 404 on every API call, and the two files sit ten lines apart in
  a reviewer's diff.
- **In production, requests reach the SPA container through `nginx-proxy`, so the SPA's own `/api/`
  block is redundant on that path** — but it is what makes `docker-compose.override.yml`'s direct
  `localhost:3002` work in dev, and what would make a bare-IP deployment work. Keep both.

## Dependencies

- **Blocked by:** **45-4** — the compose service references its Dockerfile and its port; the vhost
  references its upstream name.
- **Blocks:** **45-6** (the deploy job starts this service and runs these probes) and **45-7** (which
  cannot point `Dashboard:CustomerUrl` at a host that does not resolve).
- **External, operator-owned, with lead time:** the DNS record (AC6) and possibly a re-issued origin
  certificate (AC5). **Start both on day one of this story**, not when the vhost is written.

## Blocks / Blocked by

- **Blocks:** 45-6, 45-7.
- **Blocked by:** 45-4.

## Out of Scope

- The GHCR image reference, the build/push job, the deploy steps — 45-6. This story's compose entry
  uses `build:`, exactly as the admin service does; 45-6 adds the `image:` + `build: !reset null`
  override.
- `Dashboard:Url` / CORS — 45-7.
- The cross-subdomain nav bar. It links operator surfaces (ELSA Studio, OpenSearch); a customer
  should not see them. Epic README, Out.
- Rate limiting or WAF rules on the new host beyond what Cloudflare already applies to the zone.
- Moving the admin console to a different hostname — epic README open question 2, not taken.

## Estimated Effort

**2 days.** The compose entries and the server block are half a day of careful copying. The rest is
AC5 and AC6 — certificate SAN verification and a DNS record, both of which have external lead time
and neither of which is finished when the code is — plus AC10/AC11's probes against a live stack.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
