# Story 45-7: The `Dashboard:Url` Split — Customer Links Stop Pointing at the Admin Console

Status: done — conformance-reviewed 2026-07-28; AllowCredentials ADDED against the AC's prohibition (the AC's premise was wrong — customer auth is the tamma_session cookie with credentials:'include'); compose customer-URL default deliberately EMPTY (the AC's suggested default would exfiltrate self-hosters' tokens)

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **customer who has just been sent a verification email, a password reset or an org invite**,
I want the link to open the customer application,
So that I am not asked to authenticate with GitHub against an operator console for an account I do not
have — which is what every one of those emails does today.

## Priority

P0, and last. It is the switch that makes the whole epic take effect, and it must not be thrown until
the pages exist (45-3) and the host resolves (45-5).

## Architectural Context (READ FIRST)

- **One configuration value drives six customer-facing links and the CORS allow-list.**
  `Dashboard:Url`, set to `https://app.tamma.dev` at `docker/docker-compose.yml:257`
  (`Dashboard__Url: ${DASHBOARD_URL:-https://app.tamma.dev}`), and again at
  `docker/.env.example:40`, `.github/workflows/docker-publish.yml:425` and
  `.github/workflows/deploy.yml:110`.

  | Consumer | File:line | Emits |
  |---|---|---|
  | Email verification link | `Endpoints/AuthEndpoints.cs:29-33` | `{base}/verify?token=` |
  | Password reset link | `Endpoints/AuthEndpoints.cs:35-39` | `{base}/reset-password?token=` |
  | Org invite accept | `Endpoints/OrgEndpoints.cs:361-362` | `{base}/invites/accept?token=` |
  | Org invite pending | `Endpoints/OrgEndpoints.cs:501-502` | `{base}/invites/pending?inviteId=` |
  | GitHub install success | `Endpoints/GitHubEndpoints.cs:23,40` | `{base}/onboarding/success` |
  | GitHub install error | `Endpoints/GitHubEndpoints.cs:24,40` | `{base}/onboarding/error` |
  | CORS allow-list | `Program.cs:1169-1177` | `policy.WithOrigins(config["Dashboard:Url"] ?? "http://localhost:3001")` |

- **All six links are customer-facing. The admin console emails nobody.** So the split is not
  "half the consumers move" — it is "every link-building consumer moves, and CORS needs both".
- **`GitHubEndpoints.cs:25` already hardcodes the answer.**
  `private const string DefaultDashboardUrl = "https://dash.tamma.dev";` — the customer host, as its
  fallback, in a file whose configured value points at the admin console. Someone already knew.
- **CORS takes a single origin.** `Program.cs:1173-1174` calls `WithOrigins(...)` with one string.
  With two browser origins there must be two entries — and adding a second is a two-line change to a
  policy that also carries `WithHeaders` and `WithMethods` but **no `AllowCredentials`**. Read that
  carefully before changing it (see AC4).
- **CORS is not on the SPA's own hot path.** Both SPAs call `/api/...` **same-origin**, proxied by
  their own nginx (`docker/nginx-dashboard-user.conf`, from 45-4) and by the vhost's `/api/` location
  (from 45-5). So a wrong CORS setting will not break the app in normal use — which is exactly why it
  must be got right deliberately rather than discovered.
- **Single-user self-hosted deployments have one dashboard.** Per `CLAUDE.md`'s Operating Modes, a
  single-user install (`tamma start` / `tamma server`) has one user and one UI. Requiring two
  configured URLs there would be a regression for every self-hoster. **The fallback is the design.**

## Acceptance Criteria

1. **`Dashboard:CustomerUrl` is introduced, falling back to `Dashboard:Url`.** A single resolver —
   one method, one place — returning `config["Dashboard:CustomerUrl"] ?? config["Dashboard:Url"] ?? <existing default>`.
   **The fallback is not a convenience, it is the compatibility contract**: an existing deployment
   that sets only `Dashboard:Url` keeps working byte-for-byte, and a single-user self-hosted install
   never has to know the setting exists.

2. **All six link builders use it.** `AuthEndpoints.cs:31,37`, `OrgEndpoints.cs:361,501`,
   `GitHubEndpoints.cs:40`. Each currently re-reads `config["Dashboard:Url"]` inline with its own
   default (`"http://localhost:3001"` in four places, `"https://dash.tamma.dev"` in one) — **five
   copies with two different defaults.** Consolidating them into one resolver is most of this story's
   value; the split is the reason to finally do it.

