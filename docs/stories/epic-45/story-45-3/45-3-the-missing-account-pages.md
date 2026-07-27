# Story 45-3: The Missing Account Pages — Password Reset, Invite Accept, Invite Pending

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

As a **customer who has forgotten my password, or who has been invited to an organization**,
I want a page that completes the flow the email started,
So that account recovery and team onboarding work — the two journeys where a dead end costs a
customer rather than annoying one.

## Priority

P0 — the three pages Story 45-2 stubbed. Password reset is the one that decides whether a locked-out
customer becomes a support ticket or a churn event; invite accept is the one that decides whether a
SaaS product can have more than one user per tenant.

## Architectural Context (READ FIRST)

- **All three backends exist and are unchanged by this story.**
  - `POST /api/v1/auth/password-reset/request` — `Program.cs:1850`, **anonymous** (in the
    `/api/v1/auth` group at `:1838`, which carries no group-level authorization).
  - `POST /api/v1/auth/password-reset/confirm` — `Program.cs:1851`, **anonymous**.
  - `POST /api/v1/orgs/invites/accept` — `Program.cs:2301`, in the `orgs` group at `:2299` which is
    `.RequireAuthorization("MemberAccess")`. **The invitee must already be signed in.**
- **That last fact is the design problem in this story, and it is not obvious.** An invited person
  arrives from an email at `/invites/accept?token=…` and may have no account at all. The accept
  endpoint will 401 them. So the page must: detect anonymity, send them to register *or* login while
  preserving the token, and resume the accept on return. `AuthGuard.tsx:34-35` already preserves
  `pathname + search` into `?redirect=`, so the machinery is there — but nothing has ever exercised
  it with a token that must survive a registration *and* an email verification.
- **The API emits these paths and they are durable.** `AuthEndpoints.cs:36-39` →
  `{Dashboard:Url}/reset-password?token=`; `OrgEndpoints.cs:361-362` →
  `{Dashboard:Url}/invites/accept?token=`; `OrgEndpoints.cs:501-502` →
  `{Dashboard:Url}/invites/pending?inviteId=`. Story 45-2 mounted routes at all three; this story
  fills them. **Do not change the server's paths** — the same reasoning as 45-2's D2.
- **`/invites/pending` takes an `inviteId`, not a token.** `OrgEndpoints.cs:499-502` explains why: the
  stored value is a hash and the raw token cannot be recovered. So the pending page identifies an
  invite it cannot itself accept — it is a status view for the *inviter*, not an accept flow for the
  invitee. Read the endpoint before designing the page; the two invite URLs are not two halves of one
  journey.
- **The reset flow has two halves and only one is linked.** The email carries the *confirm* half
  (`/reset-password?token=`). The *request* half — "I forgot my password, email me a link" — has no
  entry point anywhere: `LoginPage.tsx:96-107` offers GitHub sign-in and a link to `/register`, and
  nothing else. A confirm page with no request page is a flow only reachable by someone who already
  has the email they cannot get.
- **Story 45-2 shipped three placeholders that this story deletes.** They live in
  `src/pages/placeholders/` and each carries a `// Story 45-3 replaces this file` comment. Deleting
  them is in this story's Definition of Done.

## Acceptance Criteria

1. **`ForgotPasswordPage` at `/forgot-password`.** Email field, submit to
   `POST /api/v1/auth/password-reset/request`, and a success state that is **identical whether or not
   the address exists** — "if that address has an account, we have sent a link". Anything that
   distinguishes the two is an account-enumeration oracle. Check what the endpoint returns and mirror
   its posture; if the server already returns an indistinguishable 200, say so and do not add a
   client-side branch that reintroduces the leak.

2. **`LoginPage` links to it.** `LoginPage.tsx:96-107` gains a "Forgot your password?" link. Without
   this, AC1 is a page nobody can reach, which is the exact defect this epic exists to fix.

