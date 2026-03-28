# Story 8-8: Docker CI/CD & CLI Integration

## User Story

As a **Tamma user**,
I want to run `tamma init --full-stack` to generate a ready-to-use Docker deployment,
So that I can deploy the full platform without cloning the repository or understanding the service architecture.

## Priority

P2 - Enhancement for Tier 3 distribution

## Acceptance Criteria

1. GitHub Actions workflow `.github/workflows/docker-publish.yml` builds and pushes all 5 images to GHCR on merge to `main` and on version tags
2. Images pushed to: `ghcr.io/meywd/tamma-api`, `ghcr.io/meywd/tamma-engine`, `ghcr.io/meywd/tamma-dashboard`, `ghcr.io/meywd/tamma-elsa`, `ghcr.io/meywd/tamma-api-dotnet`
3. Three tag forms per image: `latest` (main branch), semver (release tags), `sha-{commit}` (all pushes)
4. Docker smoke test workflow spins up the full stack in CI, waits for health checks, and verifies HTTP endpoints
5. `tamma init --full-stack` command generates: `docker-compose.yml`, `.env`, `init-db.sql`, `nginx-dashboard.conf` in the current directory
6. Generated compose file uses pre-built GHCR images (no `build:` directives) so users don't need source code
7. Interactive secret prompt during `init --full-stack` collects `GITHUB_TOKEN` and `ANTHROPIC_API_KEY` and writes to `.env`
8. All GHCR packages configured with public visibility
9. Docker BuildKit layer caching enabled in CI for faster builds
10. Update script (`tamma-update.sh`) provided for `docker compose pull && up -d` with Postgres backup

## Technical Design

### Docker Publish Workflow

```yaml
name: Build & Publish Docker Images
on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
    branches: [main]  # build only, no push
jobs:
  build-ts:
    matrix:
      target: [tamma-api, tamma-engine]
    # docker/metadata-action for tags
    # docker/build-push-action with cache-from: type=gha
  build-dotnet:
    matrix:
      include:
        - name: tamma-elsa, dockerfile: Tamma.ElsaServer/Dockerfile
        - name: tamma-api-dotnet, dockerfile: Tamma.Api/Dockerfile
  build-dashboard:
    # docker/build-push-action for dashboard
```

### CLI Command (`packages/cli/src/commands/init-fullstack.ts`)

```typescript
export async function initFullStackCommand(options: { dir?: string }): Promise<void> {
  const targetDir = options.dir ?? process.cwd();
  // Write embedded templates: compose, .env, init-db.sql, nginx conf
  // Interactive prompt for secrets if TTY
  // Print next steps
}
```

### Smoke Test Workflow

```yaml
smoke-test:
  steps:
    - Build all images
    - Start stack with test .env
    - Wait for health checks (postgres, rabbitmq, elsa, APIs)
    - curl health endpoints
    - Collect logs on failure
    - Teardown with docker compose down -v
```

## Dependencies

- **Prerequisite**: Story 8-6 (Dockerfiles), Story 8-7 (compose configuration)
- **Blocks**: None (final story in Tier 3)

## Testing Strategy

1. **CI Build**: Verify all images build on PRs (no push)
2. **Smoke**: Full stack smoke test in CI with health endpoint verification
3. **Init**: Test `tamma init --full-stack` generates valid compose file
4. **Pull**: Verify pre-built images pull and start correctly from GHCR

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `.github/workflows/docker-publish.yml` | Create |
| `.github/workflows/docker-smoke-test.yml` | Create |
| `packages/cli/src/commands/init-fullstack.ts` | Create |
| `packages/cli/src/index.tsx` | Modify (add `init --full-stack` flag) |
| `docker/tamma-update.sh` | Create |

## Logging Requirements

Container and CI/CD components MUST log structured output for container orchestration observability.

- **INFO**: Container started (image version, config), health check endpoints responding, CI pipeline stage completed
- **WARN**: Container resource limits approaching, health check degraded, build cache miss
- **ERROR**: Container failed to start, health check failed, CI pipeline step failed
- **Format**: All container logs MUST output structured JSON (one JSON object per line) for log aggregation compatibility (ELK, Loki, CloudWatch)
