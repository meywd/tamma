# Finding 014: `POST /orgs/:id/invites` — Weak Guid Token, No Email, Raw Token in Response Body

**Scope**: orgs
**Severity**: P1 (feature broken)
**Status**: Behavioral drift + scope loss
**Estimated port effort**: 4h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: 549f10d
- **Notes**: Token is now `RandomNumberGenerator.GetBytes(32)` hex-encoded → 256 bits entropy (64 hex chars). TTL reduced from 7 days to 72 hours. Role validated against `{owner, admin, member}` whitelist → 400. Path-tenant admin+ enforced (filter + handler). Email dispatch wired via existing `IEmailService` + new `EmailTemplates.TenantInviteEmail` template (HTML + plain text, encodes recipient/inviter/role). Send is fire-and-forget (`Task.Run`); failures log via injected logger but do not 500. Response body no longer leaks the raw token — only `{ id, email, role, expiresAt }`. `TENANT.MEMBER_INVITED.SUCCESS` event emitted. Accept URL is built from `Dashboard:Url` config: `{base}/invites/accept?token={raw}`.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:400-472`.
- Contract/behavior: generates a raw token as 32 cryptographically-random bytes hex-encoded (256 bits of entropy), computes its SHA-256 hash to store in `tenant_invites.invite_token_hash`, sets a 72-hour expiry, dispatches an invite email via `emailService.sendEmail(buildTenantInviteEmail(...))`, and returns **only** `{ id, email, role, expiresAt }` — the raw token never appears in the response body. The caller receives the token via email, not via the HTTP response.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L436-L472
// Generate invite token
const rawToken = randomBytes(32).toString('hex');
const tokenHash = createHash('sha256').update(rawToken).digest('hex');
const expiresAt = new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(); // 72 hours

const invite = await membershipStore.createInvite({
  tenantId,
  email: email.toLowerCase().trim(),
  role: inviteRole as 'owner' | 'admin' | 'member',
  inviteTokenHash: tokenHash,
  invitedBy: jwt.sub,
  expiresAt,
});

// Send invite email
const inviterName = jwt.name || jwt.email;
emailService.sendEmail(
  buildTenantInviteEmail(email.toLowerCase().trim(), tenant.name, inviterName, rawToken, inviteRole),
).catch((err) => {
  request.log.error({ err, inviteId: invite.id }, 'Failed to send invite email');
});

request.log.info({
  event: 'TENANT.MEMBER_INVITED.SUCCESS',
  tenantId,
  email: email.toLowerCase().trim(),
  invitedBy: jwt.sub,
}, 'Tenant invite sent');

return reply.status(201).send({
  id: invite.id,
  email: invite.email,
  role: invite.role,
  expiresAt: invite.expiresAt,
});
```

- Dependencies: `node:crypto` `randomBytes`/`createHash`, `IEmailService.sendEmail`, `buildTenantInviteEmail` template helper.
- Tests: asserted `token` is not in response body, asserted email payload included the raw token, asserted 403 on non-admin requester, asserted role whitelist.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:88-111`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:363`.
- Contract/behavior:
  - Token = `Guid.NewGuid().ToString("N")` — a v4 UUID with ~122 bits of entropy (vs TS 256). `Guid.NewGuid` is not documented as cryptographically secure; on .NET 6+ it uses RandomNumberGenerator internally, but the entropy is still halved vs TS.
  - TTL = 7 days (vs TS 72 hours).
  - No email dispatch at all — the `IEmailService` is not injected.
  - The raw token is returned in the response body: `new { id = invite.Id, token, expiresAt = invite.ExpiresAt }`.
  - No role whitelist validation; accepts any string.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L88-L111
