# Implementation Plan — Story 45-5: Compose Service, vhost, Hostname and TLS

## Scope & Deliverable

When this story is done, `tamma-dashboard-user` is a compose service in all three compose files with
the admin app's resource profile, `dash.tamma.dev` resolves to the VPS over TLS, `nginx-proxy` routes
it to the SPA container with **no oauth2-proxy gate**, and `post-deploy-tests.sh` carries probes that
prove exactly that. Four edited files, one certificate check, one DNS record.

## Pre-Reading

- `docker/docker-compose.yml:306-320` — the `tamma-dashboard` service, the template for AC1
- `docker/docker-compose.yml:426-453` — `nginx-proxy`, its cert mounts (`:444-445`) and its
  `depends_on` set (`:448-451`) with the hard/soft comment at `:428-430`
- `docker/docker-compose.prod.yml:14-31` — the **memory budget table** that AC2 must update
- `docker/docker-compose.prod.yml:92-99` — the admin dashboard's limits block
- `docker/docker-compose.override.yml:34-36` — the dev port publish
- `docker/nginx-proxy.conf.template:1-49` — the header routing map (AC9) and the port-80 redirect (AC8)
- `docker/nginx-proxy.conf.template:81-173` — the `app.tamma.dev` block. **Read it to know what NOT to
  copy.** Lines `:105-127`, `:130-133`, `:146-152`, `:155-157` are all oauth2-proxy machinery.
- `docker/nginx-proxy.conf.template:139-145` — the `/api/` block to copy, **note the trailing `/api/`**
- `docker/nginx-proxy.conf.template:177-201` — `api.tamma.dev`, the plain-443-no-auth block to model
- `docker/nginx-dashboard.conf:17-24` — the in-container proxy, **trailing `/`**, for contrast
- `docker/post-deploy-tests.sh:15-20` — why `--resolve` + `Host:` and not one or the other
- `docker/post-deploy-tests.sh:129-148` — the existing per-host probes; `:142` asserts `app.tamma.dev`
  returns **302**, and AC10 asserts the new host returns **200**
- `docs/stories/epic-45/README.md` — D1, D2, D5
- **All referenced paths exist.** This story creates no file; it edits four and touches two external
  systems (DNS, TLS).

## Design Decisions

- **D1 — Model the vhost on `api.tamma.dev`, not `app.tamma.dev`.** `app.tamma.dev` is the nearest
  example and the wrong one: 90 of its lines are oauth2-proxy. `api.tamma.dev` (`:177-201`) is a plain
  443 server with no auth, which is the correct skeleton. Take the `/api/` location from
  `app.tamma.dev:139-145` (it is the one the customer host also needs) and the `location /` shape
  from `app.tamma.dev:164-171` minus its `auth_request`/`error_page` lines. Being explicit about the
  two sources is the point — a copy of one block would be wrong in one direction or the other.

- **D2 — No `auth_request`, and say so in a header comment inside the file.** The customer app has its
  own `/login`, `/register` and cookie-session `AuthProvider`; anonymous reachability is the
  requirement, not an oversight. Someone editing this template in six months will see three blocks
  with `auth_request` and one without, and the comment is what stops them "fixing" the outlier. One
  sentence, in the file, not only in this plan.

- **D3 — Verify the certificate SANs before writing the vhost, not after.** A 443 block whose cert
  does not cover its `server_name` fails TLS at the handshake and produces a browser interstitial that
  looks like an attack. If the mounted origin cert is a `*.tamma.dev` wildcard, this is a no-op —
  but *checking* is one command and *not* checking risks shipping a block that cannot serve. If a
  re-issue is needed it is an operator action with lead time, and it must be started on day one.

- **D4 — DNS and TLS start on day one, in parallel with the code.** Both are external, both have
  propagation or issuance lead time, and neither is visible in the repository. A plan that leaves them
  to the end has a two-day story finishing on day four for reasons no code review can see.

- **D5 — `nginx-proxy` hard-depends on the customer app.** The existing comment (`:428-430`) splits
  hard dependencies (API, dashboard) from soft (oauth2-proxy, opensearch-dashboards) on criticality.
  The customer app is the product; it is at least as critical as the admin console. Update the comment
  as well as the list — a `depends_on` entry that contradicts the comment above it is how the next
  person removes it.

- **D6 — Keep the in-container `/api/` proxy even though production does not use it.** With
  `nginx-proxy` in front, the SPA container's own `/api/` block is redundant on the production path.
  It is what makes `docker-compose.override.yml`'s direct `localhost:3002` work in dev and what makes
  a bare-IP deployment work (`nginx-proxy.conf.template:52-79` has such a block for the admin app).
  Removing it as "dead" would break local development for a saving of eight lines.

