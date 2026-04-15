#!/usr/bin/env bash
# =============================================================================
# Post-Deploy Integration Tests
#
# Runs on the VPS after deploy to verify all deployed endpoints behave
# correctly. Tests are read-only and idempotent (write tests use invalid
# data to confirm validation, not to create state).
#
# Usage: bash post-deploy-tests.sh
# Exit code: 0 if all critical tests pass, 1 otherwise
# =============================================================================

set -euo pipefail

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
PASS=0
FAIL=0
SKIP=0
RESULTS=()

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
RESET='\033[0m'

# test_endpoint LABEL HOST PATH EXPECTED_STATUS [METHOD] [DATA] [EXTRA_CURL_ARGS...]
#   EXPECTED_STATUS can be:
#     "200"        — exact match
#     "!404"       — must NOT be 404
#     "302|403"    — either 302 or 403
test_endpoint() {
  local label="$1"
  local host="$2"
  local path="$3"
  local expected="$4"
  local method="${5:-GET}"
  local data="${6:-}"
  shift 6 2>/dev/null || true
  local extra_args=("$@")

  local curl_args=(-sk -o /dev/null -w '%{http_code}' -H "Host: ${host}" -X "${method}")
  if [ -n "${data}" ]; then
    curl_args+=(-H 'Content-Type: application/json' -d "${data}")
  fi
  if [ ${#extra_args[@]} -gt 0 ]; then
    curl_args+=("${extra_args[@]}")
  fi
  curl_args+=("https://localhost${path}")

  local status
  status=$(curl "${curl_args[@]}" 2>/dev/null || echo "000")

  local pass=false
  if [[ "${expected}" == *"|"* ]]; then
    # Multiple acceptable codes: "302|403"
    IFS='|' read -ra codes <<< "${expected}"
    for code in "${codes[@]}"; do
      if [ "${status}" = "${code}" ]; then
        pass=true
        break
      fi
    done
  elif [[ "${expected}" == "!"* ]]; then
    # Negation: "!404" means anything except 404
    local neg="${expected#!}"
    if [ "${status}" != "${neg}" ]; then
      pass=true
    fi
  else
    # Exact match
    if [ "${status}" = "${expected}" ]; then
      pass=true
    fi
  fi

  if $pass; then
    PASS=$((PASS + 1))
    RESULTS+=("PASS|${label}|${status}|${expected}")
    printf "  ${GREEN}PASS${RESET}  %-60s  HTTP %s (expected %s)\n" "${label}" "${status}" "${expected}"
  else
    FAIL=$((FAIL + 1))
    RESULTS+=("FAIL|${label}|${status}|${expected}")
    printf "  ${RED}FAIL${RESET}  %-60s  HTTP %s (expected %s)\n" "${label}" "${status}" "${expected}"
  fi
}

print_header() {
  printf "\n${BOLD}--- %s ---${RESET}\n" "$1"
}

# ---------------------------------------------------------------------------
# Tests grouped by story
# ---------------------------------------------------------------------------

print_header "Story 16-1: OAuth2 Proxy"

# oauth2-proxy health (internal docker network — use direct container check)
OAUTH2_HEALTH=$(docker exec "$(docker ps -qf name=oauth2-proxy | head -1)" wget -qO- http://127.0.0.1:4180/ping 2>/dev/null && echo "ok" || echo "fail")
if [ "${OAUTH2_HEALTH}" = "ok" ]; then
  PASS=$((PASS + 1))
  RESULTS+=("PASS|oauth2-proxy /ping (internal)|ok|ok")
  printf "  ${GREEN}PASS${RESET}  %-60s  %s\n" "oauth2-proxy /ping (internal)" "ok"
else
  FAIL=$((FAIL + 1))
  RESULTS+=("FAIL|oauth2-proxy /ping (internal)|${OAUTH2_HEALTH}|ok")
  printf "  ${RED}FAIL${RESET}  %-60s  %s\n" "oauth2-proxy /ping (internal)" "${OAUTH2_HEALTH}"
fi

test_endpoint \
  "app.tamma.dev / redirects to sign_in (302)" \
  "app.tamma.dev" "/" "302"

test_endpoint \
  "api.tamma.dev /api/health accessible (200)" \
  "api.tamma.dev" "/api/health" "200"

test_endpoint \
  "api.tamma.dev /api/github/webhooks not auth-blocked" \
  "api.tamma.dev" "/api/github/webhooks" "!302" "POST" '{"action":"ping"}'

# ---------------------------------------------------------------------------
print_header "Story 17-1: Tenant Model"

test_endpoint \
  "API health returns 200 (postgres + migrations OK)" \
  "api.tamma.dev" "/api/health" "200"

# ---------------------------------------------------------------------------
print_header "Story 16-2: User Management"

test_endpoint \
  "GET /api/admin/users without auth returns 401" \
  "api.tamma.dev" "/api/admin/users" "401"

test_endpoint \
  "GET /api/admin/users endpoint exists (not 404)" \
  "api.tamma.dev" "/api/admin/users" "!404"

# ---------------------------------------------------------------------------
print_header "Story 16-5: RBAC"

test_endpoint \
  "elsa.tamma.dev requires auth (302 or 403)" \
  "elsa.tamma.dev" "/" "302|403"

test_endpoint \
  "logs.tamma.dev requires auth (302 or 403)" \
  "logs.tamma.dev" "/" "302|403"

# Check custom 403 page exists (via nginx error_page directive)
if [ -f /opt/tamma/docker/error-pages/403.html ] || [ -f ./error-pages/403.html ]; then
  PASS=$((PASS + 1))
  RESULTS+=("PASS|Custom 403 error page exists on disk|found|found")
  printf "  ${GREEN}PASS${RESET}  %-60s  %s\n" "Custom 403 error page exists on disk" "found"
else
  # Try in the deploy path
  DEPLOY_403=$(find / -path "*/error-pages/403.html" -type f 2>/dev/null | head -1)
  if [ -n "${DEPLOY_403}" ]; then
    PASS=$((PASS + 1))
    RESULTS+=("PASS|Custom 403 error page exists on disk|${DEPLOY_403}|found")
    printf "  ${GREEN}PASS${RESET}  %-60s  %s\n" "Custom 403 error page exists on disk" "found at ${DEPLOY_403}"
  else
    FAIL=$((FAIL + 1))
    RESULTS+=("FAIL|Custom 403 error page exists on disk|not found|found")
    printf "  ${RED}FAIL${RESET}  %-60s  %s\n" "Custom 403 error page exists on disk" "not found"
  fi
fi

# ---------------------------------------------------------------------------
print_header "Story 16-7: Service-to-Service Auth"

test_endpoint \
  "POST /api/admin/service-keys without auth returns 401" \
  "api.tamma.dev" "/api/admin/service-keys" "401" "POST" '{}'

# ---------------------------------------------------------------------------
print_header "Story 9-1: Agent Config"

test_endpoint \
  "GET /api/v1/agents/config returns 200 or 401" \
  "api.tamma.dev" "/api/v1/agents/config" "200|401"

# ---------------------------------------------------------------------------
print_header "Story 27-3: Prompt Store API"

test_endpoint \
  "GET /api/prompts/system returns data or 401" \
  "api.tamma.dev" "/api/prompts/system" "200|401"

# ---------------------------------------------------------------------------
print_header "Story 18-1/18-2: Auth Endpoints"

test_endpoint \
  "POST /api/v1/auth/register with invalid data returns 400" \
  "api.tamma.dev" "/api/v1/auth/register" "400" "POST" '{"invalid":"data"}'

test_endpoint \
  "POST /api/v1/auth/login with bad creds returns 401" \
  "api.tamma.dev" "/api/v1/auth/login" "401" "POST" '{"email":"nobody@invalid.test","password":"wrongpassword123"}'

test_endpoint \
  "POST /api/v1/auth/password-reset/request missing email returns 400" \
  "api.tamma.dev" "/api/v1/auth/password-reset/request" "400" "POST" '{}'

# ---------------------------------------------------------------------------
print_header "Story 9-8: Agent Resolver"

test_endpoint \
  "GET /api/v1/agents/developer/resolve returns data or 401" \
  "api.tamma.dev" "/api/v1/agents/developer/resolve" "200|401"

# ==========================================================================
# Summary
# ==========================================================================
TOTAL=$((PASS + FAIL + SKIP))
printf "\n${BOLD}========================================${RESET}\n"
printf "${BOLD}  Post-Deploy Test Results${RESET}\n"
printf "${BOLD}========================================${RESET}\n"
printf "  ${GREEN}Passed${RESET}:  %d\n" "${PASS}"
printf "  ${RED}Failed${RESET}:  %d\n" "${FAIL}"
if [ "${SKIP}" -gt 0 ]; then
  printf "  ${YELLOW}Skipped${RESET}: %d\n" "${SKIP}"
fi
printf "  Total:   %d\n" "${TOTAL}"
printf "${BOLD}========================================${RESET}\n"

if [ "${FAIL}" -gt 0 ]; then
  printf "\n${RED}${BOLD}FAILED TESTS:${RESET}\n"
  for result in "${RESULTS[@]}"; do
    IFS='|' read -r verdict label actual expected <<< "${result}"
    if [ "${verdict}" = "FAIL" ]; then
      printf "  ${RED}*${RESET} %s  (got HTTP %s, expected %s)\n" "${label}" "${actual}" "${expected}"
    fi
  done
  printf "\n"
  exit 1
fi

printf "\n${GREEN}All tests passed.${RESET}\n"
exit 0
