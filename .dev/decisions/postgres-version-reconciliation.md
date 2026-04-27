# Decision: PostgreSQL Version Standardization

**Date**: 2026-04-26  
**Status**: Implemented  
**Resolves**: Infrastructure drift between CLAUDE.md target (PostgreSQL 17) and shipped artifacts (mixed 15/16/17)

## Problem

The codebase exhibited version drift across deployment and test artifacts:

- **CLAUDE.md** declares PostgreSQL 17 as the target
- **Compose files** (dev/prod) pinned postgres:15-alpine
- **Test fixtures** correctly used postgres:17-alpine
- **CI workflows** used postgres:17 (GitHub Actions)
- **Documentation** contained references to 15, 16, and 17 inconsistently
- **CLI init template** used postgres:16-alpine

This drift created risk of:
1. Local development (postgres:15) diverging from test/CI/production behavior
2. Postgres 15 EOL (October 2025) approaching without a clear upgrade path
3. Loss of Postgres 17 features (improved JSON performance, COPY improvements, vacuum work)

## Decision

**Standardize all Postgres deployments on postgres:17-alpine** throughout the codebase.

### Rationale

1. **CLAUDE.md Authority**: The CLAUDE.md file explicitly lists "PostgreSQL 17" in the Technology Stack section under Database, establishing 17 as the target architecture.

2. **Test Coverage Already in 17**: All Testcontainers-based test fixtures in `apps/tamma-elsa/tests/` already use postgres:17-alpine, indicating the codebase was designed for 17 but not deployed with it.

3. **Modern LTS Strategy**: Postgres 17 is the current stable release with long-term support. Postgres 15 enters end-of-life October 2025, and Postgres 16 is in active maintenance but not the latest stable.

4. **No Version-Specific Code**: The C# data access layer uses no version-specific Postgres features. No code changes required—only image tag updates.

5. **Production Compatibility**: Hetzner (the production host per CLAUDE.md) supports Postgres 17, so there are no hosting constraints.

## Changes Applied

Updated Postgres image references from postgres:15-alpine and postgres:16-alpine to postgres:17-alpine in:

### Compose Files (4)
- `apps/tamma-elsa/docker-compose.yml`
- `apps/tamma-elsa/docker-compose.prod.yml`
- `docker/docker-compose.yml`
- `docker/docker-compose.test.yml`

### Build Templates (1)
- `packages/cli/src/commands/init-fullstack.ts` (init command generated compose)

### Test Fixtures (verified—already correct)
- 11 test fixture files in `apps/tamma-elsa/tests/` all use postgres:17-alpine (no changes needed)

### CI Workflows (verified—already correct)
- `apps/test-platform/.github/workflows/e2e-tests.yml` uses postgres:17 (GitHub Actions official image)

## Verification

1. **Compose Validation**: All updated compose files pass `docker compose config -q` validation.
2. **Test Fixture Consistency**: All 11 Testcontainers-based test fixtures use postgres:17-alpine.
3. **No Version-Specific SQL/Code**: C# production code contains no postgres 15/16/17-specific syntax.
4. **Backward Compatibility**: Postgres 17 is forward-compatible with schemas created by 15/16.

## Deployment Notes

### For Existing Production Deployments

If production already runs postgres:15 or postgres:16:

1. Ensure Hetzner (or your host) supports Postgres 17
2. Plan a maintenance window for in-place upgrade: `ALTER SYSTEM SET ...` + `pg_upgrade` or backup-restore
3. No application code changes required—simply update the compose image tag and restart

### For New Deployments

All new deployments and local development now default to postgres:17-alpine.

## Future Considerations

- **Postgres 18**: When released (Oct 2025), this pattern can be repeated: update all image tags, verify compose validation, commit once
- **Host Migration**: If moving away from Hetzner, verify the new host supports Postgres 17 before deployment
- **End-of-Life**: Postgres 17 reaches EOL Nov 2027; plan for 18 upgrade 6-9 months before

---

**Committed by**: Postgres version reconciliation task (2026-04-26)  
**Branch**: `infra/postgres-version-reconcile`  
**Related Files**: `.dev/runbooks/postgres-bootstrap.md` (reference for manual bootstrap if needed)
