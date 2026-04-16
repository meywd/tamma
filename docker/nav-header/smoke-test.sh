#!/usr/bin/env bash
# Tamma Nav Bar — Smoke tests (Story 16.4)
# Run against a deployed environment to verify nav assets + injection.
#
# Usage:
#   ./smoke-test.sh                          # default: https://app.tamma.dev
#   TAMMA_BASE=https://app.tamma.dev ./smoke-test.sh
set -euo pipefail

BASE="${TAMMA_BASE:-https://app.tamma.dev}"
PASS=0
FAIL=0

check() {
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "  PASS  $desc"
    PASS=$((PASS + 1))
  else
    echo "  FAIL  $desc"
    FAIL=$((FAIL + 1))
  fi
}

echo "Tamma Nav Bar Smoke Tests (base: $BASE)"
echo "---"

# 1. Nav assets serve with correct CORS
check "Nav JS has CORS header" \
  bash -c "curl -sfI -H 'Origin: https://elsa.tamma.dev' '$BASE/tamma-nav.js' | grep -qi 'access-control-allow-origin'"

# 2. Nav HTML contains expected nav element
check "Nav HTML contains <nav id=\"tamma-nav\">" \
  bash -c "curl -sf '$BASE/tamma-nav.html' | grep -q 'id=\"tamma-nav\"'"

# 3. Nav script is valid JS (no syntax errors)
check "Nav JS is valid JavaScript" \
  bash -c "curl -sf '$BASE/tamma-nav.js' | node --check -"

# 4. Nav injected into elsa.tamma.dev
check "Nav script injected into elsa.tamma.dev" \
  bash -c "curl -sfL https://elsa.tamma.dev/ 2>/dev/null | grep -q 'tamma-nav.js'"

# 5. Nav injected into logs.tamma.dev
check "Nav script injected into logs.tamma.dev" \
  bash -c "curl -sfL https://logs.tamma.dev/ 2>/dev/null | grep -q 'tamma-nav.js'"

# 6. Auth /me endpoint returns 401 without cookie
check "GET /api/auth/me returns 401 without cookie" \
  bash -c "[ \$(curl -s -o /dev/null -w '%{http_code}' '$BASE/api/auth/me') = '401' ]"

echo "---"
echo "Results: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ] || exit 1
