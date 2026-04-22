# Layer 0: Prerequisites

**Duration**: ~8 hours one-time setup
**Team**: Coordinator + 1 platform engineer
**Goal**: Lay the foundation for parallel, worktree-based story execution so that Layers 1–5 can run without stepping on each other.

**Do not start Layer 1 until every task in this layer is complete and verified.**

## Tasks

### 0.1 Create worktree parent directory

```bash
cd /home/meywd
mkdir -p tamma-worktrees
```

Rationale: All layer-specific worktrees live in `/home/meywd/tamma-worktrees/*`. Keeping them outside the primary checkout avoids IDE confusion.

### 0.2 Verify git worktree support

```bash
cd /home/meywd/tamma
git worktree list
git fetch --all --prune
git status
```

Expected: `main` exists on origin, no uncommitted changes in the primary checkout.

### 0.3 Create the shared test database (Postgres 17)

Layer 1+ stories need an isolated Postgres instance for integration tests. Create a dedicated database on the dev Postgres:

```bash
# From the primary checkout
psql -h localhost -U postgres -c "CREATE DATABASE tamma_test_layered;"
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/001_github_installations.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/002_users.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/003_api_keys.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/004_user_settings.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/005_user_api_keys.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/006_user_invites.sql
psql -h localhost -U postgres -d tamma_test_layered -f database/migrations/007_users_soft_delete.sql
```

**Important**: Do not run new migrations (008+) until Story 17-1 lands. The shared test DB starts at schema version 007.

Export env var for teams:

```bash
export TAMMA_TEST_DB_URL="postgres://postgres@localhost:5432/tamma_test_layered"
```

Add to `.dev/findings/layer-0-test-db.md` for team reference.

### 0.4 Define branch naming convention

All branches follow: `feat/story-{epic}-{story}-{slug}` or `fix/story-{epic}-{story}-{slug}` for bugs.

Examples:

| Story | Branch |
|-------|--------|
| 16-1 | `feat/story-16-1-oauth-proxy` |
| 17-1 | `feat/story-17-1-tenant-model` |
| 16-2 | `feat/story-16-2-user-management-api` |
| 16-5 | `feat/story-16-5-rbac-enforcement` |
| 16-7 | `feat/story-16-7-service-to-service-auth` |
| 17-2 | `feat/story-17-2-row-level-security` |
| 17-3 | `feat/story-17-3-tenant-event-store` |
| 17-4 | `feat/story-17-4-tenant-workflow-store` |
| 17-5 | `feat/story-17-5-tenant-context-middleware` |
| 27-1 | `feat/story-27-1-prompt-store-schema` |
| 27-2 | `feat/story-27-2-prompt-store-service` |
| 27-3 | `feat/story-27-3-prompt-store-api` |
| 27-4 | `feat/story-27-4-prompt-store-admin-ui` |
| 27-5 | `feat/story-27-5-prompt-store-tenant-ui` |
| 27-6 | `feat/story-27-6-prompt-store-elsa-integration` |
| 27-7 | `feat/story-27-7-prompt-store-event-sourcing` |
| 9-1 | `feat/story-9-1-agent-config-api` |
| 9-2 | `feat/story-9-2-diagnostics-api` |
| 9-3 | `feat/story-9-3-health-tracker-api` |
| 9-4 | `feat/story-9-4-provider-factory-api` |
| 9-5 | `feat/story-9-5-provider-chain-api` |
| 9-7 | `feat/story-9-7-sanitization-api` |
| 9-8 | `feat/story-9-8-agent-resolver-api` |
| 9-9 | `feat/story-9-9-engine-integration` |
| 9-10 | `feat/story-9-10-cli-wiring` |
| 9-11 | `feat/story-9-11-diagnostics-queue-elsa` |
| 9-12 | `feat/story-9-12-cross-epic-integration` |
| 16-3 | `feat/story-16-3-admin-dashboard` |
| 16-4 | `feat/story-16-4-unified-nav` |
| 18-1 | `feat/story-18-1-registration-email` |
| 18-2 | `feat/story-18-2-login-sessions` |
| 18-3 | `feat/story-18-3-organization-tenant` |
| 18-4 | `feat/story-18-4-github-app-onboarding` |
| 18-5 | `feat/story-18-5-user-dashboard` |
| 18-6 | `feat/story-18-6-password-reset` |
| 12-5a | `feat/story-12-5a-context-truncation` |
| 12-5b | `feat/story-12-5b-few-shot` |
| 12-5c | `fix/story-12-5c-skill-level` |
| 12-5d | `feat/story-12-5d-ab-testing` |
| 12-5e | `fix/story-12-5e-ci-retry-counter` |
| 12-7a | `feat/story-12-7a-vector-search-tools` |
| 12-7b | `feat/story-12-7b-convention-history` |
| 12-7c | `feat/story-12-7c-context-budget` |
| 12-7d | `feat/story-12-7d-tool-access-config` |
| 12-7e | `feat/story-12-7e-elsa-tool-loop` |

