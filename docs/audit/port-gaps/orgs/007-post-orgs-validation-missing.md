# Finding 007: `POST /orgs` Skips Name/Slug Validation and Reserved-Slug List

**Scope**: orgs
**Severity**: P1 (feature broken)
**Status**: Incomplete (partial port, missing validation)
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: 549f10d
- **Notes**: New `Tamma.Api.Validation.SlugValidation` module ports the TS regex `^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$` plus the 16-entry reserved set verbatim. `OrgEndpoints.CreateOrg` now (1) validates `Name.Trim()` length 2-100, (2) lowercases the slug, (3) enforces the regex, (4) checks the reserved set, (5) returns 409 only on duplicate. Same validation will be re-applied to the `Name`/`Plan` fields on `UpdateOrgSettings` (finding 010). Tests: `SlugValidationTests`, `OrgEndpointHandlerTests.CreateOrg_Returns400_WhenSlugIsReserved`, `_WhenNameTooShort`, `_WhenSlugInvalid`, `_Returns409_OnDuplicateSlug`.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:41-56, 92-152`.
- Contract/behavior: before creating a tenant, TS validated that `name` is 2-100 chars, `slug` matches `/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/`, and `slug` is not in a fixed reserved-slug set (`admin, api, auth, settings, app, www, dashboard, login, register, signup, signin, default, help, support, docs, blog`). Only after these checks did it call `tenantStore.getTenantBySlug(slug)` and then `createTenant`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L41-L49
/** Reserved org slugs that cannot be used. */
const RESERVED_SLUGS = new Set([
  'admin', 'api', 'auth', 'settings', 'app', 'www',
  'dashboard', 'login', 'register', 'signup', 'signin',
  'default', 'help', 'support', 'docs', 'blog',
]);

/** Slug validation regex: lowercase alphanumeric + hyphens, 3-40 chars. */
const SLUG_REGEX = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;
```

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L100-L131
const { name, slug } = request.body ?? {};

if (!name || !slug) {
  return reply.status(400).send({ error: 'name and slug are required' });
}

if (typeof name !== 'string' || name.trim().length < 2 || name.trim().length > 100) {
  return reply.status(400).send({ error: 'Name must be between 2 and 100 characters' });
}

// Validate slug
if (!SLUG_REGEX.test(slug)) {
  return reply.status(400).send({
    error: 'Slug must be 3-40 characters, lowercase alphanumeric and hyphens only, cannot start or end with hyphen',
  });
}

if (RESERVED_SLUGS.has(slug)) {
  return reply.status(400).send({ error: 'This slug is reserved and cannot be used' });
}

const existingTenant = await tenantStore.getTenantBySlug(slug);
if (existingTenant) {
  return reply.status(409).send({ error: 'An organization with this slug already exists' });
}

