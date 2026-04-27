#!/bin/bash
# scripts/db/test-no-argv-leak.sh
#
# Story 28-R2 / PF-S2 verification harness. Runs the bootstrap script
# against a freshly-spawned postgres:17-alpine container, while a
# parallel watcher polls `ps -e -o pid,cmd` for any psql process that
# exposes role-password plaintext via argv. Fails the test (exits 1)
# if the watcher captures any of the canary values; passes (exits 0)
# otherwise.
#
# Run locally:
#   bash scripts/db/test-no-argv-leak.sh
#
# Requires: docker, bash, psql client (libpq-bin / postgresql-client),
# stat/grep/ps coreutils. Linux only — `/proc/<pid>/cmdline` semantics
# differ on macOS.

set -euo pipefail

ADMIN_CANARY='canary-admin-PF-S2-$(date +%s)'
PROVISIONER_CANARY='canary-prov-PF-S2-$(date +%s)'
APP_CANARY='canary-app-PF-S2-$(date +%s)'

# Resolve canaries to actual literal strings (capture once, share).
ADMIN_CANARY="canary-admin-pf-s2-$$-$RANDOM"
PROVISIONER_CANARY="canary-prov-pf-s2-$$-$RANDOM"
APP_CANARY="canary-app-pf-s2-$$-$RANDOM"

CONTAINER="tamma-bootstrap-leaktest-$$"
LOG_DIR="$(mktemp -d -t tamma-leaktest.XXXXXXXX)"
trap 'docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; rm -rf "$LOG_DIR"' EXIT

echo "[leaktest] starting postgres:17-alpine container '$CONTAINER'"
docker run -d --name "$CONTAINER" \
    -e POSTGRES_PASSWORD=superuser-test-secret \
    -e POSTGRES_DB=tamma_control \
    -e POSTGRES_USER=postgres \
    -p 0:5432 \
    postgres:17-alpine \
    >/dev/null

# Wait for postgres to accept connections. Use psql directly because
# pg_isready can return non-zero on first probe even after the server
# binds the listening socket.
READY=0
for i in $(seq 1 60); do
    if docker exec -e PGPASSWORD=superuser-test-secret "$CONTAINER" \
            psql -U postgres -d tamma_control -c 'SELECT 1' >/dev/null 2>&1; then
        READY=1
        break
    fi
    sleep 1
done

if [ "$READY" -ne 1 ]; then
    echo "[leaktest] FAIL: postgres never accepted connections" >&2
    docker logs "$CONTAINER" 2>&1 | tail -20 >&2
    exit 1
fi

# Copy the bootstrap script + roles SQL into the container.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
docker cp "$SCRIPT_DIR/docker-entrypoint-bootstrap.sh" "$CONTAINER:/tmp/bootstrap.sh"
docker cp "$SCRIPT_DIR/postgres-roles.sql" "$CONTAINER:/tmp/postgres-roles.sql"

# Install a psql wrapper that logs its full argv before delegating to
# the real psql. This is the single most reliable way to prove "no
# leak in argv" because it captures the EXACT argv vector the real
# psql sees (not a fast-poll snapshot of /proc/<pid>/cmdline). The
# wrapper writes to /tmp/psql-argv.log inside the container; we
# inspect it after the bootstrap.
WRAPPER_INSTALL="$LOG_DIR/install-wrapper.sh"
cat >"$WRAPPER_INSTALL" <<'INSTALL_EOF'
#!/bin/sh
set -eu
mkdir -p /tmp/scripts/db
cp /tmp/bootstrap.sh /tmp/scripts/db/docker-entrypoint-bootstrap.sh
cp /tmp/postgres-roles.sql /tmp/scripts/db/postgres-roles.sql
chmod +x /tmp/scripts/db/docker-entrypoint-bootstrap.sh

# Find the real psql and move it aside.
REAL_PSQL=$(command -v psql)
mv "$REAL_PSQL" "${REAL_PSQL}.real"

# Install a wrapper at the same path that logs argv then delegates.
cat >"$REAL_PSQL" <<'WRAPPER_EOF'
#!/bin/sh
# argv-leak-test wrapper. Logs the EXACT argv we receive then
# delegates to the real psql.
{
    printf "PSQL_INVOKE pid=%s argc=%s " "$$" "$#"
    i=0
    for a in "$@"; do
        i=$((i + 1))
        printf "argv[%s]=<%s> " "$i" "$a"
    done
    printf "\n"
} >> /tmp/psql-argv.log

# Find ".real" sibling at the same path. exec preserves argv exactly.
self=$(command -v psql)
exec "${self}.real" "$@"
WRAPPER_EOF
chmod +x "$REAL_PSQL"
: >/tmp/psql-argv.log
INSTALL_EOF
docker cp "$WRAPPER_INSTALL" "$CONTAINER:/tmp/install-wrapper.sh"
docker exec "$CONTAINER" sh /tmp/install-wrapper.sh

