# Implementation Plan — Story 45-3: The Missing Account Pages

## Scope & Deliverable

When this story is done, `packages/dashboard-user` has four new pages — forgot-password,
reset-password, invite-accept and invite-pending — bound to endpoints that already ship, the login
page links to password recovery for the first time, an invited person with no account can get from
the email to a membership, and Story 45-2's three placeholders are deleted. No server change.

## Pre-Reading

- `docs/stories/epic-45/story-45-2/…` — the routes and placeholders this story fills
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1850-1851` — the two reset routes, both anonymous
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2299-2301` — the `orgs` group's `MemberAccess` and
  `invites/accept` inside it. **Read this before designing anything.**
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` — `PasswordResetRequest`,
  `PasswordResetConfirm`: their request DTOs, their validation rules (AC4), and **whether the request
  path distinguishes a known from an unknown email** (AC1)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `AcceptInvite`'s failure shapes (AC6),
  and `:499-506` for why `/invites/pending` carries an id rather than a token
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2334` — the resend route and its policy (AC7)
- `packages/dashboard-user/src/pages/auth/VerifyEmailPage.tsx:1-89` — the token-from-query,
  POST-in-body, four-state page shape to reuse
- `packages/dashboard-user/src/pages/auth/LoginPage.tsx:90-113` — where AC2's link goes
- `packages/dashboard-user/src/guards/AuthGuard.tsx:33-36` — the `?redirect=` preservation AC5 builds on
- `packages/dashboard-user/src/api/alerts.ts:206-211` — the "UX speedup, not a security boundary"
  posture AC4 follows
- **All referenced paths exist.** NOT FOUND (this story creates them): `src/pages/auth/ForgotPasswordPage.tsx`,
  `src/pages/auth/ResetPasswordPage.tsx`, `src/pages/invites/InviteAcceptPage.tsx`,
  `src/pages/invites/InvitePendingPage.tsx`, `src/api/invites.ts`.

## Design Decisions

- **D1 — Read the server before writing any form.** Four of this story's ten ACs (1, 4, 6, 7) are
  "mirror what the endpoint actually does". Every one of them is a place where writing the obvious
  client-side thing produces a wrong contract that typechecks — which is exactly how Story 45-1's
  `violations` bug happened, in this same package, four weeks ago. **Step 1 is reading, and it
  produces written findings before any component exists.**

- **D2 — The invite-accept token crosses the auth boundary in the URL, not in storage.**
  `AuthGuard.tsx:34-35` already encodes `location.pathname + location.search` into `?redirect=`, so
  `/invites/accept?token=X` round-trips through `/login?redirect=%2Finvites%2Faccept%3Ftoken%3DX`
  with no new mechanism. The alternative — `sessionStorage` — survives a full page reload that the
  query does not, but introduces a second source of truth, a cleanup obligation, and a token sitting
  in browser storage after the flow completes.
  **Take the URL.** It reuses a shipped, tested mechanism and keeps the token's lifetime equal to the
  navigation's. The one case it does not cover is registration → email-verification → return, which
  leaves the tab: see D3.

- **D3 — Registration → verification → accept is handled by re-sending the invite context through
  the register flow, and if it cannot be, the page says so rather than silently losing it.** After
  `register`, `useAuth.tsx:91-93` notes that `/auth/me` will not return a user until the email is
  verified, and verification arrives as a *new* link in a *new* email — a different navigation
  entirely. The token cannot ride that hop in a query string we control.
  Options: (a) `sessionStorage`, which survives within the tab but not a click from an email client;
  (b) have the invite email's link work a second time — i.e. tell the user to click the invite link
  again once verified.
  **Take (b), and make it explicit copy on the page**: "verify your email, then click the invite link
  again". It requires no state, no storage and no expiry semantics we would have to invent, and the
  invite token is valid until it expires regardless. (a) can be added later if telemetry shows people
  dropping — but shipping storage-backed resume that silently fails when they open the verification
  email on their phone is worse than a clear instruction.
  **Whichever is implemented, AC5 requires the choice and its reason to be stated in the code.**

- **D4 — The reset-request response is indistinguishable, and the client must not undo that.** If
  step 1 finds the server already returns the same 200 for known and unknown addresses, the client
  renders one success state and never branches. If it finds the server *does* distinguish, that is a
  security finding — file it in `.dev/findings/`, render the indistinguishable message anyway, and do
  not let the client be the thing that leaks. The client cannot fix an oracle, but it can decline to
  amplify one.

- **D5 — `/invites/pending` is a status page, not an accept page.** `OrgEndpoints.cs:499-501` states
  the raw token cannot be derived from the stored hash. So this URL — which the API sends to the
  **inviter** — can show state and offer resend, and structurally cannot complete the invite. The
  page says that plainly. Treating it as a second accept path would produce a button that cannot
  work.

