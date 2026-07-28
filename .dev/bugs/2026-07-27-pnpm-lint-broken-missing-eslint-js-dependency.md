# Bug: `pnpm lint` fails on a clean install — `@eslint/js` is imported but not a declared dependency

**Date Discovered**: 2026-07-27
**Reporter**: Claude (Epic 45 implementation — found while trying to run lint verification)
**Severity**: 🟡 Medium
**Status**: 🐛 Open

## 📋 Summary

`eslint.config.js:1` does `import eslint from '@eslint/js';`, but the root `package.json`
declares only `eslint` (`^10.2.1`) — not `@eslint/js`. Under pnpm's strict (non-hoisted)
`node_modules`, a transitive package is not resolvable from the workspace root, so on a clean
`pnpm install --frozen-lockfile` every `pnpm lint` invocation dies with:

```
Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@eslint/js' imported from /home/user/tamma/eslint.config.js
```

Verified at commit `d0e0a8f` (merge of PR #505) in a fresh environment — the failure predates
any Epic 45 change (no `package.json`/lockfile/eslint-config edits in that work). Lint is not a
CI gate today, which is why this has not surfaced in a workflow run.

## 🔬 Steps to Reproduce

```bash
git checkout d0e0a8f
pnpm install --frozen-lockfile
pnpm lint   # ERR_MODULE_NOT_FOUND: @eslint/js
node -e "require.resolve('@eslint/js')"   # also fails
```

## 💊 Suggested Fix

Add `@eslint/js` (matching the eslint 10.x line) to the root `devDependencies`. One line plus a
lockfile update. Not done in Epic 45 because the root `package.json` is outside that epic's
file lane.

## Related

- `eslint.config.js:1`
- `package.json:45` (`"eslint": "^10.2.1"` — the only eslint package declared)
