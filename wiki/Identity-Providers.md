# Per-Tenant Identity Providers (Epic 33 — deferred)

**Status**: **deferred — forward-looking stub, not scoped in detail** (written 2026-04-21).
**Layer**: activates post-launch when trigger conditions fire.
**Depends on**: Epic 18 (user + tenant model), Epic 28 (tenant lifecycle), Epic 31 (platform abstraction — sign-in is orthogonal to API access so 31 doesn't block 33 or vice versa).
**Source**: `docs/stories/epic-33/README.md`.

## Purpose

Allow each tenant to configure their **own** identity provider (SAML 2.0, OIDC, LDAP/Active Directory) so their users sign into Tamma via that IdP rather than through Tamma's built-in email/password or shared GitHub OAuth.

Typical asks:

- Enterprise customer requires SAML SSO tied to Okta / Azure AD / Entra / Google Workspace / Ping / Duo / Auth0.
- Compliance framework (SOC 2 Type II managed-identity control, ISO 27001, FedRAMP moderate) forces SSO.
- Tenant admin needs automatic user provisioning via SCIM 2.0 / directory sync.
- Tenant wants to bind IdP group membership to tenant roles inside Tamma.

## Why this is not being scoped in detail now

The near-term path is already covered:

- **Built-in user management** — Stories 18-7 + 18-8 ship tenant-admin invite / list / role / remove / audit for the local user flow.
- **GitHub OAuth sign-in** — live; handles self-service sign-in for GitHub-connected users.
- **Password reset + email verification** — Stories 18-1 / 18-6 handle the built-in auth flow.

That combination covers every current user until a paying enterprise customer demands SSO. Scoping IdP integration is a **big** effort (100–400h depending on tier) and is the wrong place to invest until trigger conditions fire.

## Trigger conditions

Activate scoping when **any** of the following is true:

1. **First enterprise customer commits** with SSO as a required contract term.
2. **Compliance auditor flags** lack of SSO as a finding on a customer's audit (SOC 2, ISO 27001, HIPAA, PCI DSS, FedRAMP).
3. **≥5 tenants independently ask for SSO / SAML / OIDC** in support tickets within a rolling 60-day window.
4. **SCIM directory sync** becomes a routine sales objection.
5. **A product decision** to target the enterprise tier (e.g. a "Tamma Enterprise" plan launch) where SSO is table stakes.

Until one of those fires, this epic stays a one-page placeholder.

## Approximate scope (three pre-scoped tiers)

Pick one at activation based on trigger conditions.

### A. Lean — OIDC-only, single-IdP-per-tenant (~100h, 4–5 stories)

- Tenant admin uploads OIDC discovery URL + client ID / secret.
- Users sign in at `/login?tenant={slug}` — Tamma redirects to the tenant's IdP.
- JIT user provisioning — first login creates a tenant member with role `member` (promotable via 18-8).
- No SCIM, no SAML, no group → role binding.
- Handles Okta / Azure AD / Google / Auth0 — all ship OIDC.

**Best fit when**: first enterprise customer is cloud-native + uses a modern IdP.

### B. Full — SAML 2.0 + OIDC + group → role binding (~250h, 10–12 stories)

- Everything in (A).
- SAML 2.0: metadata XML upload, SP-initiated + IdP-initiated flows, sig verification, encryption.
- Attribute mapping UI — tenant admin maps IdP claims to Tamma display name, email, role.
- Group → role binding — IdP group membership resolves to Tamma role on each login.
- Session binding — Tamma session tied to IdP session; IdP-triggered logout invalidates Tamma session.
- SLO (Single Logout) — Tamma sends LogoutRequest on user-initiated logout if IdP supports it.

**Best fit when**: enterprise customer with non-cloud IdP, ISO 27001 / SOC 2 with group-based access.

### C. Full + LDAP / Active Directory bind (~400h, 15–18 stories)

- Everything in (B).
- LDAP bind support — tenant runs a LDAP connector, Tamma binds + verifies credentials on each login. Group lookup via LDAP.
- SCIM 2.0 directory sync — IdP pushes user/group changes; Tamma reflects them without user login.
- AD-specific quirks (UPN vs sAMAccountName, nested groups, stale tokens).

**Best fit when**: government / regulated / air-gapped tenants.

## Reference architectures (read before scoping)

Three industry analogues that have already solved multi-tenant-IdP at B2B SaaS scale:

| Vendor | What they do | What we'd learn |
|--------|--------------|-----------------|
| **[Auth0 Organizations](https://auth0.com/docs/manage-users/organizations)** | Per-org connection picker; each org binds to its own IdP; user's effective identity is `(org, sub)` | Data model for `(tenant, external_identity)`; connection-picker UX; policy per org |
| **[Clerk multi-tenant](https://clerk.com/docs/authentication/social-connections/overview)** | Per-org SSO + SAML with metadata upload; magic-link fallback | SAML metadata ingestion UX; JIT shape; session/cookie scoping across orgs |
| **[WorkOS Directory Sync / SSO](https://workos.com/docs/sso/overview)** | SSO-in-a-box: one API over ~40 IdP implementations; SCIM directory sync with realtime change-stream | Abstraction surface for "one API covers 40 IdPs"; SCIM event model; test harness strategy |

## Relationship to Epic 31

Epic 31 ([Multi Git Platform Support](Multi-Git-Platform)) splits GitHub-App-as-sign-in from GitHub-App-as-API-access. After Epic 31:

- **Sign-in** stays Tamma-built-in (email/password + GitHub OAuth) until Epic 33 activates.
- **API access to each tenant's repos** is via their configured git platform (GitHub / Gitea / Forgejo / GitLab), independent of how the user signed in.

A tenant user could eventually sign in via their corporate SAML IdP (Epic 33) while Tamma operates on their repos via Gitea (Epic 31 driver). The two concerns are orthogonal — neither epic blocks the other.

## Not in scope

- **Multiple IdPs per tenant** — one-IdP-per-tenant is good enough for 99% of cases.
- **Social provider federation on top of tenant IdPs** — tenant IdP is the authority.
- **Custom domain login pages** (`login.tenant.example.com`) — Layer 6 marketing / brand polish.
- **FIDO2 / Passkey** as sole factor — add-on to any tier above.

## Related

- See also: [Epic 33 detail](Epics/Epic-33-Per-Tenant-IdP.md)
- [Multi Git Platform](Multi-Git-Platform) — orthogonal API-access plane
- [Security → API key hashing](Security#api-key-hashing) — built-in auth today
- Source: [`docs/stories/epic-33/README.md`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-33)
- Layer placement: [`docs/stories/plans/epic-31-33-placement.md`](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-31-33-placement.md)
- Tenant user management (built-in path): [`docs/stories/plans/tenant-user-mgmt-audit.md`](https://github.com/meywd/tamma/blob/main/docs/stories/plans/tenant-user-mgmt-audit.md)
