# Epic 33: Per-Tenant Identity Providers (deferred / post-launch)

**Status:** **Deferred — forward-looking stub, not scoped in detail** (written 2026-04-21)
**Stories:** none yet — activates when trigger conditions fire
**Layer:** will slot into Layer 5 or later when activated
**Depends on:** Epic 18 (user + tenant model), Epic 28 (tenant lifecycle), Epic 31 (platform abstraction — sign-in stays orthogonal to API access)

> **Overview**: [Identity Providers](Identity-Providers) — root-level topic page with the deferral rationale, trigger conditions, and approximate scope tiers.

## Purpose

Allow each tenant to configure their **own** identity provider (SAML 2.0, OIDC, LDAP/Active Directory) so their users sign into Tamma via that IdP rather than through Tamma's built-in email/password or shared GitHub OAuth.

Typical asks that drive this epic:

- Enterprise customer requires SAML SSO tied to corporate IdP (Okta, Azure AD / Entra, Google Workspace, Ping, Duo, Auth0)
- Compliance framework (SOC 2 Type II with managed-identity control, ISO 27001, FedRAMP moderate) forces SSO
- Tenant admin requires automatic user provisioning via SCIM 2.0 / "Directory Sync" — no per-user invite flow
- Tenant wants to bind group membership in their directory to tenant roles inside Tamma

## Why this is not being scoped in detail now

The near-term path is covered by:

- **Built-in user management** — Stories 18-7 + 18-8 ship tenant-admin invite / list / role / remove / audit for the local user flow
- **GitHub OAuth sign-in** — already live; handles self-service sign-in for GitHub-connected users
- **Password reset + email verification** — Stories 18-1 / 18-6 handle the built-in auth flow

That combination covers every current user until a paying enterprise customer demands SSO.

Scoping IdP integration is a **big** effort (100–400h depending on tier — see "Approximate scope" below) and is the wrong place to invest until the trigger conditions fire. Doing it now would ship a feature nobody is asking for against real-world IdP quirks we haven't encountered.

## Trigger conditions that activate this epic

Activate scoping when **any** of the following is true:

1. **First enterprise customer commits** to Tamma with SSO as a required contract term
2. **Compliance auditor flags** lack of SSO as a finding on a customer's audit (SOC 2, ISO 27001, HIPAA, PCI DSS, FedRAMP)
3. **≥5 tenants independently ask for SSO / SAML / OIDC** in support tickets within a rolling 60-day window
4. **SCIM directory sync** becomes a routine sales objection
5. **A product decision** to target the enterprise tier (e.g. a "Tamma Enterprise" plan launch) where SSO is table stakes

Until one of those fires, this epic stays a one-page placeholder.

## Stories

| Story | Title | Status |
|-------|-------|--------|
| (none scoped yet) | Activate when trigger conditions fire | Deferred |

## Approximate scope (3 pre-scoped tiers)

Pick one tier based on which trigger condition fires:

### A. Lean — OIDC-only, single-IdP-per-tenant (~100h, 4-5 stories)

- Tenant admin uploads OIDC discovery URL + client ID / secret in the dashboard
- Users sign in with "Sign in with {IdP name}" button at `/login?tenant={slug}` — Tamma redirects to the tenant's IdP
- JIT user provisioning — first login creates a tenant member with role `member` (promotable via 18-8)
- No SCIM, no SAML, no group → role binding
- Handles Okta / Azure AD / Google / Auth0 / any modern IdP — all ship OIDC

**Best fit when**: first enterprise customer is cloud-native + uses a modern IdP.

### B. Full — SAML 2.0 + OIDC + group → role binding (~250h, 10-12 stories)

- Everything in (A)
- SAML 2.0 support: metadata XML upload, SP-initiated + IdP-initiated flows, sig verification, encryption
- Attribute mapping UI — tenant admin maps IdP claims to Tamma display name, email, role claim
- Group → role binding — IdP group membership resolves to Tamma tenant role on each login
- Session binding — Tamma session tied to IdP session; IdP-triggered logout invalidates Tamma session
- SLO (Single Logout) — Tamma sends `LogoutRequest` on user-initiated logout if IdP supports it

**Best fit when**: enterprise customer with non-cloud IdP, ISO 27001 / SOC 2 requirement with group-based access.

### C. Full + LDAP / Active Directory bind (~400h, 15-18 stories)

- Everything in (B)
- LDAP bind support — tenant runs an LDAP connector, Tamma binds + verifies credentials on each login. Group lookup via LDAP
- SCIM 2.0 directory sync — IdP pushes user/group changes; Tamma reflects them without user login
- AD-specific quirks (UPN vs sAMAccountName, nested groups, stale tokens)

