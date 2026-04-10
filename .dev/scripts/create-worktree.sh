#!/usr/bin/env bash
# Usage: ./create-worktree.sh feat/story-16-1-oauth-proxy
set -e
BRANCH="$1"
if [[ -z "$BRANCH" ]]; then
  echo "Usage: $0 <branch-name>"
  exit 1
fi
WORKTREE_NAME=$(basename "$BRANCH")
WORKTREE_PATH="/home/meywd/tamma-worktrees/${WORKTREE_NAME}"
cd /home/meywd/tamma
git fetch origin
git worktree add "$WORKTREE_PATH" -b "$BRANCH" origin/main
cd "$WORKTREE_PATH"
pnpm install
echo "Worktree ready: $WORKTREE_PATH"
