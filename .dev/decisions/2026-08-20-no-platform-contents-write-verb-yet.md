# No contents-write verb on IGitPlatformClient until a real consumer exists

Date: 2026-08-20
Status: accepted (owner: "handle all" — this is the handling)

## Context

The fallback TDD leg's commit step (`CommitChangesActivity`) cannot land code:
`IGitPlatformClient` has 32 members and none writes file contents, and the
activity's inputs carry file PATHS, not contents — the engine never holds the
working tree; the agent executor does. Since 2026-08-18 the step fails loudly
with typed errors instead of fabricating commit SHAs (finding 28), which left
the question: build the platform contents-write verb (interface + three
drivers + null seam + ActionCatalog descriptor + mediation surface) so that
leg can commit?

## Decision

Not yet. The verb is deliberately NOT built until a consumer that can actually
supply file contents exists. Two reasons, both proven in this repo this week:

1. The commit path that works is the agent-executor path (the agent holds the
   checkout and commits with git itself; the platform API only opens the PR).
   A platform-side write verb would duplicate that for a leg whose activity
   structurally cannot feed it — the engine would still have nothing to write.
2. "Registered, tested, unreachable" code is a defect class here, not a
   convenience: CheckBudgetActivity sat wired to no graph while the alert rule
   that depended on its emission silently never fired, and this week's reviews
   caught two more fixes aimed at code nothing executes. Building the verb now
   mints exactly that shape.

## Consequences

- The fallback TDD leg remains non-functional for committing, and says so with
  typed errors (`TDD.COMMIT.NO_SEAM`) rather than pretending.
- Whoever builds a real consumer (an engine-side patch-apply flow that carries
  contents, or scaffolding pushed from the API) builds the verb IN THE SAME
  change, with the catalog descriptor and mediation route — the checklist is in
  finding 28 / item B(c) of the 2026-08-18 lane report.