public static async Task<IResult> CreateInvite(
    Guid tenantId,
    CreateOrgInviteRequest req,
    IInviteRepository inviteRepo,
    ClaimsPrincipal principal)
{
    var inviterId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var token = Guid.NewGuid().ToString("N");
    var tokenHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    var invite = await inviteRepo.CreateAsync(new UserInvite
    {
        TenantId = tenantId,
        Email = req.Email,
        Role = req.Role,
        InviteTokenHash = tokenHash,
        InvitedBy = inviterId,
        ExpiresAt = DateTime.UtcNow.AddDays(7)
    });

    return Results.Created($"/api/v1/orgs/{tenantId}/invites/{invite.Id}",
        new { id = invite.Id, token, expiresAt = invite.ExpiresAt });
}
```

- Dependencies: `IInviteRepository` (registered), but `IEmailService` / Email templates directory exists elsewhere but is not wired into this handler.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: caller gets only `{id, email, role, expiresAt}`; invitee gets an HTML+plain email with the raw token; token has 256 bits of entropy; TTL 72h.
- C# does: caller gets `{id, token, expiresAt}` — the token flows through the HTTP response and into browser dev-tools history, `curl -v` captures, access logs, reverse-proxy buffers, etc.; invitee gets **nothing** (no email infrastructure called); token has 122 bits; TTL 7 days.
- For the dashboard flow: TS's admin clicks "Invite", fills email, server emails the invitee, admin sees confirmation. C#'s admin sees the raw token in the response and is expected to manually paste it into a chat/email — the invite feature is a DIY integration in practice.
- For log security: any log aggregator capturing response bodies now has a live invite token for any recipient.
- For role input: TS rejected `role = "root"` with 400; C# writes `"root"` verbatim into `user_invites.role`. When the invite is accepted, the user gets a membership with `role = "root"`.
- In production: the feature is non-functional end-to-end without the email dispatch, and insecure in the tokens it produces.

Error paths:
- TS error paths: `400 { "error": "email is required" }`, `400 { "error": "role must be one of: owner, admin, member" }`, `403 { "error": "Requires admin role or higher to invite" }`, `404 { "error": "Organization not found" }`.
- C# error paths: none.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 6: "**Invite members** endpoint `POST /api/v1/orgs/:tenantId/invites` sends an email invitation with a join token; only `admin+` can invite".
  - Task 4 Subtask 4.3: "Implement `POST /api/v1/orgs/:tenantId/invites` -- send invite email (reuse email service from 18-1)".
  - Task 4 Subtask 4.7: "Create invite email template (HTML + plaintext)".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift + scope loss (email dispatch dropped entirely; token leaks via response).
- **What's needed to finish**:
  1. Replace `Guid.NewGuid()` with `RandomNumberGenerator.GetBytes(32)` → `Convert.ToHexString(...).ToLowerInvariant()` for 256-bit entropy.
  2. Reduce TTL to 72 hours (`DateTime.UtcNow.AddHours(72)`).
  3. Inject `IEmailService` (the C# Resend/SMTP service — there is one at `apps/tamma-elsa/src/Tamma.Api/Services/` family). Build a `TenantInviteEmail` template (HTML + text) and send it. Fire-and-forget, log failures.
  4. Remove `token` from the response body. Return only `{ id, email, role, expiresAt }`.
  5. Validate `req.Role` against `{owner, admin, member}`; 400 otherwise.
  6. Require admin+ of path tenant (findings 001 + hierarchy).
  7. Emit `TENANT.MEMBER_INVITED.SUCCESS` event.
- **Is it "just a stub" or is scope missing?** Scope defined in Story 18-3 (ACs 6, 14; Task 4). The port built the persistence layer correctly but skipped the email send and leaked the token in the response.
- **Blockers**: `IEmailService` exists for auth flows (register, password reset). Confirm whether the same service supports the tenant-invite template or needs a new one.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (CreateInvite).
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (maybe rename response shape).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/EmailTemplates/TenantInviteEmail.cs` (builds subject + HTML + text).
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/CreateInviteTests.cs`.
- Tests to add:
  - `CreateInvite_DoesNotIncludeTokenInResponseBody`
  - `CreateInvite_GeneratesToken_With256BitsEntropy` (assert hex length == 64)
  - `CreateInvite_SetsExpiryTo72Hours`
  - `CreateInvite_CallsEmailService_WithRawToken`
  - `CreateInvite_Returns400_WhenRoleIsInvalid`
  - `CreateInvite_Returns403_WhenNotAdminOfPathTenant`
- Estimated effort: 4h broken down as:
  - Strong token: 0.25h
  - Email template + wiring: 1.5h
  - Role validation + membership gate: 0.5h
  - Remove token from response: 0.1h
  - Tests: 1.65h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:400-472` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:88-111`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:363`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (ACs 6, 14; Task 4 Subtasks 4.3, 4.7)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `017-accept-invite-no-active-tenant.md`, `027-tenant-invites-table-absent.md`, `008-post-orgs-no-event-emission.md`