- **D6 — Four failure states, read not invented (AC6).** Expired, already-accepted, wrong-account and
  revoked are four different customer actions: wait for a new invite, do nothing, sign in as someone
  else, ask the admin. Collapsing them into "something went wrong" produces support tickets with no
  triage signal. Step 1 extracts the real shapes; if the endpoint returns fewer than four
  distinguishable outcomes, render the ones it does and note the gap rather than faking the rest.

- **D7 — Reuse `VerifyEmailPage`'s structure; do not extract a shared abstraction.** Three pages read
  a token from the query, POST it, and render four states. That is a rhyme, not a duplication worth a
  `useTokenSubmit` hook — the four states differ per page, the endpoints differ, and the failure
  vocabularies differ. Copying an 89-line shape three times is cheaper to read and change than one
  parameterized hook with three call sites and four boolean props.

## Implementation Steps

1. **Read and write down the server contracts.** Before any component: extract from
   `AuthEndpoints.cs` and `OrgEndpoints.cs` the request DTOs, the success shapes, the failure shapes
   and status codes, the password validation rules, and whether reset-request distinguishes a known
   email. **Record all of it in the PR description.** Every subsequent step cites it.
2. **Create `src/api/invites.ts`** — typed wrappers over accept, pending-lookup and resend, built on
   `apiClient` (never a bare `fetch` — see 45-1). Mirror the C# DTOs field-for-field with a citation
   comment, following 45-1's D2.
3. **Extend the auth API surface** — password-reset request and confirm. They belong beside the other
   auth calls; `useAuth.tsx` already holds login/register/logout, so put the two reset calls in a
   small `src/api/auth.ts` rather than growing the context with functions no component shares.
4. **`ForgotPasswordPage`** — one email field, one submit, one success state (D4). Loading and error
   states from `LoginPage.tsx`'s existing conventions.
5. **Link it from `LoginPage.tsx:96-107`** (AC2).
6. **`ResetPasswordPage`** — `VerifyEmailPage`'s shape: token from query, new password + confirm,
   POST, four states. Client-side rules mirror step 1's findings, pre-flight only (AC4).
7. **`InviteAcceptPage`** — the load-bearing component.
   - Authenticated → accept immediately, render per-outcome (D6), then navigate to `/`.
   - Anonymous → render sign-in / create-account, both carrying `?redirect=` with the token (D2), plus
     D3's explicit copy about clicking the invite link again after verification.
   - The chosen resume mechanism and its reason go in a file-header comment.
8. **`InvitePendingPage`** — read `?inviteId=`, show status, state plainly that this page cannot
   accept the invite (D5), and render resend only if step 1 confirms the caller can be authorized for
   `Program.cs:2334`.
9. **Wire the routes** — `src/App.tsx`. Swap the three placeholder imports for the real pages; add
   `/forgot-password`. Update the file-header route comment (`App.tsx:1-14`).
10. **Delete `src/pages/placeholders/`** entirely (AC9).
11. **Update 45-2's route table test** with `/forgot-password` (AC10), and add the new pages' own test
    files.
12. **Manual end-to-end against a local API**: register → receive verification → verify → request
    reset → receive email → reset → log in with the new password. Then: invite a second user → open
    the invite link anonymously → register → verify → click the invite link again → land in the org.
    **jsdom cannot prove D3 works. Only this can.**

## Data & Migrations

None. No schema change, no migration. All three endpoints and their tables ship today.

## Events

None emitted client-side. The server emits whatever `PasswordResetConfirm` and `AcceptInvite`
already emit; this story does not touch those paths.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `ForgotPasswordPage` — `Success_message_is_identical_for_known_and_unknown_email` | one rendered string for both — D4 |
| 2 | `ForgotPasswordPage` — `Server_error_is_surfaced_without_leaking_existence` | 500 renders a generic error |
| 3 | `LoginPage` — `Links_to_forgot_password` | the link exists — AC2, the pin that stops AC1 being unreachable |
| 4 | `ResetPasswordPage` — `Missing_token_renders_an_explanatory_state` | no POST issued |
| 5 | `ResetPasswordPage` — `Invalid_or_expired_token_renders_its_own_state` | distinct from validation failure |
| 6 | `ResetPasswordPage` — `Mismatched_confirmation_blocks_submit` | no POST issued |
| 7 | `ResetPasswordPage` — `Success_links_to_login` | |
| 8 | `InviteAcceptPage` — `Authenticated_accepts_immediately` | POST issued once, navigates to `/` |
| 9 | `InviteAcceptPage` — `Anonymous_offers_sign_in_and_register_carrying_the_token` | both hrefs contain the encoded token — D2 |
| 10 | `InviteAcceptPage` — `Expired / AlreadyAccepted / WrongAccount / Revoked` (4 cases) | four distinct rendered messages — D6 |
| 11 | `InvitePendingPage` — `States_that_it_cannot_accept_the_invite` | D5's copy present |
| 12 | `InvitePendingPage` — `Resend_is_hidden_when_the_caller_is_not_authorized` | |
| 13 | `App.test.tsx` route table | includes `/forgot-password`; all routes still render — AC10 |
| 14 | Full suite + typecheck | green, exit 0 |
| 15 | **Manual, step 12** | the two end-to-end journeys, against a real API and real emails |

