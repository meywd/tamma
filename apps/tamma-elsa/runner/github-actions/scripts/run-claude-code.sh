#!/usr/bin/env bash
# tamma-runner-version: 1.0.0
#
# Tamma runner — claude-code provider (story 40-1, AC2/AC6).
#
# Contract with tamma-agent.yml: read .tamma/INSTRUCTIONS.md, edit the working
# tree in place, and leave behind
#   .tamma/agent.log         human-readable log (tail becomes agent_log_summary)
#   .tamma/tokens            integer token count (0 if unknown)
#   .tamma/agent-version     agent CLI version string
#   .tamma/agent-error.txt   one-line reason, ONLY when this script fails
# Committing, pushing and result.json assembly belong to the workflow and to
# collect-results.sh — never to this script.
#
# Exit codes: 0 ok · 64 usage/precondition · 70 agent failed · 78 missing secret.
# Any non-zero exit is fail-SAFE: the workflow still collects and uploads a
# result.json with success:false, so Tamma sees a reason instead of a silence.

set -euo pipefail

TAMMA_DIR="${TAMMA_DIR:-.tamma}"
LOG="${TAMMA_DIR}/agent.log"
mkdir -p "$TAMMA_DIR"
: > "$LOG"

fail() {
  # $1 = exit code, $2 = reason. The reason is what Tamma shows the user, so it
  # must name the fix — never dump command output that could carry a key.
  printf '%s\n' "$2" > "${TAMMA_DIR}/agent-error.txt"
  printf '%s\n' "$2" >> "$LOG"
  echo "::error::$2"
  exit "$1"
}

[ -f "${TAMMA_DIR}/INSTRUCTIONS.md" ] || fail 64 "Missing ${TAMMA_DIR}/INSTRUCTIONS.md — the prepare step did not run."

# D7 — fail LOUD on a missing key, never silently degrade into a no-op run that
# looks like "the agent had nothing to do".
[ -n "${ANTHROPIC_API_KEY:-}" ] || fail 78 \
  "ANTHROPIC_API_KEY secret is not set in this repository. Add it under Settings → Secrets and variables → Actions."

CLI_VERSION="${TAMMA_CLAUDE_CODE_VERSION:-latest}"
echo "Installing @anthropic-ai/claude-code@${CLI_VERSION}" >> "$LOG"
# Pinning is the caller's choice (repo variable TAMMA_CLAUDE_CODE_VERSION);
# 'latest' keeps a fresh install working, a pin keeps a run reproducible.
npm install -g "@anthropic-ai/claude-code@${CLI_VERSION}" >> "$LOG" 2>&1 \
  || fail 70 "Failed to install the claude-code CLI (see the run log)."

command -v claude >/dev/null 2>&1 || fail 70 "claude-code CLI is not on PATH after install."
claude --version > "${TAMMA_DIR}/agent-version" 2>/dev/null || echo "unknown" > "${TAMMA_DIR}/agent-version"

# Default permission mode: the agent must be able to edit files AND run the
# repo's tests unattended. This runner is an ephemeral, repo-scoped CI VM whose
# only credentials are the ones the repo owner put there, which is what makes
# unattended tool use acceptable here and nowhere else. Override with the repo
# variable TAMMA_CLAUDE_EXTRA_ARGS (it replaces these defaults entirely).
DEFAULT_ARGS="--permission-mode bypassPermissions"
read -r -a CLAUDE_ARGS <<< "${TAMMA_CLAUDE_EXTRA_ARGS:-$DEFAULT_ARGS}"

OUTPUT_JSON="${TAMMA_DIR}/agent-output.json"
set +e
# The prompt arrives on stdin: INSTRUCTIONS.md carries an untrusted plan slice
# and can exceed the OS argument limit.
claude --print --output-format json "${CLAUDE_ARGS[@]}" \
  < "${TAMMA_DIR}/INSTRUCTIONS.md" > "$OUTPUT_JSON" 2>> "$LOG"
AGENT_EXIT=$?
set -e

# Token accounting is best-effort: a missing/unparseable usage block must not
# fail a run that produced good code.
TOKENS=0
if [ -s "$OUTPUT_JSON" ] && command -v jq >/dev/null 2>&1; then
  TOKENS="$(jq -r '
      (.usage.input_tokens // 0)
    + (.usage.output_tokens // 0)
    + (.usage.cache_creation_input_tokens // 0)
    + (.usage.cache_read_input_tokens // 0)' "$OUTPUT_JSON" 2>/dev/null || echo 0)"
  case "$TOKENS" in (''|*[!0-9]*) TOKENS=0 ;; esac
  jq -r '.result // ""' "$OUTPUT_JSON" >> "$LOG" 2>/dev/null || true
fi
printf '%s\n' "$TOKENS" > "${TAMMA_DIR}/tokens"

[ "$AGENT_EXIT" -eq 0 ] || fail 70 "claude-code exited with ${AGENT_EXIT}."

# `is_error` is the agent's own verdict; a zero exit with is_error:true is still
# a failed task.
if [ -s "$OUTPUT_JSON" ] && command -v jq >/dev/null 2>&1; then
  if [ "$(jq -r '.is_error // false' "$OUTPUT_JSON" 2>/dev/null)" = "true" ]; then
    fail 70 "claude-code reported an error result."
  fi
fi

echo "claude-code finished successfully." >> "$LOG"
