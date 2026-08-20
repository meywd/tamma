#!/usr/bin/env bash
# tamma-runner-version: 1.0.0
#
# Tamma runner — result collection (story 40-1, AC3/AC5).
#
# Writes .tamma/result.json: the ONLY thing Tamma reads back from a run. The
# workflow uploads it as the `tamma-result` artifact and
# AgentResultArtifactParser.ParseResultJson decodes it. Every key below is read
# by that parser; the key set is pinned from the C# side by RunnerContractTests,
# so adding or renaming one here without changing the parser fails Tamma's build.
#
# This script must SUCCEED even when everything else failed — a run with no
# result.json is indistinguishable, from Tamma's side, from a lost run. It never
# calls `exit 1` on a missing input; it reports the gap in `error_message`.
#
# Metadata only. Never write repository source, secrets, or raw environment into
# the artifact — it leaves the customer's security boundary.

set -uo pipefail

TAMMA_DIR="${TAMMA_DIR:-.tamma}"
RESULT_PATH="${TAMMA_DIR}/result.json"
mkdir -p "$TAMMA_DIR"

# tamma-result-keys: the exact key set AgentResultArtifactParser reads. Keep in
# lockstep with result.schema.json — RunnerContractTests asserts all three agree.
TAMMA_RESULT_KEYS="success task issue_number branch_name tamma_session_id files_changed pr_number commit_sha error_message agent_log_summary tokens_used duration_seconds agent_provider agent_version"

# Parser caps (AgentResultArtifactParser). Exceeding them is not an error on the
# C# side — it silently truncates — but truncating here keeps the artifact small
# and the reported values honest.
MAX_LOG_CHARS=32000
MAX_FILES=2000

TASK="${TAMMA_TASK:-implement}"
BRANCH="${TAMMA_BRANCH_NAME:-}"
SESSION="${TAMMA_SESSION_ID:-}"
PROVIDER="${TAMMA_AGENT_PROVIDER:-claude-code}"
AGENT_OUTCOME="${TAMMA_AGENT_OUTCOME:-}"
PUSH_OUTCOME="${TAMMA_PUSH_OUTCOME:-}"

ISSUE_NUMBER="${TAMMA_ISSUE_NUMBER:-0}"
case "$ISSUE_NUMBER" in (''|*[!0-9]*) ISSUE_NUMBER=0 ;; esac

# ── Duration ───────────────────────────────────────────────────────────────
NOW="$(date +%s)"
START="$NOW"
[ -f "${TAMMA_DIR}/start-epoch" ] && START="$(tr -dc '0-9' < "${TAMMA_DIR}/start-epoch")"
[ -n "$START" ] || START="$NOW"
DURATION=$(( NOW - START ))
[ "$DURATION" -ge 0 ] || DURATION=0

# ── Tokens ─────────────────────────────────────────────────────────────────
TOKENS=0
[ -f "${TAMMA_DIR}/tokens" ] && TOKENS="$(tr -dc '0-9' < "${TAMMA_DIR}/tokens")"
[ -n "$TOKENS" ] || TOKENS=0

# ── Agent version ──────────────────────────────────────────────────────────
AGENT_VERSION=""
[ -f "${TAMMA_DIR}/agent-version" ] && AGENT_VERSION="$(head -c 200 "${TAMMA_DIR}/agent-version" | tr -d '\r\n')"

# ── Git state ──────────────────────────────────────────────────────────────
# files_changed is what THIS run added on top of the branch point, so a re-run
# on an already-implemented task reports [] instead of the whole branch.
COMMIT_SHA=""
FILES_FILE="${TAMMA_DIR}/files-changed.txt"
: > "$FILES_FILE"
if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
  COMMIT_SHA="$(git rev-parse HEAD 2>/dev/null || echo '')"
  BASE_SHA=""
  [ -f "${TAMMA_DIR}/base-sha" ] && BASE_SHA="$(tr -dc '0-9a-f' < "${TAMMA_DIR}/base-sha")"
  if [ -n "$BASE_SHA" ] && [ "$BASE_SHA" != "$COMMIT_SHA" ]; then
    git diff --name-only "${BASE_SHA}" HEAD 2>/dev/null | head -n "$MAX_FILES" > "$FILES_FILE" || true
  fi
  # Uncommitted edits mean the agent ran but the push step did not (failure or
  # cancellation) — still report them, they are what the agent touched.
  git diff --name-only 2>/dev/null | head -n "$MAX_FILES" >> "$FILES_FILE" || true
  sort -u -o "$FILES_FILE" "$FILES_FILE" 2>/dev/null || true
fi

# ── PR number (optional) ───────────────────────────────────────────────────
# The Tamma cycle opens the PR before the task loop, so this is a convenience:
# null is a valid answer and the collector falls back to its own PR lookup.
PR_NUMBER="null"
if [ -n "$BRANCH" ] && command -v gh >/dev/null 2>&1 && [ -n "${GH_TOKEN:-}" ]; then
  found="$(gh pr list --head "$BRANCH" --state open --json number --jq '.[0].number' 2>/dev/null || echo '')"
  case "$found" in (''|*[!0-9]*) : ;; (*) PR_NUMBER="$found" ;; esac
fi

