#!/usr/bin/env bash
# Story 29-10 — CI guard that fails the build if any forbidden symbol
# appears in production code. See ci/forbidden-symbols.txt for the
# pattern list.
#
# Prefers ripgrep (fast) but falls back to grep -r when rg is absent
# so local development without ripgrep still sees the check.
set -euo pipefail

PATTERNS_FILE="$(dirname "$0")/forbidden-symbols.txt"
ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

# Strip comment + blank lines into a temp patterns file.
TMP_PATTERNS="$(mktemp)"
trap 'rm -f "$TMP_PATTERNS"' EXIT
grep -v '^[[:space:]]*#' "$PATTERNS_FILE" | grep -v '^[[:space:]]*$' > "$TMP_PATTERNS" || true

if [[ ! -s "$TMP_PATTERNS" ]]; then
    echo "check-forbidden-symbols: no patterns active (all lines commented). Exiting 0."
    exit 0
fi

echo "check-forbidden-symbols: scanning apps/ and packages/..."

MATCHES=""
if command -v rg >/dev/null 2>&1; then
    MATCHES="$(
        rg --no-ignore --hidden -f "$TMP_PATTERNS" \
           --glob '!**/Migrations/**' \
           --glob '!**/tests/**' \
           --glob '!**/*.md' \
           --glob '!**/*.test.ts' \
           --glob '!**/*.spec.ts' \
           --glob '!**/*.fixture.*' \
           --glob '!**/SystemSanitizationRules.cs' \
           --glob '!**/common-passwords.txt' \
           --glob '!**/init-fullstack.ts' \
           --glob '!**/config.ts' \
           --glob '!**/docker-compose*.yml' \
           --glob '!**/keys/**' \
           --glob '!ci/forbidden-symbols.txt' \
           apps/ packages/ 2>/dev/null || true
    )"
else
    # grep fallback: slower, no glob excludes in a single pass.
    if [[ -d apps ]] || [[ -d packages ]]; then
        MATCHES="$(
            grep -rEn -f "$TMP_PATTERNS" apps packages 2>/dev/null \
              | grep -v '/Migrations/' \
              | grep -v '/tests/' \
              | grep -v '\.md:' \
              | grep -v '\.test\.' \
              | grep -v '\.spec\.' \
              | grep -v '\.fixture\.' \
              | grep -v 'SystemSanitizationRules\.cs' \
              | grep -v 'common-passwords\.txt' \
              | grep -v 'init-fullstack\.ts' \
              | grep -v 'packages/cli/src/config\.ts' \
              | grep -v 'docker-compose.*\.yml' \
              | grep -v '/keys/' \
              | grep -v 'ci/forbidden-symbols\.txt' \
              || true
        )"
    fi
fi

if [[ -n "$MATCHES" ]]; then
    echo "check-forbidden-symbols: MATCHES FOUND — failing build." >&2
    echo "$MATCHES" >&2
    echo "" >&2
    echo "See ci/forbidden-symbols.txt for the forbidden-symbol list " >&2
    echo "and Story 29-10 for migration guidance." >&2
    exit 1
fi

echo "check-forbidden-symbols: OK — no forbidden symbols in production code."