- **D7 — The `post-deploy` probe asserts 200, and that is a deliberate inversion.**
  `post-deploy-tests.sh:142` asserts `app.tamma.dev /` returns **302** — the proof that oauth2-proxy
  is in front of it. The customer host's probe asserts **200** — the proof that it is not. Written as
  a mirrored pair, the two probes together document the intended difference between the hosts better
  than any comment, and a regression in either direction fails the deploy.

## Implementation Steps

1. **Check the certificate (D3).**
   `openssl x509 -noout -text -in secrets/origin-cert.pem | grep -A1 'Subject Alternative Name'`.
   Record the SAN list in the PR. If `dash.tamma.dev` is not covered, request the re-issue **now** and
   note the block in the PR.
2. **Create the DNS record (D4).** `dash` → the VPS, Cloudflare-proxied, matching the existing hosts'
   configuration. Record what was created, in which zone, and with which proxy setting.
3. **Add the compose service** — `docker/docker-compose.yml`, immediately after `tamma-dashboard`
   (`:320`). Copy the block, change the dockerfile path and the service name, keep everything else,
   add the header comment.
4. **Add the prod limits** — `docker/docker-compose.prod.yml`, after `:99`. Copy the admin block.
5. **Update the memory-budget table** in that file's header (`:14-31`) — add the row and re-total. A
   budget table that does not include a running service is worse than none, because it is trusted.
6. **Add the dev port publish** — `docker/docker-compose.override.yml`, after `:36`. `"3002:3002"`.
7. **Add `nginx-proxy`'s dependency** — `docker-compose.yml:448-451` — and update the comment at
   `:428-430` (D5).
8. **Add `dash.tamma.dev` to the port-80 redirect** — `nginx-proxy.conf.template:47`'s `server_name`
   list.
9. **Write the vhost block.** Place it after `api.tamma.dev` (`:201`) so the file reads
   app → api → dash → elsa → logs. Per D1: `listen 443 ssl` + cert directives from a sibling, the
   `/api/` location from `:139-145` (**trailing `/api/`**), the `location /` from `:164-171` minus
   `auth_request` and `error_page`, upstream `http://tamma-dashboard-user:3002`. Add D2's comment.
10. **Update the header routing map** (`:16-22`) with the `dash.tamma.dev` row (AC9).
11. **Add the post-deploy probes** — `docker/post-deploy-tests.sh`, in the existing style near the
    `app.tamma.dev` probes (`:140-148`). Three: `/` → 200 (D7, with an inline comment stating it is
    the deliberate inverse of `:142`), `/settings/billing` → 200, `/api/health` → 200. Add the host to
    the `--resolve` setup at `:129-130`.
12. **Bring up the stack locally.**
    `docker compose -f docker/docker-compose.yml -f docker/docker-compose.override.yml up -d`.
    Confirm the service reaches healthy, `curl localhost:3002/` serves the app, and
    **`curl localhost:3002/api/health` returns 200** — the first proof the `/api/` proxy works against
    a live upstream, which 45-4 could only leave at 502.
13. **Validate the nginx template renders.** The `nginx-proxy` image runs `envsubst` over
    `/etc/nginx/templates/*.template`. Bring up `nginx-proxy` locally and confirm it starts without a
    config error and without a DNS failure on the new upstream. A typo in a `proxy_pass` hostname
    restart-loops the proxy and takes **every** host down, not just the new one.
14. **Probe through the proxy.** With the full stack up, run the three new `post-deploy-tests.sh`
    probes against the local proxy using `--resolve`, exactly as CI will.

## Data & Migrations

None.

## Events

None.

## Test Plan

| # | Probe | Asserts |
|---|---|---|
| 1 | `docker compose config` | all three compose files merge without error; the service appears once |
| 2 | `docker compose up -d tamma-dashboard-user` | reaches `healthy` |
| 3 | `curl localhost:3002/` | 200, SPA served |
| 4 | `curl localhost:3002/api/health` | **200** — the `/api/` proxy against a live `tamma-api` (AC11) |
| 5 | `nginx-proxy` starts | no config error, no DNS failure on `tamma-dashboard-user` — step 13 |
| 6 | `curl --resolve dash.tamma.dev:443:<ip> https://dash.tamma.dev/` | **200, not 302** — D7, the no-oauth2-proxy proof |
| 7 | `curl --resolve … https://dash.tamma.dev/settings/billing` | 200 — SPA fallback through the full chain |
| 8 | `curl --resolve … https://dash.tamma.dev/api/health` | 200 — `/api/` through the proxy, trailing-slash correctness |
| 9 | `curl -I http://dash.tamma.dev/` | 301 to https — AC8 |
| 10 | `curl --resolve … https://app.tamma.dev/` | still **302** — the admin host is unchanged; the regression check on step 9's `server_name` edit |
| 11 | TLS handshake on `dash.tamma.dev` | no cert warning — AC5 |

