#!/usr/bin/env bash
# Story 28-12 — rotate the tamma_app Postgres role password.
#
# The Phase-2 RLS migration (20260419021119_Phase2RlsAndTriggers.cs)
# creates the tamma_app role with the placeholder password 'changeme'.
# Rolling that out to production is unacceptable. This script generates
# a fresh password, applies it via ALTER ROLE, and prints the new
# connection string fragment for the operator to copy into the
# deployment secret store.
#
# Usage:
#   PG_ADMIN_URL=postgres://admin:pass@host:5432/tamma_control \
#     scripts/rotate-tamma-app-password.sh
#
#   PG_ADMIN_URL=postgres://admin:pass@host:5432/tamma_control \
#     scripts/rotate-tamma-app-password.sh --print-connection-string
#
# Run modes:
#   --print-connection-string  — print the new ConnectionStrings:TammaAppDb
#                                value the operator should paste into the
#                                deployment secret store. Default: only
#                                print the password.
#
# Safety:
#   • The new password is 32 bytes of CSPRNG entropy, base64-url-encoded.
#   • The password is generated locally (no API call) so it never crosses
#     the wire except as part of the ALTER ROLE statement.
#   • PG_ADMIN_URL must be a SUPERUSER or role-owner connection — the
#     script does NOT bootstrap that credential. Source it from the
#     sealed-secrets vault for the duration of the rotation only.
#
# Exit codes:
#   0  — rotation succeeded
#   1  — usage error (missing PG_ADMIN_URL)
#   2  — psql exec failed
set -euo pipefail

if [[ -z "${PG_ADMIN_URL:-}" ]]; then
    echo "ERROR: PG_ADMIN_URL must be set to a Postgres superuser or" >&2
    echo "       role-owner connection string." >&2
    echo "Usage: PG_ADMIN_URL=postgres://... $0 [--print-connection-string]" >&2
    exit 1
fi

print_cs=0
case "${1:-}" in
    "")
        ;;
    --print-connection-string)
        print_cs=1
        ;;
    *)
        echo "ERROR: unknown flag '${1}'" >&2
        echo "Usage: $0 [--print-connection-string]" >&2
        exit 1
        ;;
esac

# 32 bytes of CSPRNG → base64-url. Strip = padding for cleaner copy/paste
# into appsettings.json + Hetzner sealed secrets.
new_pw="$(openssl rand -base64 32 | tr '/+' '_-' | tr -d '=')"

# The single-quote escape inside SQL is literal $$ so a stray ' in the
# password (impossible with base64-url, but defence in depth) cannot
# break the statement.
sql="ALTER ROLE tamma_app WITH PASSWORD '${new_pw}';"

if ! psql --set ON_ERROR_STOP=on --command="${sql}" "${PG_ADMIN_URL}" >/dev/null; then
    echo "ERROR: psql ALTER ROLE failed. Verify PG_ADMIN_URL points at a" >&2
    echo "       superuser or the tamma_app role owner." >&2
    exit 2
fi

echo "tamma_app password rotated successfully."

if (( print_cs == 1 )); then
    # Surface a sample connection-string fragment. The operator pastes
    # the result into ConnectionStrings:TammaAppDb (or the analogous
    # tamma_app credential slot in the deployment secret manager).
    cat <<EOF

# Paste into your deployment secret store as ConnectionStrings:TammaAppDb:
Server=<host>;Port=5432;Database=<db>;User Id=tamma_app;Password=${new_pw};Pooling=true;Maximum Pool Size=20;
EOF
else
    echo "Password (copy into the secret store now — it is not stored): ${new_pw}"
fi
