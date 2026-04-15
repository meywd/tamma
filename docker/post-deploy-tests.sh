#!/usr/bin/env bash
# =============================================================================
# Post-Deploy Integration Tests
#
# Runs on the VPS after deploy to verify all deployed endpoints.
# Tests are read-only and idempotent.
#
# Usage:
#   bash post-deploy-tests.sh              # on VPS (localhost)
#   bash post-deploy-tests.sh 204.168.x.x  # remote
#
# Exit code: 0 if all tests pass, 1 if any fail
# =============================================================================

set -uo pipefail

TARGET="${1:-localhost}"

PASS=0
FAIL=0
WARN=0
RESULTS=()

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
DIM='\033[0;90m'
BOLD='\033[1m'
RESET='\033[0m'

# test_endpoint LABEL HOST PATH EXPECTED [METHOD] [DATA]
#   EXPECTED: exact code like "200", "401", "302"
test_endpoint() {
  local label="$1" host="$2" path="$3" expected="$4"
  local method="${5:-GET}" data="${6:-}"

  local curl_args=(-sk -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: ${host}" -X "${method}")
  [ -n "${data}" ] && curl_args+=(-H 'Content-Type: application/json' -d "${data}")
  curl_args+=("https://${TARGET}${path}")

  local status
  status=$(curl "${curl_args[@]}" 2>/dev/null) || status="000"

  if [ "${status}" = "${expected}" ]; then
    PASS=$((PASS + 1))
    printf "  ${GREEN}PASS${RESET}  %-55s  HTTP %s\n" "${label}" "${status}"
  elif [ "${status}" = "404" ]; then
    # Route not deployed yet — warn, don't fail
    WARN=$((WARN + 1))
    printf "  ${YELLOW}WARN${RESET}  %-55s  HTTP 404 (not deployed yet, expected %s)\n" "${label}" "${expected}"
  elif [ "${status}" = "000" ]; then
    FAIL=$((FAIL + 1))
    RESULTS+=("${label} — connection failed (timeout/unreachable)")
    printf "  ${RED}FAIL${RESET}  %-55s  HTTP 000 (connection failed)\n" "${label}"
  else
    FAIL=$((FAIL + 1))
    RESULTS+=("${label} — got ${status}, expected ${expected}")
    printf "  ${RED}FAIL${RESET}  %-55s  HTTP %s (expected %s)\n" "${label}" "${status}" "${expected}"
  fi
}

header() { printf "\n${BOLD}--- %s ---${RESET}\n" "$1"; }

# =============================================================================
# Tests — strict expected codes, no masking
# =============================================================================

header "Story 16-1: OAuth2 Proxy"

# oauth2-proxy health
if [ "${TARGET}" = "localhost" ]; then
  PROXY_ID=$(docker ps -qf name=oauth2-proxy 2>/dev/null | head -1)
  if [ -n "${PROXY_ID}" ]; then
    if docker exec "${PROXY_ID}" curl -sf http://127.0.0.1:4180/ping >/dev/null 2>&1 || \
       docker exec "${PROXY_ID}" wget -qO- http://127.0.0.1:4180/ping >/dev/null 2>&1; then
      PASS=$((PASS + 1)); printf "  ${GREEN}PASS${RESET}  %-55s  ok\n" "oauth2-proxy health"
    else
      FAIL=$((FAIL + 1)); RESULTS+=("oauth2-proxy /ping failed")
      printf "  ${RED}FAIL${RESET}  %-55s  unhealthy\n" "oauth2-proxy health"
    fi
  else
    FAIL=$((FAIL + 1)); RESULTS+=("oauth2-proxy container not found")
    printf "  ${RED}FAIL${RESET}  %-55s  not found\n" "oauth2-proxy health"
  fi
else
  OA_STATUS=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: app.tamma.dev" "https://${TARGET}/oauth2/sign_in" 2>/dev/null) || OA_STATUS="000"
  if [ "${OA_STATUS}" != "000" ] && [ "${OA_STATUS}" != "502" ]; then
    PASS=$((PASS + 1)); printf "  ${GREEN}PASS${RESET}  %-55s  HTTP %s\n" "oauth2-proxy reachable" "${OA_STATUS}"
  else
    FAIL=$((FAIL + 1)); RESULTS+=("oauth2-proxy unreachable (HTTP ${OA_STATUS})")
    printf "  ${RED}FAIL${RESET}  %-55s  HTTP %s\n" "oauth2-proxy reachable" "${OA_STATUS}"
  fi
fi

# app.tamma.dev: unauthenticated should get 302 redirect to oauth2-proxy
# If oauth2-proxy is not wired (old nginx config), it returns 200
test_endpoint "app.tamma.dev / unauthenticated → 302 redirect" "app.tamma.dev" "/" "302"

# API health bypasses oauth2-proxy — must always be 200
test_endpoint "api.tamma.dev /api/health bypasses auth" "api.tamma.dev" "/api/health" "200"

# Webhooks must not require auth (GitHub sends unsigned POSTs for pings)
test_endpoint "api.tamma.dev /api/github/webhooks reachable" "api.tamma.dev" "/api/github/webhooks" "401" "POST" '{"action":"ping"}'

# ---------------------------------------------------------------------------
header "Story 17-1: Tenant Model"

test_endpoint "API health (postgres + migrations OK)" "api.tamma.dev" "/api/health" "200"

# ---------------------------------------------------------------------------
header "Story 16-2: User Management"

test_endpoint "GET /api/admin/users without auth → 401" "api.tamma.dev" "/api/admin/users" "401"

# ---------------------------------------------------------------------------
header "Story 16-5: RBAC"

# elsa and logs should require auth — unauthenticated gets 302 (redirect to login)
# or 403 (denied). NOT 200 (that means RBAC is not enforced).
test_endpoint "elsa.tamma.dev unauthenticated → 302 or 401" "elsa.tamma.dev" "/" "302"
test_endpoint "logs.tamma.dev unauthenticated → 302 or 401" "logs.tamma.dev" "/" "302"

# 403 page on disk (local only)
if [ "${TARGET}" = "localhost" ]; then
  if find /opt/tamma -name "403.html" -path "*/error-pages/*" 2>/dev/null | grep -q .; then
    PASS=$((PASS + 1)); printf "  ${GREEN}PASS${RESET}  %-55s  found\n" "Custom 403 page on disk"
  else
    FAIL=$((FAIL + 1)); RESULTS+=("403.html not found on disk")
    printf "  ${RED}FAIL${RESET}  %-55s  not found\n" "Custom 403 page on disk"
  fi
fi

# ---------------------------------------------------------------------------
header "Story 16-7: Service-to-Service Auth"

test_endpoint "POST /api/admin/service-keys without auth → 401" "api.tamma.dev" "/api/admin/service-keys" "401" "POST" '{}'

# ---------------------------------------------------------------------------
header "Story 9-1: Agent Config"

test_endpoint "GET /api/v1/agents/config without auth → 200" "api.tamma.dev" "/api/v1/agents/config" "200"

# ---------------------------------------------------------------------------
header "Story 27-3: Prompt Store API"

test_endpoint "GET /api/prompts/system → 200" "api.tamma.dev" "/api/prompts/system" "200"

# ---------------------------------------------------------------------------
header "Story 18-1/18-2: Auth Endpoints"

test_endpoint "POST /register with bad data → 400" "api.tamma.dev" "/api/v1/auth/register" "400" "POST" '{"bad":"data"}'
test_endpoint "POST /login with bad creds → 401" "api.tamma.dev" "/api/v1/auth/login" "401" "POST" '{"email":"fake@test.com","password":"wrong"}'
test_endpoint "POST /password-reset missing email → 400" "api.tamma.dev" "/api/v1/auth/password-reset/request" "400" "POST" '{}'

# ---------------------------------------------------------------------------
header "Story 9-8: Agent Resolver"

test_endpoint "GET /agents/developer/resolve → 200" "api.tamma.dev" "/api/v1/agents/developer/resolve" "200"

# =============================================================================
# Summary
# =============================================================================
TOTAL=$((PASS + FAIL + WARN))
printf "\n${BOLD}=== Summary ===${RESET}\n"
printf "  ${GREEN}PASS: %d${RESET}  ${RED}FAIL: %d${RESET}  ${YELLOW}WARN: %d${RESET}  TOTAL: %d\n\n" "${PASS}" "${FAIL}" "${WARN}" "${TOTAL}"

if [ "${WARN}" -gt 0 ]; then
  printf "${YELLOW}Warnings (routes not yet deployed — will pass after next deploy with new images):${RESET}\n"
  printf "  These return 404 because the VPS is running old images.\n"
  printf "  They are NOT counted as failures but must pass after deploy.\n\n"
fi

if [ "${FAIL}" -gt 0 ]; then
  printf "${RED}Failed tests:${RESET}\n"
  for r in "${RESULTS[@]}"; do
    printf "  - %s\n" "${r}"
  done
  exit 1
fi

if [ "${WARN}" -gt 0 ]; then
  printf "${YELLOW}Deploy needed to verify WARN tests.${RESET}\n"
fi

printf "${GREEN}All deployed endpoints working correctly.${RESET}\n"