Test 15 is not optional and is not automatable here. D3's mechanism spans an email client and a
browser navigation jsdom does not model.

## Definition of Done

- Four pages exist and every state in the test plan renders.
- `LoginPage` links to `/forgot-password`.
- **`src/pages/placeholders/` does not exist** (grep-checked) and none of its three files remains.
- `src/App.tsx`'s header comment matches the route tree.
- The invite-accept resume mechanism and its rationale are stated in a file-header comment (D3).
- Step 1's server-contract findings are in the PR description; AC4's password rules match them.
- No bare `fetch` anywhere in the new code (grep-checked) — all through `apiClient`.
- Step 12's two manual journeys completed and recorded.
- ~132 tests green; typecheck exit 0.
- **No file under `apps/tamma-elsa/` changed** (grep-checked).

## Dependencies & Sequencing

- **Blocked by:** 45-2. It owns `src/App.tsx`'s route tree and ships the placeholders. Do not run
  concurrently.
- **Blocks:** 45-7 — repointing `Dashboard:Url` before these pages exist relocates the breakage
  instead of fixing it.
- **Shared-edit register:** `src/App.tsx` (hand-off from 45-2), `src/pages/auth/LoginPage.tsx` (this
  story only), `src/api/` (45-1 also edits `client.ts` and `alerts.ts` — different files).
- **Not blocked by 45-4/45-5/45-6.** The deployment half can run fully in parallel; these are pages,
  those are infrastructure, and they meet only at 45-7.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **AC5 is half-built.** The authenticated path works, the anonymous path renders buttons, and nobody walks the full registration → verification → accept journey — so it ships broken in the one case it exists for. | D3 forces an explicit, stated mechanism; test 9 pins the token in both hrefs; **step 12 / test 15 is a manual end-to-end and is in the DoD.** This is the single most likely failure of this story and it gets three separate controls. |
| **The four invite failure states are invented rather than read**, so a real expired invite renders "something went wrong". | D6 and step 1. If the endpoint genuinely returns fewer than four distinguishable outcomes, render what it does and file the gap — do not fabricate branches that can never fire. |
| **The reset-request endpoint distinguishes known from unknown emails** and the client faithfully mirrors an enumeration oracle. | D4: mirror the *safe* message regardless, and file the server behaviour as a finding. The client declines to amplify. |
| **Password rules drift** between the client pre-flight and the server validator, so a customer is rejected by a rule the form said was fine. | AC4 makes the server authoritative and the client a pre-flight only — the shipped `hasPlaintextCredential` posture (`alerts.ts:206-211`). Step 1 reads the actual rules; a comment names the C# validator so the next change has a grep target. |
| **Someone "simplifies" invite-accept by making the endpoint anonymous.** | Out of Scope names it; the Technical Note explains that accepting an invite binds a membership to an identity that does not exist before authentication. It is a security change wearing a UX fix's clothes. |
| **The placeholders are not deleted** and both a placeholder and a real page exist for the same route. | AC9, the DoD grep-check, and 45-2's own comment convention pointing here. |

## Effort Breakdown

| Task | Days |
|---|---|
| Step 1 (read and document all four server contracts) | 0.5 |
| Steps 2–3 (`api/invites.ts`, `api/auth.ts`, DTO mirrors) | 0.5 |
| Steps 4–6 (forgot-password, login link, reset-password) | 0.75 |
| Step 7 (`InviteAcceptPage` — the anonymous path, D2/D3, four failure states) | 1.25 |
| Step 8 (`InvitePendingPage`) | 0.25 |
| Steps 9–11 (routes, delete placeholders, tests) | 0.5 |
| Step 12 (two manual end-to-end journeys) + review | 0.25 |
| **Total** | **4.0** |

A third of the story is one component. That is the honest shape: three of these pages are forms, and
one of them has to carry a secret across an authentication boundary it does not control.