# ── Verdict ────────────────────────────────────────────────────────────────
SUCCESS=false
ERROR_MESSAGE=""
if [ -f "${TAMMA_DIR}/agent-error.txt" ]; then
  ERROR_MESSAGE="$(head -c 2000 "${TAMMA_DIR}/agent-error.txt" | tr '\r\n' '  ')"
fi
case "$AGENT_OUTCOME" in
  success)
    if [ "$PUSH_OUTCOME" = "failure" ]; then
      ERROR_MESSAGE="${ERROR_MESSAGE:-Agent succeeded but pushing to ${BRANCH} failed.}"
    else
      SUCCESS=true
      ERROR_MESSAGE=""
    fi
    ;;
  cancelled)
    ERROR_MESSAGE="${ERROR_MESSAGE:-Agent step was cancelled (job timeout or manual cancellation).}"
    ;;
  '')
    ERROR_MESSAGE="${ERROR_MESSAGE:-Agent step did not run — an earlier step failed.}"
    ;;
  *)
    ERROR_MESSAGE="${ERROR_MESSAGE:-Agent step outcome: ${AGENT_OUTCOME}.}"
    ;;
esac

# ── Log summary ────────────────────────────────────────────────────────────
LOG_SUMMARY=""
if [ -f "${TAMMA_DIR}/agent.log" ]; then
  LOG_SUMMARY="$(tail -c "$MAX_LOG_CHARS" "${TAMMA_DIR}/agent.log")"
fi

# ── Emit ───────────────────────────────────────────────────────────────────
if command -v jq >/dev/null 2>&1; then
  # jq owns all escaping: every value below is untrusted text (agent output,
  # branch names, plan-derived paths).
  jq -n \
    --argjson success "$SUCCESS" \
    --arg task "$TASK" \
    --argjson issue_number "$ISSUE_NUMBER" \
    --arg branch_name "$BRANCH" \
    --arg tamma_session_id "$SESSION" \
    --rawfile files_raw "$FILES_FILE" \
    --argjson pr_number "$PR_NUMBER" \
    --arg commit_sha "$COMMIT_SHA" \
    --arg error_message "$ERROR_MESSAGE" \
    --arg agent_log_summary "$LOG_SUMMARY" \
    --argjson tokens_used "$TOKENS" \
    --argjson duration_seconds "$DURATION" \
    --arg agent_provider "$PROVIDER" \
    --arg agent_version "$AGENT_VERSION" \
    '{
      success: $success,
      task: $task,
      issue_number: $issue_number,
      branch_name: $branch_name,
      tamma_session_id: $tamma_session_id,
      files_changed: ($files_raw | split("\n") | map(select(length > 0))),
      pr_number: $pr_number,
      commit_sha: $commit_sha,
      error_message: (if $error_message == "" then null else $error_message end),
      agent_log_summary: (if $agent_log_summary == "" then null else $agent_log_summary end),
      tokens_used: $tokens_used,
      duration_seconds: $duration_seconds,
      agent_provider: $agent_provider,
      agent_version: (if $agent_version == "" then null else $agent_version end)
    }' > "$RESULT_PATH"

  # Self-check: the emitted key set must be exactly the contract. A drift here
  # is caught in the customer's own run, not only in Tamma's build.
  emitted="$(jq -r 'keys_unsorted | join(" ")' "$RESULT_PATH" | tr ' ' '\n' | sort | tr '\n' ' ')"
  expected="$(printf '%s' "$TAMMA_RESULT_KEYS" | tr ' ' '\n' | sort | tr '\n' ' ')"
  if [ "$emitted" != "$expected" ]; then
    echo "::error::result.json key set drifted from the Tamma contract."
    echo "  emitted:  ${emitted}"
    echo "  expected: ${expected}"
  fi
else
  # jq is missing (an under-provisioned self-hosted runner). Emit a minimal but
  # SCHEMA-COMPLETE artifact by hand — no arbitrary text is interpolated, so
  # there is nothing to escape and nothing to get wrong.
  safe_branch="$(printf '%s' "$BRANCH" | tr -cd 'A-Za-z0-9._/-')"
  safe_session="$(printf '%s' "$SESSION" | tr -cd 'A-Za-z0-9._-')"
  safe_task="$(printf '%s' "$TASK" | tr -cd 'A-Za-z0-9._-')"
  safe_provider="$(printf '%s' "$PROVIDER" | tr -cd 'A-Za-z0-9._-')"
  safe_sha="$(printf '%s' "$COMMIT_SHA" | tr -cd '0-9a-f')"
  cat > "$RESULT_PATH" <<EOF
{
  "success": false,
  "task": "${safe_task}",
  "issue_number": ${ISSUE_NUMBER},
  "branch_name": "${safe_branch}",
  "tamma_session_id": "${safe_session}",
  "files_changed": [],
  "pr_number": null,
  "commit_sha": "${safe_sha}",
  "error_message": "jq is not installed on this runner, so Tamma could not assemble a full result. Install jq on the runner image.",
  "agent_log_summary": null,
  "tokens_used": ${TOKENS},
  "duration_seconds": ${DURATION},
  "agent_provider": "${safe_provider}",
  "agent_version": null
}
EOF
fi

echo "Wrote ${RESULT_PATH}:"
sed -e 's/^/  /' "$RESULT_PATH" | head -n 40