### 0.5 Create worktree reference script

Save as `.dev/scripts/create-worktree.sh` (or put in `.dev/findings/worktree-quickstart.md`):

```bash
#!/usr/bin/env bash
# Usage: ./create-worktree.sh feat/story-16-1-oauth-proxy
set -e
BRANCH="$1"
if [[ -z "$BRANCH" ]]; then
  echo "Usage: $0 <branch-name>"
  exit 1
fi
WORKTREE_NAME=$(basename "$BRANCH")
WORKTREE_PATH="/home/meywd/tamma-worktrees/${WORKTREE_NAME}"
cd /home/meywd/tamma
git fetch origin
git worktree add "$WORKTREE_PATH" -b "$BRANCH" origin/main
cd "$WORKTREE_PATH"
pnpm install
echo "Worktree ready: $WORKTREE_PATH"
```

### 0.6 Assign CI/deploy coordinator

Assign one person (the **Deploy Coordinator**) to own:

- Shepherding PRs that require Docker redeploy (16-1 oauth2-proxy container, 16-7 service-to-service auth)
- Coordinating staging deploys at Layer 4/5 boundaries
- Approving nginx/oauth2-proxy config changes
- Monitoring CI failures across parallel worktrees

Deploy-requiring stories are flagged in each layer file with **Deploy: YES**.

### 0.7 Document local test workflow

Local test commands agents should run before pushing. Add to `.dev/findings/local-test-workflow.md`:

```bash
# Unit tests (fast, run always)
pnpm test:unit

# Integration tests (requires test DB)
TAMMA_TEST_DB_URL=postgres://postgres@localhost:5432/tamma_test_layered pnpm test:integration

# Type check
pnpm build

# Lint & format
pnpm lint && pnpm format

# Scoped to a package
pnpm test --filter @tamma/api
```

### 0.8 Migration steward handoff

Nominate a **Migration Steward** who owns `docs/stories/migration-ordering.md`. Before any agent creates a new migration file:

1. Agent asks the steward for the next available number.
2. Steward updates `migration-ordering.md` with the assigned number, story, and description.
3. Agent creates the file using the assigned number.

**Rule**: No agent may pick a migration number unilaterally. The steward must approve.

### 0.9 Agree on PR description template

Save as `.github/pull_request_template.md` (if absent) or `.dev/templates/pr-description.md`:

```markdown
## Story
Closes #<issue-id> — implements Story <epic>-<story>.

## Summary
<1-3 sentences>

## Layer
Layer <N> / Team <letter>

## Migration
<number(s)> or "none"

## Depends on
<list of merged PRs this depends on>

## Test Plan
- [ ] Unit tests added/updated
- [ ] Integration tests pass on shared test DB
- [ ] Coverage ≥ 80% line
- [ ] `pnpm build` passes
- [ ] `pnpm lint` passes

## Deploy Requirement
none | docker-redeploy | nginx-config-change | env-var-addition

## Reviewers
- [ ] Team reviewer
- [ ] Cross-team reviewer
- [ ] Migration steward (if migration added)
- [ ] Deploy coordinator (if deploy required)
```

### 0.10 Smoke test: create a dummy worktree

To verify setup, the Coordinator creates a dummy worktree, runs `pnpm install` and `pnpm test:unit`, and removes it:

```bash
cd /home/meywd/tamma
git worktree add /home/meywd/tamma-worktrees/layer-0-smoke -b chore/layer-0-smoke-test origin/main
cd /home/meywd/tamma-worktrees/layer-0-smoke
pnpm install
pnpm test:unit
cd /home/meywd/tamma
git worktree remove /home/meywd/tamma-worktrees/layer-0-smoke
git branch -D chore/layer-0-smoke-test
```

## Success Criteria

- [ ] `/home/meywd/tamma-worktrees/` directory exists
- [ ] `tamma_test_layered` database exists at schema version 007
- [ ] Branch naming convention documented in this file and read by all team leads
- [ ] Worktree creation script works end-to-end
- [ ] Deploy Coordinator nominated
- [ ] Migration Steward nominated
- [ ] Local test workflow documented in `.dev/findings/local-test-workflow.md`
- [ ] PR template saved
- [ ] Smoke test passes

## Deliverables

- `.dev/findings/layer-0-test-db.md` — test DB usage notes
- `.dev/findings/local-test-workflow.md` — local test commands
- `.dev/scripts/create-worktree.sh` — worktree helper (optional)
- `.dev/templates/pr-description.md` — PR template
- Named Coordinator, Deploy Coordinator, Migration Steward

## Handoff to Layer 1

When Layer 0 is complete, announce in the coordinator log:

```
Layer 0 complete. Layer 1 teams may begin pulling worktrees from origin/main.
Migration Steward: <name>
Deploy Coordinator: <name>
Shared test DB: postgres://postgres@localhost:5432/tamma_test_layered
```

---

**Next**: [`layer-1-foundation.md`](./layer-1-foundation.md)
