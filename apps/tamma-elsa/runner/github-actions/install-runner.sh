#!/usr/bin/env bash
# tamma-runner-version: 1.0.0
#
# Tamma runner installer — the self-serve half of story 40-1 AC7.
#
# Copies the canonical runner into a repository:
#   .github/workflows/tamma-agent.yml
#   .github/tamma/scripts/run-claude-code.sh
#   .github/tamma/scripts/collect-results.sh
#
# It reports the same four states the App-driven scaffold path reports, and it
# NEVER clobbers a customized copy without being told to (D5):
#   absent      → installs                                   (exit 0)
#   current     → no-op                                      (exit 0)
#   drifted     → needs --upgrade  (version marker differs)  (exit 3)
#   customized  → needs --force    (same version, edited)    (exit 4)
#
# SaaS users: Tamma can do this for you through the GitHub App once the
# server-side scaffold endpoint lands; until then this script is the install path.
# Single-user (self-hosted, no GitHub App): you do not need this file at all —
# the engine runs the agent locally. See README.md § single-user.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$SCRIPT_DIR"
TARGET_REPO="."
MODE="install"

usage() {
  cat <<'USAGE'
Usage: install-runner.sh [--repo <path>] [--from <dir>] [--check|--upgrade|--force]

  --repo <path>   Repository to install into (default: current directory)
  --from <dir>    Directory holding the canonical runner files (default: this script's dir)
  --check         Report the install state and exit; change nothing
  --upgrade       Replace an installed copy whose version marker differs
  --force         Replace an installed copy even if it was customized

Exit: 0 installed/current · 2 usage · 3 drifted (needs --upgrade) · 4 customized (needs --force)
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) TARGET_REPO="${2:?--repo needs a path}"; shift 2 ;;
    --from) SOURCE_DIR="${2:?--from needs a path}"; shift 2 ;;
    --check) MODE="check"; shift ;;
    --upgrade) MODE="upgrade"; shift ;;
    --force) MODE="force"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[ -d "$TARGET_REPO" ] || { echo "no such directory: $TARGET_REPO" >&2; exit 2; }
[ -f "${SOURCE_DIR}/tamma-agent.yml" ] || { echo "no tamma-agent.yml under ${SOURCE_DIR}" >&2; exit 2; }

version_of() { sed -n 's/^# tamma-runner-version: //p' "$1" | head -n1; }

SRC_WORKFLOW="${SOURCE_DIR}/tamma-agent.yml"
DST_WORKFLOW="${TARGET_REPO}/.github/workflows/tamma-agent.yml"
SHIPPED_VERSION="$(version_of "$SRC_WORKFLOW")"

# ── State ──────────────────────────────────────────────────────────────────
if [ ! -f "$DST_WORKFLOW" ]; then
  STATE="absent"
elif cmp -s "$SRC_WORKFLOW" "$DST_WORKFLOW"; then
  STATE="current"
elif [ "$(version_of "$DST_WORKFLOW")" != "$SHIPPED_VERSION" ]; then
  STATE="drifted"
else
  # Same version marker, different bytes — someone edited it on purpose.
  STATE="customized"
fi

echo "tamma runner ${SHIPPED_VERSION} → ${TARGET_REPO}: ${STATE}"

if [ "$MODE" = "check" ]; then
  case "$STATE" in
    drifted) exit 3 ;;
    customized) exit 4 ;;
    *) exit 0 ;;
  esac
fi

case "$STATE" in
  current) echo "Already up to date."; exit 0 ;;
  drifted)
    if [ "$MODE" != "upgrade" ] && [ "$MODE" != "force" ]; then
      echo "Installed copy is version '$(version_of "$DST_WORKFLOW")'. Re-run with --upgrade to replace it." >&2
      exit 3
    fi
    ;;
  customized)
    if [ "$MODE" != "force" ]; then
      echo "Installed copy has been edited locally. Re-run with --force to overwrite it (your changes are lost)." >&2
      exit 4
    fi
    ;;
esac

# ── Install ────────────────────────────────────────────────────────────────
# The three files move as a SET: the workflow refuses to run against scripts
# carrying a different version marker, so a half-install fails loud rather than
# running a mismatched contract.
mkdir -p "${TARGET_REPO}/.github/workflows" "${TARGET_REPO}/.github/tamma/scripts"
cp "$SRC_WORKFLOW" "$DST_WORKFLOW"
for script in run-claude-code.sh collect-results.sh; do
  cp "${SOURCE_DIR}/scripts/${script}" "${TARGET_REPO}/.github/tamma/scripts/${script}"
  chmod +x "${TARGET_REPO}/.github/tamma/scripts/${script}"
done

cat <<EOF
Installed:
  .github/workflows/tamma-agent.yml
  .github/tamma/scripts/run-claude-code.sh
  .github/tamma/scripts/collect-results.sh

Next: commit these files, then add the ANTHROPIC_API_KEY secret under
Settings → Secrets and variables → Actions. Tamma dispatches the workflow;
it never sees that key.
EOF
