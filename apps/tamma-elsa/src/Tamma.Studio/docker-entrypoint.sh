#!/bin/sh
set -e

SETTINGS_FILE="/usr/share/nginx/html/appsettings.json"

# ---------------------------------------------------------------------------
# Inject ELSA Server URL into appsettings.json
#
# The placeholder "http://localhost:13000/elsa/api" is baked into the
# published WASM app. Replace it with the runtime ELSASERVER__URL env var.
# ---------------------------------------------------------------------------
if [ -n "$ELSASERVER__URL" ]; then
    echo "Injecting ElsaServer URL: $ELSASERVER__URL"
    # Use a temp file to avoid sed -i portability issues on alpine
    sed "s|http://localhost:13000/elsa/api|${ELSASERVER__URL}|g" "$SETTINGS_FILE" > "${SETTINGS_FILE}.tmp"
    mv "${SETTINGS_FILE}.tmp" "$SETTINGS_FILE"
else
    echo "WARNING: ELSASERVER__URL not set. Studio will try to connect to http://localhost:13000/elsa/api"
fi

echo "Starting nginx..."
exec "$@"
