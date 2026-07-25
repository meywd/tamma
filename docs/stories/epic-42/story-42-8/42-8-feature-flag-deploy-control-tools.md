# Story 42-8 — SPLIT into 42-8A and 42-8B

Status: superseded by 42-8A + 42-8B (this file is the split index)

## What changed and why

*Corrected: an earlier draft shipped feature-flag toggling and deploy control as **one** story on the
argument that they are "the same governance shape" and "shared plumbing keeps it from being two
separate larges". That argument does not survive contact with the delivery surface.* The two halves
share **no** implementation — they are:

| | Feature flags (42-8A) | Deploy control (42-8B) |
|---|---|---|
| Provider abstraction | `IFeatureFlagProvider` | `IDeployControlProvider` |
| Reference driver | a flag provider | the Docker-Compose-on-Hetzner deploy path |
| Secret binding | `flags/<provider>` (`ApiKey`) | `deploy/<platform>` (`ApiKey` **or** `SigningKey`) |
| Long-op path | **none** — a flag flip is synchronous | an engine-side bookmark wait + authenticated callback |
| Class discriminator | environment (prod vs non-prod) resolved from the **binding** | target (prod vs staging) + verb (`rollback`) |

That is two provider abstractions, two drivers, two secret bindings, and one suspend path present on
only one side. The only genuinely shared asset is the 42-1/42-3/42-4/42-5 envelope every family
inherits anyway, and — with 42-7 — the generic `WaitForToolOperationActivity`. The combined estimate
(~5–6 days, identical to 42-7's *single* provider abstraction) was the tell.

Splitting also lets 42-8A ship independently: it has no engine-side work at all, so it is not gated on
the wait-activity/bookmark/callback-endpoint chain that 42-8B and 42-7 share.

## The two stories

- **[42-8A — Feature-Flag / Config-Toggle Tool](./42-8a-feature-flag-tool.md)** — read/flip feature
  flags and runtime config. No suspend. Medium.
- **[42-8B — Deploy-Control Tool](./42-8b-deploy-control-tool.md)** — trigger / promote / rollback /
  status a release. Suspending (shares 42-7's wait activity). Large.

## Sequencing

Both stay **Wave 3**, both on the Wave-1 rails (42-1 → 42-2 → 42-3/42-4/42-5). Order within the wave:
**42-9 → 42-8A → 42-8B → 42-7**, by Epic 41 demand and by the fact that 42-8A needs nothing
engine-side. 42-8B and 42-7 share `WaitForToolOperationActivity`, its bookmark prefix, its
`LifecycleBookmarks.CanonicalSuspendActivities` registration and its authenticated callback endpoint —
whichever lands first ships them; the second reuses them and adds only its operation `kind`.
