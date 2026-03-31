---
title: "Story 8-2: npm Publish CI/CD Pipeline"
sidebar:
  order: 80
---

## User Story

As a **release engineer**,
I want an automated pipeline that publishes `@tamma/cli` to npm on release tags,
So that new versions are available to users immediately after tagging without manual intervention.

## Priority

P0 - Required for Tier 1 distribution

## Acceptance Criteria

1. GitHub Actions workflow `.github/workflows/publish-cli.yml` triggers on `cli-v*` tag push (e.g., `cli-v0.1.0`)
2. Workflow runs: install → build all packages → typecheck → test → bundle → smoke test → publish to npm
3. Published package uses npm provenance (`--provenance` flag) for supply chain security
4. Post-publish verification job installs the package from npm and runs `npx @tamma/cli --version`
5. Bundle build step added to existing CI workflow (`.github/workflows/ci.yml`) so every PR validates the bundle builds
6. Version is extracted from the git tag and verified against `packages/cli/package.json` version
7. `NPM_TOKEN` GitHub Actions secret is configured for authentication
8. npm org `@tamma` is created (or verified) with public access
9. Failed publishes do not leave partial/broken versions on npm
10. Release process documented: bump version → commit → tag → push tag

## Technical Design

### Release Workflow (`.github/workflows/publish-cli.yml`)

```yaml
name: Publish @tamma/cli
on:
  push:
    tags: ['cli-v*']
permissions:
  contents: read
  id-token: write  # npm provenance
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - checkout, setup pnpm, setup node 22
      - pnpm install --frozen-lockfile
      - pnpm -r run build
      - pnpm --filter @tamma/cli run typecheck
      - pnpm --filter @tamma/cli run test
      - pnpm --filter @tamma/cli run build:publish
      - node packages/cli/dist/index.js --version
      - pnpm --filter @tamma/cli run test:smoke
      - npm publish --provenance --access public
  verify:
    needs: publish
    steps:
      - sleep 30 (npm propagation)
      - npx @tamma/cli@{version} --version
```

### CI Addition

Add to existing `ci.yml`:

```yaml
- name: Verify CLI bundle builds
  run: pnpm --filter @tamma/cli run build:bundle
```

### Version Management

Phase 1: Tag-based manual versioning
- Version lives in `packages/cli/package.json`
- Tags follow `cli-v{semver}` pattern
- Workspace packages are bundled (not published independently)

Phase 2 (future): Changesets for multi-package publishing

### Release Process

```bash
# 1. Update version
cd packages/cli && npm version 0.2.0 --no-git-tag-version
# 2. Commit
git commit -am "release: @tamma/cli v0.2.0"
# 3. Tag
git tag cli-v0.2.0
# 4. Push
git push origin main cli-v0.2.0
```

## Dependencies

- **Prerequisite**: Story 8-1 (bundle pipeline must exist)
- **Blocks**: None (Tier 2 and Tier 3 are independent)

## Testing Strategy

1. **Dry-run**: `npm publish --dry-run` to verify package contents before first real publish
2. **npm pack**: Verify tarball contains only `dist/`, `package.json`, `LICENSE`, `README.md`
3. **Post-publish**: Automated verification in CI installs from npm and runs version check
4. **Security**: Verify no `.env`, credentials, or source files are included in tarball

## Estimated Effort

1-2 days

## Files Created/Modified

| File | Action |
|------|--------|
| `.github/workflows/publish-cli.yml` | Create |
| `.github/workflows/ci.yml` | Modify (add bundle build step) |
| `packages/cli/package.json` | Modify (add `files` field, verify `bin`) |