Probe 6 is the one this story exists for. Probe 10 is the one that catches breaking the admin console
while adding a host to a shared `server_name` line.

## Definition of Done

- Service present in `docker-compose.yml`, `docker-compose.prod.yml` and `docker-compose.override.yml`
  with the admin app's profile; the prod memory-budget table updated and re-totalled.
- `nginx-proxy` hard-depends on it; the hard/soft comment updated.
- `dash.tamma.dev` block present, **containing no `auth_request`, no `/oauth2/`, no `@oauth2_redirect`
  and no `/sign-out`** (grep-checked in review), with D2's comment.
- The header routing map lists the new host.
- The `/api/` upstream is `http://tamma-api:3100/api/` — **trailing `/api/`, not `/`** (review-checked
  against `:140`).
- All eleven probes pass; output in the PR.
- Certificate SAN list recorded in the PR; DNS record and its settings recorded in the PR.
- `post-deploy-tests.sh` carries the three probes, with the inline comment pairing the 200 assertion
  to `:142`'s 302.
- **`docker/Dockerfile.dashboard-user` and `docker/nginx-dashboard-user.conf` unchanged** — 45-4 owns
  them.

## Dependencies & Sequencing

- **Blocked by:** 45-4 (the image, its port, its service name).
- **Blocks:** 45-6 (starts this service, runs these probes), 45-7 (needs the host to resolve).
- **External:** DNS record and possibly a re-issued origin cert. **Start on day one** (D4).
- **Shared-edit register:** `docker/docker-compose.yml`, `docker/nginx-proxy.conf.template` and
  `docker/post-deploy-tests.sh` are all also touched by **45-6** (which adds the image override and
  the deploy/verify steps in the workflows, and may extend the same probe block). Sequence 45-5 fully
  before 45-6 — they are adjacent lines in the same files.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The `app.tamma.dev` block is copied wholesale** and the customer app ends up behind oauth2-proxy — a GitHub OAuth wall in front of the registration form, which will read as an intentional SSO feature. | D1 names both source blocks and what to take from each; D2 puts the reason in the file; probe 6 asserts 200-not-302 and is in `post-deploy-tests.sh`, so it fails every future deploy too. |
| **The origin certificate does not cover the new subdomain**, discovered after the vhost is written. | Step 1 is the first action of the story, before any file is edited, and its output goes in the PR. |
| **DNS propagation runs past the story's end.** | Step 2 is day one. Probes 6–8 use `--resolve` and therefore pass before propagation, so the code can be verified independently of DNS. |
| **The `/api/` upstream is copied from the wrong sibling** — `nginx-dashboard.conf:18` has a trailing `/`, `nginx-proxy.conf.template:140` has `/api/` — and every API call 404s. | The DoD names the exact expected string; probe 8 exercises it end-to-end; the Technical Note in the story file explains why the two differ. |
| **Editing the shared port-80 `server_name` line breaks the other three hosts.** | Probe 10 checks `app.tamma.dev` still behaves; probe 9 checks the new redirect. |
| **A typo in the new `proxy_pass` hostname restart-loops `nginx-proxy`, taking every host down** — not just the new one. | Step 13 brings the proxy up locally before anything reaches the VPS. Note that variable-based `proxy_pass` (the `set $x …` idiom used for optional services at `:107,229`) defers DNS to request time; the new upstream is a **hard** dependency and uses a literal, which fails fast at start-up — correct, but it means a typo is an outage rather than a 502. |
| **The prod memory budget silently overruns** on a 16 GB box after adding a 256 MB service. | AC2/step 5 update and re-total the table. 256 MB against the documented ~7.2 GB headroom is not close, but an untracked service is how the next one is. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–2 (cert SAN check, DNS record) — external lead time, started day one | 0.25 |
| Steps 3–7 (three compose files, budget table, `depends_on` + comment) | 0.5 |
| Steps 8–10 (vhost block, port-80 redirect, routing map) | 0.5 |
| Steps 11–14 (post-deploy probes, local stack, proxy validation, eleven probes) | 0.5 |
| Review and PR write-up (cert SANs, DNS settings) | 0.25 |
| **Total** | **2.0** |

The editing is half a day. The other day and a half is proving the host is reachable, is *not* behind
oauth2-proxy, and did not break the three hosts that share its config file.