**Best fit when**: government / regulated / air-gapped tenants. LDAP is typically a deal-breaker or deal-maker; if not needed, skip.

## Reference architectures (read these before scoping)

Three industry analogues that have already solved the multi-tenant-IdP problem at B2B SaaS scale:

| Vendor | What they do | What we'd learn |
|--------|--------------|-----------------|
| **[Auth0 Organizations](https://auth0.com/docs/manage-users/organizations)** | Per-organization connection picker; each org binds to its own IdP; user's effective identity is `(org, sub)` | Data model for `(tenant, external_identity)` mapping; connection-picker UX; policy per org |
| **[Clerk multi-tenant](https://clerk.com/docs/authentication/social-connections/overview)** | Per-organization SSO + SAML IdP with metadata upload; magic-link fallback | SAML metadata ingestion UX; JIT provisioning shape; session/cookie scoping across orgs |
| **[WorkOS Directory Sync / SSO](https://workos.com/docs/sso/overview)** | SSO-in-a-box: one API over ~40 IdP implementations; SCIM directory sync with realtime change-stream | Abstraction surface for "one API covers 40 IdPs"; SCIM event model; test harness strategy |

If scoping activates, reading these docs before writing the story set is non-negotiable — the quirks (NameID formats, attribute mapping, metadata refresh semantics, SLO vs session invalidation) are where every greenfield SAML integration burns weeks of budget.

## Architecture / key decisions (forward-looking)

These are the design intents to lock in before story-writing begins:

1. **Sign-in plane stays orthogonal to API-access plane**. A tenant signed in via corporate SAML IdP (Epic 33) can still operate on repos via Gitea / GitLab (Epic 31 driver). The two concerns do not block each other.
2. **One IdP per tenant for v1**. Multi-IdP-per-tenant is a post-MVP ask; 99% of cases are single-IdP.
3. **Local accounts as fallback** stay forever — Tamma's built-in email/password + GitHub OAuth (Epics 16, 18) are the universal login. Per-tenant IdP is opt-in per tenant.
4. **JIT user provisioning** is the default for tier A; SCIM is tier C only. JIT creates tenant member with role `member`; role promotion via tenant-admin UI.
5. **No per-tenant custom domain login pages** in MVP (`login.tenant.example.com`). Layer 6 marketing/brand polish, not an identity concern.

## Dependencies

**Upstream**:
- [Epic 18](Epic-18-User-Auth.md) — user + tenant model; Stories 18-7/18-8 ship the local user-management path users fall back to when no IdP is configured
- [Epic 28](Epic-28-DB-Per-Tenant.md) — tenant lifecycle hooks
- [Epic 31](Epic-31-Multi-Git-Platform.md) — platform abstraction (sign-in stays orthogonal so 31 doesn't block 33 or vice versa)

**Downstream**: none — terminal epic.

## Not in scope (for eventual scoping, too)

- **Multiple IdPs per tenant** — one-IdP-per-tenant is good enough for 99% of cases; multi-IdP is post-MVP
- **Social provider federation on top of tenant IdPs** — out of scope; tenant IdP is the authority
- **Custom domain login pages** (`login.tenant.example.com`) — Layer 6 marketing/brand polish
- **FIDO2 / Passkey** as the sole factor — add-on to any tier above

## Open questions

These are gating decisions for when scoping activates — not answered now:

1. **Build vs buy**: do we build SAML/OIDC against Microsoft.IdentityModel.* directly, or use WorkOS / Auth0 / Stytch as an abstraction layer? Each shaves 50-100h off tier B/C but adds vendor cost + lock-in.
2. **Tenant onboarding UX for SAML metadata XML**: upload-file vs paste-XML vs URL-fetch. Tier A (OIDC) just needs URL; tier B (SAML) is the painful part.
3. **Session model**: does Tamma's session live on top of the IdP's session (tier B SLO), or is it independent (logout triggers IdP logout but not vice-versa)? Affects compliance posture.
4. **Trigger-condition watchlist**: should we instrument support-ticket tagging for SSO mentions to detect trigger #3 automatically? Cheap to add now if we decide to.

## Cross-reference

- Tenant user management (built-in path): `docs/stories/plans/tenant-user-mgmt-audit.md`
- Layer placement across Epics 29 / 30 / 31 / 33: `docs/stories/plans/epic-31-33-placement.md`
- Unified RBAC reference: `docs/stories/rbac-unified-model.md`

## Story files

[Epic 33 README on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-33)

## Change log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-21 | 0.1.0 | Initial stub (forward-looking, deferred) | Planning sweep |

---

_Last updated: 2026-04-21_
