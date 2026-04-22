# Story 31-3: GitHub driver refactor — wrap existing Octokit clients behind new interface

Status: todo (planning brief, 2026-04-21)

## Story

As a **Tamma service currently calling `IGitHubAppClient` /
`IGitHubActionsClient` / `IGitHubSecretsProvisioner` directly**,
I want those calls re-routed through `IGitPlatformClient` +
`IGitPlatformActionsClient` + `ICiSecretsProvisioner` so the call
sites become platform-agnostic,
so that when Gitea / Forgejo / GitLab drivers land (31-4, 31-6) the
existing agent-dispatch + webhook + onboarding code does not need
parallel refactoring.

## Narrative

GitHub is already the reference implementation. This story does no
new feature work on GitHub — it only moves the seam. Post-31-3:

- `IGitHubAppClient` + `IGitHubActionsClient` stay internal to the
  GitHub driver project.
- All external callers take `IGitPlatformDriver` (from
  `IPlatformResolver`, via 31-2) and use its
  `Client.GetRepoAsync(...)` / `Actions.DispatchWorkflowAsync(...)`
  surface.
- The Epic 19 agent-dispatch activities lose their direct dependency
  on the GitHub-specific client.

## Acceptance Criteria

1. New driver project `apps/tamma-elsa/src/Tamma.Platforms.GitHub/`
   with:
   - `GitHubPlatformDriver : IGitPlatformDriver`
   - `GitHubPlatformClient : IGitPlatformClient` — wraps
     `OctokitGitHubAppClient` and adds repo/PR/branch operations
     that weren't already there.
   - `GitHubActionsPlatformClient : IGitPlatformActionsClient` —
     wraps `OctokitGitHubActionsClient`.
   - `Capabilities` returns the GitHub row from the 31-1 matrix
     (includes `LibsodiumSecrets` and `WebhookHmac`).
2. All internal code (the existing `IGitHubAppClient`,
   `IGitHubActionsClient`) stays, un-renamed, but marked
   `internal sealed class` — external callers cannot bypass the
   abstraction.
3. Call-site refactor — every file that previously took
   `IGitHubActionsClient` or `IGitHubAppClient` as a constructor
   dep is updated:
   - `Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` —
     takes `IPlatformResolver` + an explicit `tenantId` (passed in
     via the workflow context) and resolves the driver at dispatch
     time.
   - `Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs`
     — same refactor.
   - `Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs`
     — same.
   - `Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs`
     — same.
   - Any `Tamma.Api` endpoint that today constructs an Octokit
     client directly (e.g. for install-callback metadata fetch) moves
     to `IPlatformResolver`.
4. DI registration in `Program.cs` — `services.AddGitHubPlatformDriver()`
   extension (replaces the prior `AddGitHubAppClient`,
   `AddGitHubActionsClient`, `AddGitHubSecretsProvisioner` registrations).
   The extension wires the driver into the keyed DI collection the
   `PlatformResolver` (31-2) reads from.
5. `ICiSecretsProvisioner` (31-8) is not in scope yet; 31-3 keeps
   the existing `IGitHubSecretsProvisioner` callers unchanged but
   re-homed under `Tamma.Platforms.GitHub`. 31-8 folds it under the
   new abstraction.
6. No behaviour change — every existing unit + integration test
   passes unchanged. New tests added:
   - `GitHubPlatformDriverTests.CapabilitiesMatchMatrix` — asserts
     the static matrix and the runtime driver return the same set.
   - `GitHubPlatformClientTests.MapsOctokitErrorsToPlatformError` —
     each known Octokit exception shape maps to the right
     `PlatformError` variant.
7. The existing `NullGitHubAppClient` / `NullGitHubSecretsProvisioner`
   survive as the "dev / missing-creds" fallback — `GitHubPlatformDriver`
   delegates to the Null client when `GitHub:AppId` isn't configured.
   Runtime behaviour matches today's "service unavailable" pattern.
8. Webhook handling (`GitHubEndpoints.Webhooks`) is **not** touched
   by 31-3 — 31-7 owns that surface.
9. Documentation: update `apps/tamma-elsa/README.md` platform-
   integration section to reference the new driver layout.

## Technical Context

### Why not delete the old interfaces

Backward-compat for internal callers during the refactor wave. The
old interfaces become `internal` so they stop leaking through the
abstraction; they remain callable inside the driver itself. A later
cleanup story (post-31-4) can inline them.

### Risk of behaviour drift

Keep the existing tests for `OctokitGitHubAppClient`,
`OctokitGitHubActionsClient`, `LibsodiumGitHubSecretsProvisioner`
running unchanged. 31-3 only adds new tests at the
`IGitPlatformClient` seam.

## Dependencies

- **31-1** — abstraction
- **31-2** — resolver
- Blocks 31-7, 31-8, 31-9

## Estimated hours

**16h**

| Task | Hours |
|---|---|
| New driver project + wrapper classes | 4 |
| Call-site refactor (agent dispatch activities) | 5 |
| DI wiring + extension method | 2 |
| Tests | 3 |
| Docs + review | 2 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.GitHub/*.cs` (new project, 3-4 files)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/*.cs` (refactor; ~5 files)
- `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/*.cs` (visibility change: public → internal)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- `apps/tamma-elsa/tests/Tamma.Platforms.GitHub.Tests/*.cs` (new)

## References

- Existing clients: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/`
- Existing activities: `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`
- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md)