3. **`ResetPasswordPage` at `/reset-password`.** Reads `?token=` (the `VerifyEmailPage.tsx:17`
   pattern), takes a new password with a confirmation field, submits to
   `POST /api/v1/auth/password-reset/confirm`. Distinct rendered states for: missing token, invalid
   or expired token (whatever the endpoint's failure shape is — read it), validation failure, and
   success-with-a-link-to-`/login`.

4. **Password rules are read from the server's, not invented.** Check `AuthEndpoints`' registration
   and reset-confirm validation for the actual minimum length and character rules and mirror them
   client-side as a pre-flight only. **The server stays authoritative** — this is the
   `hasPlaintextCredential` posture already established in this package
   (`api/alerts.ts:206-211`: "a UX speedup, not a security boundary"). Do not let the two disagree.

5. **`InviteAcceptPage` at `/invites/accept` handles the anonymous case.** On arrival with a token:
   - If authenticated → call `POST /api/v1/orgs/invites/accept`, then land the user on `/`.
   - If anonymous → render a page that names the organization if it can, and offers **Sign in** and
     **Create an account**, both preserving the token so the accept resumes after auth.
   - After a *registration*, the user must verify their email before `/api/auth/me` returns a
     user — so the resume must survive that hop too. **State explicitly in the implementation which
     mechanism carries the token** (the `?redirect=` query the guard already uses, or `sessionStorage`)
     and why. This is the AC most likely to be quietly half-built.

6. **Accept failures are distinguishable.** Expired, already-accepted, wrong-account (the invite was
   for a different email), and revoked each render a different message. Read `OrgEndpoints.AcceptInvite`
   for the actual failure shapes rather than inventing four. A single "something went wrong" here
   generates support tickets that cannot be triaged.

7. **`InvitePendingPage` at `/invites/pending`.** Reads `?inviteId=`, shows the invite's status, and —
   per the endpoint's own comment at `OrgEndpoints.cs:499-501` — makes clear that this page **cannot
   accept the invite**, because the raw token is not recoverable from the stored hash. It offers
   resend where the API supports it (`POST /api/v1/orgs/{tenantId}/invites/{inviteId}/resend`,
   `Program.cs:2334`, gated — check the policy before rendering the button).

8. **Every state of every page is tested.** Success, each named failure, missing/invalid token,
   anonymous-vs-authenticated for the accept page. These are the pages a customer reaches at their
   least patient moment and they are the pages with the least manual QA, because reproducing them
   requires a real email.

9. **The three placeholder files are deleted.**
   `src/pages/placeholders/PasswordResetPlaceholder.tsx`,
   `InviteAcceptPlaceholder.tsx` and `InvitePendingPlaceholder.tsx` are removed, and
   `src/pages/placeholders/` no longer exists. Grep-checked in review.

10. **The route table test from 45-2 still passes and covers the new paths.** `/forgot-password` is a
    new route and must be added to the table — the pin only works if it is kept current.

## Technical Notes

- **The `MemberAccess` gate on invite-accept is the whole reason this story is four days rather than
  two.** Every other page in this app is either fully public (`/login`, `/register`) or fully guarded.
  The accept page is the only one that must work *both* ways and hand a secret across an
  authentication boundary — potentially across a registration and an email verification. Budget the
  time there, not on the forms.
- **Do not solve AC5 by making the endpoint anonymous.** It is `MemberAccess` for a reason: accepting
  an invite binds a membership row to a user identity, and there is no user identity to bind before
  authentication. Changing the server here would be a security change dressed as a UX fix.
- **AC1's enumeration posture is not optional.** A password-reset request form that says "no account
  with that email" is a bulk email-validity oracle. If the server currently distinguishes, that is a
  finding to file — not something for the client to paper over, and not something for the client to
  make worse.
- The reset and verify pages are structurally the same shape as `VerifyEmailPage.tsx` (read `?token=`,
  POST it in a body, render four states). Reuse its structure; it is 89 lines and already tested.

## Dependencies

- **Blocked by:** **45-2** — it declares the routes and ships the placeholders this story replaces.
  Running them concurrently means two people editing `src/App.tsx`'s route tree.
- **Blocks:** **45-7**, which repoints `Dashboard:Url` at the customer app. Repointing before these
  pages exist moves the breakage to a new host rather than fixing it — the verification and reset
  emails would then 404 on `dash.tamma.dev` instead of hitting an OAuth wall on `app.tamma.dev`.
- **Server-side:** none. All three endpoints ship today.

## Blocks / Blocked by

- **Blocks:** 45-7.
- **Blocked by:** 45-2.

## Out of Scope

- Any change to `AuthEndpoints` or `OrgEndpoints` — including making invite-accept anonymous.
- Sending, templating or styling the emails themselves. This story owns the pages the links open.
- Multi-factor auth, password strength meters, breach-list checks. AC4 mirrors the server's existing
  rules and nothing more.
- An organization-switcher or a members-management screen. Accepting an invite lands the user on `/`;
  managing the org they joined is a separate product surface.
- Social/OAuth account linking on the reset path.

## Estimated Effort

**4 days.** Two forms and a status page would be two. The extra two are AC5 and AC6: carrying a token
across an authentication boundary that may include a registration *and* an email verification, and
then rendering four genuinely distinct failure states read from the server rather than guessed. Both
are the parts that are invisible in a demo and decisive in production.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
