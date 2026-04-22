# Shared Test Database

## Connection

```
TAMMA_TEST_DB_URL=postgres://postgres@localhost:5432/tamma_test_layered
```

## Schema Version

Starts at migration 007. Do not apply migrations 008+ until the corresponding story lands.

## Setup

```bash
psql -h localhost -U postgres -c "CREATE DATABASE tamma_test_layered;"
for f in database/migrations/00{1..7}_*.sql; do
  psql -h localhost -U postgres -d tamma_test_layered -f "$f"
done
```

## Rules

- All integration tests must use this database (not the dev or production database).
- Migrations are additive — never drop columns or tables.
- Each worktree runs its own `pnpm test:unit` against local code but shares this DB for integration tests.
