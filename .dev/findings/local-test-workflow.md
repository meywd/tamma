# Local Test Workflow

Run these commands before pushing any branch.

## Unit Tests (fast, run always)

```bash
pnpm test:unit
```

## Integration Tests (requires shared test DB)

```bash
TAMMA_TEST_DB_URL=postgres://postgres@localhost:5432/tamma_test_layered pnpm test:integration
```

## Type Check

```bash
pnpm build
```

## Lint & Format

```bash
pnpm lint && pnpm format
```

## Scoped to a Package

```bash
pnpm test --filter @tamma/api
pnpm test --filter @tamma/shared
```
