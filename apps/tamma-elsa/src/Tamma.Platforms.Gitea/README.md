# Tamma.Platforms.Gitea

Story 31-4 — Gitea platform driver implementing
`Tamma.Platforms.Abstractions.IGitPlatformDriver`. Targets Gitea
1.21+ for the full capability surface; older instances get a read-
only subset (no Actions / Artifacts / Secrets).

## Endpoint mapping

| Abstraction method | Gitea endpoint |
|---|---|
| `GetRepoAsync` | `GET /api/v1/repos/{owner}/{repo}` |
| `ListRepoBranchesAsync` | `GET /api/v1/repos/{owner}/{repo}/branches?page=&limit=50` |
| `GetFileContentAsync` | `GET /api/v1/repos/{owner}/{repo}/contents/{path}?ref=` |
| `CreateBranchAsync` | `POST /api/v1/repos/{owner}/{repo}/branches` |
| `OpenPullRequestAsync` | `POST /api/v1/repos/{owner}/{repo}/pulls` (idempotent — see below) |
| `GetPullRequestAsync` | `GET /api/v1/repos/{owner}/{repo}/pulls/{n}` |
| `ListPullRequestFilesAsync` | `GET /api/v1/repos/{owner}/{repo}/pulls/{n}/files` |
| `CreatePullRequestReviewCommentAsync` | `POST /api/v1/repos/{owner}/{repo}/pulls/{n}/reviews` |
| `MergePullRequestAsync` | `POST /api/v1/repos/{owner}/{repo}/pulls/{n}/merge` |
| `CreateIssueCommentAsync` | `POST /api/v1/repos/{owner}/{repo}/issues/{n}/comments` |
| `RegisterWebhookAsync` | `POST /api/v1/repos/{owner}/{repo}/hooks` |
| `ListAccessibleReposAsync` | `GET /api/v1/user/repos?page=&limit=50` |
| `DispatchWorkflowAsync` | `POST /api/v1/repos/{o}/{r}/actions/workflows/{file}/dispatches` |
| `GetRunStatusAsync` | `GET /api/v1/repos/{o}/{r}/actions/runs/{id}` |
| `ListRunJobsAsync` | `GET /api/v1/repos/{o}/{r}/actions/runs/{id}/jobs` |
| `DownloadArtifactAsync` | `GET /api/v1/repos/{o}/{r}/actions/artifacts/{id}/zip` (4 MB cap) |
| `CancelRunAsync` | `POST /api/v1/repos/{o}/{r}/actions/runs/{id}/cancel` |

## Auth

Credential plaintext supplied through `IPlatformCredentialReader`
(Epic 29 secret-store seam — `Tamma.Platforms.PlatformResolver`
wires the read).

Two credential shapes:

- **Bot / PAT token** — raw string. Sent as
  `Authorization: token <value>`.
- **OAuth2 application** — JSON object
  `{ "kind": "oauth2", "clientId": "...", "clientSecret": "...",
  "refreshToken": "..." }`. Driver mints a short-lived access token
  via `POST /login/oauth/access_token` (`grant_type=refresh_token`)
  and caches it per-installation with a 60 s safety margin. On 401
  the driver invalidates the cache and retries once.

## Capability detection

On factory construction the driver probes `/api/v1/version` and
parses the response. Versions below 1.21 drop:

- `PlatformCapability.Actions`
- `PlatformCapability.Artifacts`
- `PlatformCapability.Secrets`

`IGitPlatformDriver.Actions` returns null when Actions is missing —
callers SHOULD branch on `Capabilities.Contains(Actions)` per the
31-1 ADR.

## Webhook signatures

`GiteaWebhookSignatureVerifier` accepts both `X-Gitea-Signature` and
(via 31-5 reuse) `X-Forgejo-Signature` headers. HMAC-SHA256 over the
raw body, hex-lowercase, constant-time compared via
`CryptographicOperations.FixedTimeEquals`. Fails closed on missing
secret.

## Caveats

- **Gitea pagination max is 50** — vs. GitHub's 100. List helpers
  page until a partial page comes back.
- **Artifact zip endpoint** — Gitea supports both v1-v3 multi-file
  and v4 single-zip; `GET /zip` returns whichever the runtime ran.
  We always pull through the bounded 4 MB-cap stream
  (`Agent:MaxArtifactBytes` overrides; 0 / negative reverts to
  default — unbounded is not allowed).
- **Idempotent OpenPR** — before creating, the driver lists open
  PRs and returns an existing one if `(head, base)` matches. Callers
  needing strict idempotency should still attach a workflow-level
  key.
- **Branch creation** — Gitea wants `old_ref_name` (a SHA) OR
  `old_branch_name` (a branch name). Driver uses `old_ref_name`
  with `CreateBranchRequest.FromSha`.

## Wire-up

```csharp
services.AddGiteaPlatformDriver();
// PlatformResolver picks the factory up via keyed DI when
// tenant_platform_installations.platform_kind = 'gitea'.
```

## Forgejo compatibility (Story 31-5)

Forgejo branched from Gitea at v1.18 (Dec 2022) and intentionally
retains REST + DB + webhook payload compatibility with its Gitea
fork-base. The 31-5 driver is a thin shim that composes the same
`GiteaPlatformClient` + `GiteaActionsPlatformClient` stack and
exposes itself as `PlatformKind.Forgejo` so the onboarding picker
can brand Forgejo separately and the matrix can diverge in future
without touching the Gitea driver.

### Divergence points the wrapper handles

1. **Version string suffix**: Forgejo's `/api/v1/version` returns
   shapes like `1.21.5+forgejo-3`. The Gitea factory's
   strip-after-'+' parser handles this unchanged — the suffix is
   just metadata.
2. **Webhook signature header**: Forgejo emits
   `X-Forgejo-Signature` on modern releases. Older forks (pre-
   rename) still emit `X-Gitea-Signature`. The verifier accepts
   both; the Forgejo registration extension wires it with the
   `ForgejoAndGiteaHeaderNames` priority list (Forgejo first,
   Gitea second).

### Wire-up

```csharp
services.AddGiteaPlatformDriver();   // PlatformKind.Gitea
services.AddForgejoPlatformDriver(); // PlatformKind.Forgejo
```

Both extensions can run in the same host without conflict — they
share the OAuth2 token cache singleton and each registers a
distinct keyed factory + named HttpClient (`tamma-gitea`,
`tamma-forgejo`).

### Future-drift policy

The wrapper is cheaper than duplication today because Forgejo's
divergences are at the boundary (one header name, one suffix
shape). A capability divergence (Forgejo gets native OIDC, say) is
absorbed by overriding `ForgejoPlatformDriver.ComputeCapabilities`
+ updating `PlatformKindCapabilityMatrix.Defaults[Forgejo]`. If a
hot-path REST shape diverges in a way that breaks shared parsing,
the fix is to promote `ForgejoPlatformDriver` to a full driver
project with its own `GiteaHttpClient` subclass. The trigger is a
contract-test failure in the 31-10 nightly Forgejo container suite.