# Background watcher: poll `ps` inside the container for any process
# argv containing the canaries. Logs hits + ALL psql command lines.
# We deliberately install the watcher script as a file (not inline
# `sh -c`) so the watcher's own argv does NOT contain the literal
# string `psql` (which would self-match and pollute the log).
WATCHER_LOG="$LOG_DIR/watcher.log"
WATCHER_PIDFILE="$LOG_DIR/watcher.pid"
WATCHER_SCRIPT="$LOG_DIR/watcher-inner.sh"
cat >"$WATCHER_SCRIPT" <<'WATCHER_EOF'
#!/bin/sh
target='p''sql'  # split so the watcher script's own argv does not contain "psql"
for d in /proc/[0-9]*; do
    pid="${d##*/}"
    [ -r "$d/cmdline" ] || continue
    cmd=$(tr "\0" " " <"$d/cmdline" 2>/dev/null)
    case "$cmd" in
        *"$target"*)
            # Skip the watcher's own grandparent invocation which may
            # have happened to fork the inner watcher.
            case "$cmd" in
                *"watcher-inner.sh"*) continue ;;
            esac
            printf 'PSQL_ARGV pid=%s cmd=%s\n' "$pid" "$cmd"
            ;;
    esac
done
WATCHER_EOF
chmod +x "$WATCHER_SCRIPT"
docker cp "$WATCHER_SCRIPT" "$CONTAINER:/tmp/watcher-inner.sh"

(
    while true; do
        docker exec "$CONTAINER" /tmp/watcher-inner.sh 2>/dev/null >>"$WATCHER_LOG" || true
        sleep 0.05
    done
) &
WATCHER_PID=$!
echo $WATCHER_PID >"$WATCHER_PIDFILE"

# Run the bootstrap inside the container with the canary passwords.
echo "[leaktest] running bootstrap with canary role-passwords"
set +e
docker exec \
    -e PGPASSWORD=superuser-test-secret \
    -e POSTGRES_DB=tamma_control \
    -e POSTGRES_USER=postgres \
    -e TAMMA_ADMIN_PASSWORD="$ADMIN_CANARY" \
    -e TAMMA_PROVISIONER_PASSWORD="$PROVISIONER_CANARY" \
    -e TAMMA_APP_PASSWORD="$APP_CANARY" \
    "$CONTAINER" \
    bash /tmp/scripts/db/docker-entrypoint-bootstrap.sh \
    >"$LOG_DIR/bootstrap.stdout" 2>"$LOG_DIR/bootstrap.stderr"
BOOTSTRAP_RC=$?
set -e

# Stop the watcher.
kill "$WATCHER_PID" 2>/dev/null || true
wait "$WATCHER_PID" 2>/dev/null || true

if [ $BOOTSTRAP_RC -ne 0 ]; then
    echo "[leaktest] FAIL: bootstrap exited $BOOTSTRAP_RC" >&2
    echo "----- stdout -----" >&2
    cat "$LOG_DIR/bootstrap.stdout" >&2
    echo "----- stderr -----" >&2
    cat "$LOG_DIR/bootstrap.stderr" >&2
    exit 1
fi

# Pull the wrapper-captured argv log out of the container — this is
# the canonical record of what argv the real psql process saw.
docker exec "$CONTAINER" cat /tmp/psql-argv.log >"$LOG_DIR/psql-argv.log" 2>/dev/null || true

# Check both the wrapper log AND the watcher log for any canary hit.
LEAK_HITS=$(grep -E -- "$ADMIN_CANARY|$PROVISIONER_CANARY|$APP_CANARY" \
    "$WATCHER_LOG" "$LOG_DIR/psql-argv.log" 2>/dev/null || true)

# Show the wrapper-captured argv log (this is the canonical proof).
echo "[leaktest] wrapper-captured psql argv (definitive — every psql call):"
if [ -s "$LOG_DIR/psql-argv.log" ]; then
    sed 's/^/  /' "$LOG_DIR/psql-argv.log"
else
    echo "  (wrapper log was empty — psql wrapper not invoked)"
fi
echo

# Also show /proc/<pid>/cmdline samples (best-effort).
echo "[leaktest] /proc/<pid>/cmdline samples captured by background watcher:"
PSQL_LINES=$(grep -c PSQL_ARGV "$WATCHER_LOG" 2>/dev/null || echo 0)
echo "[leaktest] watcher captured $PSQL_LINES psql argv samples"
grep -m 5 PSQL_ARGV "$WATCHER_LOG" 2>/dev/null | sed 's/^/  /' || \
    echo "  (no /proc/<pid>/cmdline snapshots — bootstrap window too short)"
echo

if [ -n "$LEAK_HITS" ]; then
    echo "[leaktest] FAIL: canary plaintext appeared in psql argv:" >&2
    echo "$LEAK_HITS" >&2
    exit 1
fi

# It's possible the watcher missed the brief psql window. Still, the
# absence of canary hits is the primary signal. To make the test
# stronger we ALSO confirm the bootstrap script's own argv handling
# by inspecting its rendered source for the canary leak vector.
if grep -E '\-\-set\s*=?\s*[a-z_]*_password' \
        "$SCRIPT_DIR/docker-entrypoint-bootstrap.sh" >/dev/null; then
    echo "[leaktest] FAIL: bootstrap script still uses '--set=…_password' (PF-S2 regression)" >&2
    grep -nE '\-\-set\s*=?\s*[a-z_]*_password' \
        "$SCRIPT_DIR/docker-entrypoint-bootstrap.sh" >&2
    exit 1
fi

echo "[leaktest] PASS: no canary plaintext leaked into psql argv."
echo "[leaktest] PASS: bootstrap script source contains no '--set=…_password' argv pattern."
echo "[leaktest] verified canaries:"
echo "  TAMMA_ADMIN_PASSWORD=$ADMIN_CANARY"
echo "  TAMMA_PROVISIONER_PASSWORD=$PROVISIONER_CANARY"
echo "  TAMMA_APP_PASSWORD=$APP_CANARY"
exit 0
