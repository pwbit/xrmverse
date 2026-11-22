#!/usr/bin/env bash
# Mints a development JWT (HS256) for Auth:Mode=Development. Needs only bash + openssl.
# Usage: docker/dev-token.sh [env-file]   (default: docker/.env next to this script)
set -euo pipefail
envfile="${1:-$(dirname "$0")/.env}"
get() {
  local v
  v=$(grep -E "^$1=" "$envfile" | head -1 | cut -d= -f2- | tr -d '\r' || true)
  v="${v%\"}"; v="${v#\"}"; v="${v%\'}"; v="${v#\'}"
  printf '%s' "$v"
}
key=$(get AUTH_DEV_SIGNING_KEY); aud=$(get AUTH_AUDIENCE); oid=$(get SEED_ADMIN_OBJECT_ID); name=$(get SEED_ADMIN_NAME)
[ -n "$key" ] || { echo "AUTH_DEV_SIGNING_KEY is empty in $envfile" >&2; exit 1; }
[ -n "$oid" ] || { echo "SEED_ADMIN_OBJECT_ID is empty in $envfile" >&2; exit 1; }
aud=${aud:-http://localhost:5000/}
b64() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
now=$(date +%s); exp=$((now + 3600))
header=$(printf '{"alg":"HS256","typ":"JWT"}' | b64)
payload=$(printf '{"iss":"xrmverse-dev","aud":"%s","oid":"%s","name":"%s","nbf":%d,"exp":%d}' "$aud" "$oid" "${name:-Administrator}" "$now" "$exp" | b64)
sig=$(printf '%s.%s' "$header" "$payload" | openssl dgst -sha256 -hmac "$key" -binary | b64)
printf '%s.%s.%s\n' "$header" "$payload" "$sig"