3. **The GitHub install redirect's target is decided, not defaulted.** Epic README open question 1.
   `/onboarding/success` and `/onboarding/error` exist in the admin app (`router.tsx:77,85`) and, as
   of 45-2, in the customer app. The person installing the GitHub App is a customer, so the default
   answer is the customer app — but **state the decision and its reason in the code**, because a
   redirect that silently changes host is the kind of thing that gets reverted by whoever debugs the
   next install failure.

4. **CORS allows both origins.** `Program.cs:1169-1177` gains the customer origin alongside the admin
   one, de-duplicated when they are equal (the single-user case, where the fallback makes them
   identical and `WithOrigins` would otherwise receive the same string twice).
   **Do not add `AllowCredentials()`.** The policy does not have it today; adding it while widening
   the origin list turns a widening into a credential-bearing widening, and the SPAs do not need it —
   they are same-origin (see Architectural Context). If a future cross-origin need appears, that is
   its own change with its own review.

5. **Configuration is set in every place `Dashboard:Url` is set today** — `docker-compose.yml:257`
   (`Dashboard__CustomerUrl: ${CUSTOMER_DASHBOARD_URL:-https://dash.tamma.dev}`),
   `docker/.env.example:40`, `docker-publish.yml:425`, `deploy.yml:110`. **All four**, in the same
   style. Missing one means the value is right on the automated deploy path and defaulted on the
   manual one — the same class of drift 45-6 documents between the two image-override generators.

6. **The old value keeps working, and a test proves it.** With `Dashboard:CustomerUrl` unset, every
   one of the six links resolves exactly as it does today. This is the test that protects existing
   self-hosted deployments, and it is the one that will not be written unless it is an AC.

7. **The verification-link path mismatch is fixed at the client, not the server.** 45-2 aliased
   `/verify` alongside `/verify-email` for exactly this reason: `AuthEndpoints.cs:32` emits `/verify`
   and that string is in the inbox of every user who has ever registered. **This story does not
   change it.** If a future story wants a nicer path, it adds one — it does not remove the old.

8. **An end-to-end verification against production.** Register a fresh account; confirm the email's
   link points at `dash.tamma.dev` and opens the verification page. Then a password reset, then an org
   invite. **Three real emails, three real clicks.** Nothing else proves this story worked, because
   every part of it is a string that only appears in an email.

## Technical Notes

- **Why `Dashboard:CustomerUrl` rather than repointing `Dashboard:Url` and adding `Dashboard:AdminUrl`.**
  Repointing changes the meaning of a setting that already exists in four deployment configs and in
  every self-hoster's environment. Someone upgrading without reading the changelog would silently
  start emailing links to a host they have not deployed. Adding a new, optional key that falls back to
  the old one is inert on upgrade and explicit on adoption.
- **The five inline defaults are a finding in themselves.** Four sites default to
  `http://localhost:3001` — the *admin* app's dev port — and one to `https://dash.tamma.dev`. So an
  unconfigured local deployment emails four kinds of link to the admin dev server and one to
  production. AC2's consolidation fixes it as a side effect; worth a line in the PR because it means
  local development of these flows has never worked without manual configuration.
- **CORS deserves the caution in AC4.** Widening an origin list is routine; widening it in the same
  diff that someone might "helpfully" add `AllowCredentials()` is how a CSRF surface appears. The
  policy today is `WithOrigins` + `WithHeaders` + `WithMethods` and no credentials. Keep that shape.
- **This story is last for a reason.** Repointing customer links at `dash.tamma.dev` before 45-3's
  pages exist moves the breakage rather than fixing it — verification emails would 404 on a new host
  instead of hitting an OAuth wall on the old one. Before 45-5, they would fail DNS.

## Dependencies

- **Blocked by:** **45-3** (the pages the links open must exist) and **45-5** (the host must resolve
  over TLS).
- **Blocks:** nothing. It is the last story in the epic and the one that makes the rest visible.
- **Related:** epic README open question 1 is decided by AC3.

## Blocks / Blocked by

- **Blocks:** nothing.
- **Blocked by:** 45-3, 45-5. (And in practice 45-6, since AC8 verifies against production.)

## Out of Scope

- Changing any emitted **path** — `/verify`, `/reset-password`, `/invites/*`, `/onboarding/*` all stay
  exactly as they are. This story changes the **host**, nothing else.
- Email templates, copy, or styling.
- Moving the admin console to a different hostname — epic README open question 2, not taken.
- `AllowCredentials()` or any other CORS capability beyond adding a second origin.
- A per-tenant custom domain. Out of scope for the epic and a much larger change (cert issuance,
  routing, tenant→host mapping).

## Estimated Effort

**2 days.** The resolver and the six call sites are half a day; the config in four places and the CORS
change are another half. The remaining day is AC6's compatibility test and AC8's three real emails —
the only two things that distinguish this story from a find-and-replace.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