const tenant = await tenantStore.createTenant({
  name: name.trim(),
  slug,
});
```

- Dependencies: none external.
- Tests: `packages/api/src/routes/orgs/__tests__/orgs.test.ts` covered each error code (400 on bad slug, 400 on reserved, 409 on duplicate).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:12-36`, `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:3`.
- Contract/behavior: only a single check: duplicate slug → 409. No name-length validation, no slug-regex validation, no reserved-slug list. `req.Slug.ToLowerInvariant()` is the only transformation.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L12-L36
public static async Task<IResult> CreateOrg(
    CreateOrgRequest req,
    ITenantRepository tenantRepo,
    ITenantMembershipRepository membershipRepo,
    IUserRepository userRepo,
    ClaimsPrincipal principal)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    var existing = await tenantRepo.GetBySlugAsync(req.Slug);
    if (existing is not null)
        return Results.Conflict(new { error = "Slug already taken" });

    var tenant = await tenantRepo.CreateAsync(new Tenant
    {
        Name = req.Name,
        Slug = req.Slug.ToLowerInvariant(),
        Type = "org",
        OwnerId = userId
    });

    await membershipRepo.AddAsync(tenant.Id, userId, "owner");
    return Results.Created($"/api/v1/orgs/{tenant.Id}",
        new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs (current) L3
public record CreateOrgRequest(string Name, string Slug);
// ← no [Required], no [MinLength]/[MaxLength], no [RegularExpression]
```

- Dependencies: `ITenantRepository.GetBySlugAsync` exists.
- Tests: none under `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/`.

## 3. The gap

Concrete behavioral difference — what a caller can send.

- TS did: 400 for empty name, 400 for `slug = "A"`, 400 for `slug = "admin"`, 400 for `slug = "-foo"`, 400 for `slug = "foo-"`, 409 for duplicate. Successful creates always trimmed `name`.
- C# does: accepts `name = ""`, `name = null` (technically rejected by C# `Name` non-null record signature only at the JSON binding layer — an empty string passes), `slug = "Admin"` (down-cased to `admin`, collides with the ideal reserved list), `slug = "a"` (single char), `slug = "-foo"`, `slug = "foo-"`. No trim on name.
- For a caller sending `{ "name": "", "slug": "admin" }`: TS returns `400 { "error": "Name must be between 2 and 100 characters" }`; C# returns `201 Created` with `tenant.slug = "admin"`, poisoning `/orgs/admin` routes on the dashboard.
- For a caller sending `{ "name": "Acme", "slug": "my.org" }` (invalid char): TS returns `400 { "error": "Slug must be 3-40 characters…" }`; C# returns `201 Created` with a slug the dashboard cannot safely use in URLs.
- In production: every tenant slug becomes a vanity URL (per Story 18-3 implementation note "`tenants.slug` enables vanity URLs"). Allowing `admin`, `app`, `www`, etc. collides with existing routes like `/api/admin` (`apps/tamma-elsa/src/Tamma.Api/Program.cs:338`).

Error paths:
- TS error path: `400 { "error": "Name must be between 2 and 100 characters" }`, `400 { "error": "Slug must be 3-40 characters…" }`, `400 { "error": "This slug is reserved and cannot be used" }`, `409 { "error": "An organization with this slug already exists" }`.
- C# error path: only `409 { "error": "Slug already taken" }`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 2: "**Organization slug** must be unique, URL-safe (lowercase alphanumeric + hyphens, 3-40 chars), and not conflict with reserved words (`admin`, `api`, `auth`, `settings`, `app`, `www`). Maps to `tenants.slug`."
  - Implementation notes L158: "Reserved slugs should be defined as a constant array and checked on tenant creation and slug update."
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port, missing three behaviors: regex, reserved, name length).
- **What's needed to finish**:
  1. Add a `Tamma.Api/Validation/SlugValidation.cs` constant with the regex and the reserved set (port the 16 entries).
  2. In `CreateOrg`, after `var userId = ...`, validate `req.Name.Trim().Length >= 2 && <= 100`, regex-match the slug, check `ReservedSlugs.Contains(slug)`; return `Results.BadRequest(new { error = "..." })` for each.
  3. Apply the same validation to `UpdateOrgSettings` when renaming (see finding 010).
  4. Trim the `name` on write.
- **Is it "just a stub" or is scope missing?** Scope was understood in the story; the port simplified the endpoint to "just duplicate check". The author likely expected DataAnnotations to handle it, but `OrgDtos.cs` has no annotations.
- **Blockers**: none.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (add validation), `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (optional: add DataAnnotations).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Validation/SlugValidation.cs`, `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/CreateOrgValidationTests.cs`.
- Tests to add:
  - `CreateOrg_Returns400_WhenNameIsEmpty`
  - `CreateOrg_Returns400_WhenNameExceeds100Chars`
  - `CreateOrg_Returns400_WhenSlugFailsRegex` (params: "a", "admin-", "-admin", "my.org", "My-Org")
  - `CreateOrg_Returns400_WhenSlugIsReserved` (params: "admin", "api", "auth", "settings", "app", "www")
  - `CreateOrg_Returns409_WhenSlugTaken`
  - `CreateOrg_TrimsName_BeforePersist`
- Estimated effort: 1h broken down as:
  - Validation module + wiring: 0.5h
  - Tests: 0.5h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:41-131` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:12-36`, `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:3`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 2)
- Related findings: `010-put-orgs-settings-cannot-rename.md`, `008-post-orgs-no-event-emission.md`, `009-post-orgs-no-active-tenant-update.md`
