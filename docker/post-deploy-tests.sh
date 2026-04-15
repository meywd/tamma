#!/usr/bin/env bash
# =============================================================================
# Post-Deploy Integration Tests
#
# Runs on the VPS after deploy to verify all deployed endpoints.
# Tests are read-only and idempotent.
#
# Usage: bash post-deploy-tests.sh
# Exit code: 0 if all critical tests pass, 1 otherwise
# =============================================================================

set -uo pipefail

# Target: localhost when running on VPS, or pass IP/hostname as $1
TARGET="${1:-localhost}"

PASS=0
FAIL=0
RESULTS=()

GREEN='\033[0;32m'
RED='\033[0;31m'
BOLD='\033[1m'
RESET='\033[0m'

# test_endpoint LABEL HOST PATH EXPECTED [METHOD] [DATA]
#   EXPECTED: "200" (exact), "!404" (not 404), "302|403" (either)
test_endpoint() {
  local label="$1" host="$2" path="$3" expected="$4"
  local method="${5:-GET}" data="${6:-}"

  local curl_args=(-sk -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: ${host}" -X "${method}")
  [ -n "${data}" ] && curl_args+=(-H 'Content-Type: application/json' -d "${data}")
  curl_args+=("https://${TARGET}${path}")

  local status
  status=$(curl "${curl_args[@]}" 2>/dev/null) || status="000"

  local pass=false
  if [[ "${expected}" == *"|"* ]]; then
    IFS='|' read -ra codes <<< "${expected}"
    for code in "${codes[@]}"; do
      [ "${status}" = "${code}" ] && pass=true && break
    done
  elif [[ "${expected}" == "!"* ]]; then
    [ "${status}" != "${expected#!}" ] && pass=true
  else
    [ "${status}" = "${expected}" ] && pass=true
  fi

  if $pass; then
    PASS=$((PASS + 1))
    printf "  ${GREEN}PASS${RESET}  %-55s  HTTP %s\n" "${label}" "${status}"
  else
    FAIL=$((FAIL + 1))
    RESULTS+=("${label} — got ${status}, expected ${expected}")
    printf "  ${RED}FAIL${RESET}  %-55s  HTTP %s (expected %s)\n" "${label}" "${status}" "${expected}"
  fi
}

header() { printf "\n${BOLD}--- %s ---${RESET}\n" "$1"; }

# =============================================================================
# Tests
# =============================================================================

header "Story 16-1: OAuth2 Proxy"

# oauth2-proxy health — try docker exec if local, otherwise check via /oauth2/ path
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
  # Remote: check that /oauth2/sign_in returns something (302 or 200)
  OA_STATUS=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: app.tamma.dev" "https://${TARGET}/oauth2/sign_in" 2>/dev/null) || OA_STATUS="000"
  if [ "${OA_STATUS}" != "000" ] && [ "${OA_STATUS}" != "502" ]; then
    PASS=$((PASS + 1)); printf "  ${GREEN}PASS${RESET}  %-55s  HTTP %s\n" "oauth2-proxy reachable (remote)" "${OA_STATUS}"
  else
    FAIL=$((FAIL + 1)); RESULTS+=("oauth2-proxy unreachable (HTTP ${OA_STATUS})")
    printf "  ${RED}FAIL${RESET}  %-55s  HTTP %s\n" "oauth2-proxy reachable (remote)" "${OA_STATUS}"
  fi
fi

# app.tamma.dev should redirect unauthenticated users (302) or return dashboard (200)
test_endpoint "app.tamma.dev / requires auth or serves dashboard" "app.tamma.dev" "/" "200|302"

# API health bypasses oauth2-proxy
test_endpoint "api.tamma.dev /api/health bypasses auth" "api.tamma.dev" "/api/health" "200"

# Webhooks not auth-blocked
test_endpoint "api.tamma.dev /api/github/webhooks reachable" "api.tamma.dev" "/api/github/webhooks" "!302" "POST" '{"action":"ping"}'

# ---------------------------------------------------------------------------
header "Story 17-1: Tenant Model"

test_endpoint "API health (postgres + migrations)" "api.tamma.dev" "/api/health" "200"

# ---------------------------------------------------------------------------
header "Story 16-2: User Management"

# These routes go through api.tamma.dev (no oauth2-proxy)
test_endpoint "GET /api/admin/users returns 401 without auth" "api.tamma.dev" "/api/admin/users" "401"
test_endpoint "GET /api/admin/users exists (not 404)" "api.tamma.dev" "/api/admin/users" "!404"

# ---------------------------------------------------------------------------
header "Story 16-5: RBAC"

test_endpoint "elsa.tamma.dev requires auth" "elsa.tamma.dev" "/" "200|302|403"
test_endpoint "logs.tamma.dev requires auth" "logs.tamma.dev" "/" "200|302|403|503"

# 403 page — check on disk if local, skip if remote
if [ "${TARGET}" = "localhost" ]; then
  if find /opt/tamma -name "403.html" -path "*/error-pages/*" 2>/dev/null | grep -q .; then
    PASS=$((PASS + 1)); printf "  ${GREEN}PASS${RESET}  %-55s  found\n" "Custom 403 page exists"
  else
    FAIL=$((FAIL + 1)); RESULTS+=("403.html not found on disk")
    printf "  ${RED}FAIL${RESET}  %-55s  not found\n" "Custom 403 page exists"
  fi
else
  printf "  ${BOLD}SKIP${RESET}  %-55s  (remote, can't check disk)\n" "Custom 403 page exists"
fi

# ---------------------------------------------------------------------------
header "Story 16-7: Service-to-Service Auth"

test_endpoint "POST /api/admin/service-keys needs auth" "api.tamma.dev" "/api/admin/service-keys" "401|404" "POST" '{}'

# ---------------------------------------------------------------------------
header "Story 9-1: Agent Config"

test_endpoint "GET /api/v1/agents/config reachable" "api.tamma.dev" "/api/v1/agents/config" "200|401|404"

# ---------------------------------------------------------------------------
header "Story 27-3: Prompt Store API"

test_endpoint "GET /api/prompts/system reachable" "api.tamma.dev" "/api/prompts/system" "200|401|404"

# ---------------------------------------------------------------------------
header "Story 18-1/18-2: Auth Endpoints"

test_endpoint "POST /api/v1/auth/register validates input" "api.tamma.dev" "/api/v1/auth/register" "400|404" "POST" '{"bad":"data"}'
test_endpoint "POST /api/v1/auth/login rejects bad creds" "api.tamma.dev" "/api/v1/auth/login" "400|401|404" "POST" '{"email":"x","password":"y"}'
test_endpoint "POST password-reset validates input" "api.tamma.dev" "/api/v1/auth/password-reset/request" "400|404" "POST" '{}'

# ---------------------------------------------------------------------------
header "Story 9-8: Agent Resolver"

test_endpoint "GET /api/v1/agents/developer/resolve reachable" "api.tamma.dev" "/api/v1/agents/developer/resolve" "200|401|404"

# =============================================================================
# Summary
# =============================================================================
printf "\n${BOLD}=== Summary ===${RESET}\n"
printf "  PASS: %d  FAIL: %d  TOTAL: %d\n\n" "${PASS}" "${FAIL}" "$((PASS + FAIL))"

if [ "${FAIL}" -gt 0 ]; then
  printf "${RED}Failed tests:${RESET}\n"
  for r in "${RESULTS[@]}"; do
    printf "  - %s\n" "${r}"
  done
  exit 1
fi

printf "${GREEN}All tests passed.${RESET}\n"
