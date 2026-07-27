# Implementation Plan — Story 45-7: The `Dashboard:Url` Split

## Scope & Deliverable

When this story is done, every URL `Tamma.Api` emails or redirects a customer to is built from one
resolver reading `Dashboard:CustomerUrl` with a fallback to `Dashboard:Url`, that value is
`https://dash.tamma.dev` on every deployment path, CORS accepts both dashboard origins without
gaining credential support, and a deployment that sets only `Dashboard:Url` behaves exactly as it
does today. Five inline config reads with two different defaults collapse into one method.

## Pre-Reading

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:29-39` — `BuildVerificationUrl`,
  `BuildResetUrl`; both default to `http://localhost:3001`
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:358-364` and `:499-506` — the two invite
  URLs; both default to `http://localhost:3001`; `:499-501` explains the `inviteId`-not-token design
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:22-25, 32-45` — the install callback;
  **defaults to `https://dash.tamma.dev`**, unlike the other four
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1168-1178` — the CORS policy. Note: `WithOrigins` +
  `WithHeaders` + `WithMethods`, and **no `AllowCredentials`**
- `docker/docker-compose.yml:257` — `Dashboard__Url: ${DASHBOARD_URL:-https://app.tamma.dev}`
- `docker/.env.example:40`, `.github/workflows/docker-publish.yml:425`,
  `.github/workflows/deploy.yml:110` — the other three places the value is set
- `docs/stories/epic-45/README.md` — D3
- `docs/stories/epic-45/story-45-2/…` — the `/verify` alias this story relies on
- `CLAUDE.md` — Operating Modes; why the fallback matters for single-user installs
- **All referenced paths exist.** NOT FOUND (this story creates it): the resolver's home — a small
  static helper class, sited per D2.

## Design Decisions

- **D1 — Add `Dashboard:CustomerUrl`; do not repoint `Dashboard:Url`.** Repointing changes the meaning
  of a key that already exists in four deployment configs and in every self-hoster's environment.
  Someone upgrading without reading a changelog would silently begin emailing links to a host they
  have never deployed — a failure that only appears in a customer's inbox and only for accounts
  created after the upgrade. A new optional key with a fallback is **inert on upgrade** and explicit
  on adoption. That asymmetry decides it.

- **D2 — One resolver, not five inline reads.** Today `config["Dashboard:Url"]` is read at five sites
  with **two different defaults** — four use `http://localhost:3001` (the *admin* app's dev port) and
  `GitHubEndpoints.cs:25` uses `https://dash.tamma.dev`. That is not a style problem: it means an
  unconfigured local deployment emails four kinds of link to the admin dev server and one to
  production, so local development of the verification and invite flows has never worked without
  manual configuration. Site the resolver where all three endpoint classes can reach it without a new
  dependency edge, and give it one default. Note the discrepancy in the PR — it is a finding, not just
  a tidy-up.

- **D3 — The fallback chain is `CustomerUrl → Url → default`, and the default is the customer host.**
  `GitHubEndpoints.cs:25` already chose `https://dash.tamma.dev` as its fallback, which is the correct
  answer for a link-building default. The four `http://localhost:3001` defaults are wrong for their
  purpose (that port is the admin container) and are replaced.

- **D4 — CORS gains a second origin and nothing else.** `WithOrigins` takes params, so two strings is
  a one-line change. **Explicitly do not add `AllowCredentials()`.** The policy has never had it, both
  SPAs call `/api/...` same-origin through their own nginx (45-4, 45-5), and adding it in the same
  diff as an origin widening converts a routine change into a credential-bearing cross-origin
  surface. De-duplicate when the two resolve equal — the single-user case, where the fallback makes
  them the same string and `WithOrigins("x","x")` is at best noise.

- **D5 — The GitHub install redirect targets the customer app, decided rather than defaulted (AC3).**
  The person installing the GitHub App is a customer completing onboarding, and 45-2 built
  `/onboarding/success` and `/onboarding/error` in the customer app for this reason. **Write the
  decision and its reason as a comment at `GitHubEndpoints.cs`**, because a redirect that changes
  host is exactly what gets reverted by whoever debugs the next failed install. If the product owner
  answers "admin" to open question 1, this is the one AC that changes — and it changes to a second
  named config key, not back to `Dashboard:Url`.

- **D6 — No emitted path changes.** `/verify`, `/reset-password`, `/invites/accept`,
  `/invites/pending`, `/onboarding/success`, `/onboarding/error` all stay. 45-2 aliased `/verify`
  client-side precisely so this story would not have to touch a string that is already in every
  registered user's inbox. Host only.

## Implementation Steps

1. **Create the resolver.** One static helper —
   `CustomerDashboardUrl(IConfiguration) => (config["Dashboard:CustomerUrl"] ?? config["Dashboard:Url"] ?? "https://dash.tamma.dev").TrimEnd('/')`.
   Keep `TrimEnd('/')`, which four of the five existing sites already do; the fifth
   (`GitHubEndpoints.cs:40`) does not, and normalizing in one place removes a latent double-slash.
2. **Repoint `AuthEndpoints.cs:29-33` and `:35-39`.** Bodies become
   `$"{CustomerDashboardUrl(config)}/verify?token=…"` and `/reset-password?token=…`. **Paths
   unchanged** (D6).
3. **Repoint `OrgEndpoints.cs:361` and `:501`.**
4. **Repoint `GitHubEndpoints.cs:40`;** delete the now-redundant `DefaultDashboardUrl` const at `:25`
   (its value moves into the resolver) and add D5's decision comment.
5. **Update CORS** — `Program.cs:1169-1177`. Resolve both, de-duplicate, pass both to `WithOrigins`.
   Add a comment naming the two dashboards. **Do not touch `WithHeaders`, `WithMethods`, or add
   `AllowCredentials`** (D4).
6. **Set the config in all four places** (AC5):
   - `docker/docker-compose.yml:257` — add
     `Dashboard__CustomerUrl: ${CUSTOMER_DASHBOARD_URL:-https://dash.tamma.dev}` beside the existing
     line.
   - `docker/.env.example:40` — add `CUSTOMER_DASHBOARD_URL=https://dash.tamma.dev` with a comment
     distinguishing it from `DASHBOARD_URL`.
   - `.github/workflows/docker-publish.yml:425` and `.github/workflows/deploy.yml:110` — add
     `"CUSTOMER_DASHBOARD_URL=https://dash.tamma.dev"` to each `.env` generator.
   **All four.** One omission means the value is right on the automated deploy and defaulted on the
   manual one.
7. **Write the compatibility test (AC6).** With `Dashboard:CustomerUrl` unset and `Dashboard:Url` set
   to a fixture value, assert all six builders produce today's exact strings. Then set
   `CustomerUrl` and assert all six move. Then unset both and assert the default.
8. **Write the CORS tests.** Two distinct values → two origins. Equal values (the single-user
   fallback case) → one. Assert `AllowCredentials` is **not** set — a test that pins the absence of a
   capability, which is the only way an accidental addition gets caught.
9. **Deploy and verify against production (AC8).** Three real journeys, three real emails:
   register → verification link → does it point at `dash.tamma.dev` and open the page; password reset
   → same; org invite → same. **Record each email's actual URL in the PR.**

## Data & Migrations

None. Configuration only.

## Events

None.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `Verification_url_uses_customer_url_when_set` | `{CustomerUrl}/verify?token=` |
| 2 | `Verification_url_falls_back_to_dashboard_url` | **byte-identical to today's output** — AC6, the upgrade-safety pin |
| 3 | `Reset_url_uses_customer_url` / `…falls_back` | both halves |
| 4 | `Invite_accept_url_uses_customer_url` / `…falls_back` | both halves |
| 5 | `Invite_pending_url_uses_customer_url` / `…falls_back` | both halves |
| 6 | `Github_install_redirect_uses_customer_url` | D5's decision |
| 7 | `All_builders_use_the_default_when_neither_key_is_set` | one default across all six — the D2 discrepancy is gone |
| 8 | `Trailing_slash_is_normalized` | `https://dash.tamma.dev/` and `…dev` produce identical URLs |
| 9 | `Cors_allows_both_origins_when_they_differ` | two entries |
| 10 | `Cors_deduplicates_when_they_are_equal` | one entry — the single-user case |
| 11 | `Cors_does_not_allow_credentials` | the absence pin — D4 |
| 12 | **Manual, step 9** | three real emails, three real clicks, URLs recorded |

Test 2 is the one that protects every existing deployment. Test 11 is the one that stops a routine
widening becoming a credential-bearing one.

## Definition of Done

- One resolver; **`config["Dashboard:Url"]` appears in exactly one place** in the endpoint layer
  (grep-checked) — down from five, with two defaults, today.
- All six links use it; **no emitted path changed** (grep-checked against the six strings) — D6.
- CORS accepts both origins, de-duplicated, with **no `AllowCredentials`** (grep-checked).
- `CUSTOMER_DASHBOARD_URL` / `Dashboard__CustomerUrl` set in **all four** deployment configs.
- Tests 1–11 green, including the fallback pin and the credentials-absence pin.
- Step 9's three journeys recorded in the PR **with the actual URLs from the emails**.
- The five-inline-defaults discrepancy noted in the PR as a finding (D2).
- D5's decision comment present at `GitHubEndpoints.cs`.

## Dependencies & Sequencing

- **Blocked by:** **45-3** (the pages the links open) and **45-5** (the host must resolve over TLS).
  In practice also **45-6**, since AC8 verifies against a deployed production.
- **Blocks:** nothing. Last story in the epic.
- **Shared-edit register:** `docker/docker-compose.yml` (also 45-5 — different line), `deploy.yml` and
  `docker-publish.yml` (also 45-6 — different sections of the same files; sequence 45-6 first).
  `docker/.env.example` is this story's alone.
- **Decides** epic README open question 1 (AC3/D5).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The switch is thrown before the pages exist**, relocating the breakage: verification emails 404 on a new host instead of hitting an OAuth wall on the old one — and looking more broken, because a 404 reads as "this product is gone". | The dependency on 45-3 and 45-5 is stated in the story, this plan and the execution plan. It is the reason this story is last. |
| **Only some of the four config sites are updated**, so the value is right on the automated deploy and defaulted on the manual one — the exact drift class 45-6 documents between the two image-override generators. | AC5 and the DoD both say "all four"; step 6 enumerates them by file and line. |
| **Someone adds `AllowCredentials()`** while widening the origin list, turning a routine change into a cross-origin credential surface. | D4 states it, AC4 states it, test 11 pins the absence, and the DoD greps for it. Four controls on one line because the failure is a security regression that no functional test would notice. |
| **An existing self-hosted deployment breaks on upgrade** because the fallback is missing or wrong. | D1 makes the fallback the design rather than a nicety; test 2 asserts byte-identical output when only the old key is set. |
| **The GitHub install redirect change is reverted** by whoever debugs the next failed install, since a host change is invisible in a stack trace. | D5 requires the decision and its reason as a comment in `GitHubEndpoints.cs`, not only in this document. |
| **AC8 is skipped** because the unit tests are green and the change "obviously works". Every part of this story is a string that appears only in an email — unit tests assert the string, not that the email carries it. | Step 9 and test 12 are in the DoD, and the PR must carry the actual URLs from three real emails. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–4 (resolver, five call sites, delete the redundant const) | 0.5 |
| Step 5 (CORS, carefully) | 0.25 |
| Step 6 (four deployment configs) | 0.25 |
| Steps 7–8 (compatibility pin, CORS tests incl. the absence pin) | 0.5 |
| Step 9 (three production journeys, three real emails) + review | 0.5 |
| **Total** | **2.0** |

The code is half a day. The rest is proving that an existing deployment does not change and that three
emails now point somewhere a customer can actually use.
