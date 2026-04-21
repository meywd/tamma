# Story 31-5: Forgejo compat shim + test-matrix extension

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant whose repos live on a Forgejo instance (Codeberg,
self-hosted, Lite, etc.)**,
I want the Gitea driver to recognise Forgejo as a supported kind,
verify webhooks using the `X-Forgejo-Signature` header, and get
tested on every CI run,
so that Forgejo users get the same first-class support as Gitea
without maintaining a parallel driver.

## Narrative

Research surprise (see
[`research/multi-git-platform-2026.md` §2](../research/multi-git-platform-2026.md)):
Forgejo 15.0 (April 2026) keeps Gitea database + REST API
compatibility by design. The original suggested story shape "full
Forgejo driver" overfits — Gitea driver works as-is against
Forgejo with two tiny changes:

1. `PlatformKind.Forgejo` registered as a distinct value (so the
   onboarding UI picks the right branding + default base-URL
   patterns).
2. Webhook signature helper accepts either `X-Gitea-Signature` or
   `X-Forgejo-Signature` (Forgejo emits the latter; some older
   forks emit the former).

Everything else — endpoints, OAuth2 flow, Actions dispatch, artifact
protocols — is shared with 31-4.

## Acceptance Criteria

1. `PlatformKind.Forgejo` value in the 31-1 enum. `PlatformKindCapabilityMatrix`
   has its own row (identical to Gitea's for 15.0; may diverge later).
2. `ForgejoPlatformDriver` — a thin class inheriting or wrapping
   `GiteaPlatformDriver`:
   - `Kind` returns `PlatformKind.Forgejo` (not `Gitea`).
   - `Capabilities` set may override if a future Forgejo divergence
     is found — today identical.
3. Webhook verifier shared code — `GiteaWebhookSignatureVerifier`
   (from 31-4) accepts a configurable header-name list. Driver
   config for Forgejo uses `["X-Forgejo-Signature",
   "X-Gitea-Signature"]` (order = preference); Gitea driver uses
   `["X-Gitea-Signature"]`. No hash-shape change.
4. Integration test harness (31-10) adds a `forgejo` test service
   container alongside Gitea. The same contract-test fixture runs
   against both — passes without modification. Any Forgejo-specific
   failure is a new divergence to document.
5. Onboarding UI (31-9) picker lists Forgejo as a distinct option
   with its own branding. Credential entry form identical to
   Gitea's.
6. No new migration; no new table; no new endpoints. `tenant_platform_installations`
   already carries `platform_kind` and accepts `'forgejo'` per the
   31-2 CHECK constraint.
7. DI extension `services.AddForgejoPlatformDriver()` registers
   under `PlatformKind.Forgejo` key — re-uses Gitea's HTTP client
   setup.
8. Documentation updated: `apps/tamma-elsa/src/Tamma.Platforms.Gitea/README.md`
   gains a "Forgejo compatibility" section listing the two
   divergence points (header name + potential future capability
   drift).

## Technical Context

### If compat diverges

Should a future Forgejo version break API-compat with Gitea (e.g. a
security-hardening flag removal, a rename, a payload schema
change), 31-5's thin shim graduates into a full driver. The
interface shape doesn't change — only the impl grows.

### Why not `platform_kind='gitea'` with a flag

Keeping `forgejo` as a first-class kind:

- Lets onboarding UI show the right branding + docs.
- Lets telemetry + audit logs distinguish Forgejo from Gitea.
- Supports future governance discussions without a rename burden.

The cost is a tiny bit of duplication in
`PlatformKindCapabilityMatrix`; acceptable.

## Dependencies

- **31-4** — Gitea driver provides the shared client
- Blocks 31-9, 31-10

## Estimated hours

**8h**

| Task | Hours |
|---|---|
| `ForgejoPlatformDriver` + header override | 2 |
| DI extension | 1 |
| Test container addition + contract-test run | 3 |
| Docs + review | 2 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.Gitea/ForgejoPlatformDriver.cs` (new, lives in the Gitea project)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` (DI)
- `apps/tamma-elsa/src/Tamma.Platforms.Gitea/README.md` (docs)
- `apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/ForgejoContractTests.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §2
- [Forgejo v15.0 release (2026-04)](https://forgejo.org/2026-04-release-v15-0/)
- [Forgejo Actions Reference](https://forgejo.org/docs/next/user/actions/reference/)
- Gitea driver: [`31-4-gitea-driver.md`](./31-4-gitea-driver.md)
