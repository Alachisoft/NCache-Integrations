#!/usr/bin/env bash
#
# Brings up a single-node NCache Open Source server in Docker and starts the cache
# the integration tests connect to. Exports NCACHE_SERVER and NCACHE_CACHE_NAME
# through $GITHUB_ENV so later steps can reach the cache.
#
# Requires NCACHE_REGISTRATION_KEY: NCache Open Source refuses to start a cache until
# it has been registered. NCACHE_REGISTRATION_EMAIL is only passed along when set.

set -euo pipefail

IMAGE="${NCACHE_IMAGE:-alachisoft/ncache:latest-oss}"
CONTAINER="${NCACHE_CONTAINER:-ncache}"
CACHE_NAME="${NCACHE_CACHE_NAME:-defaultCache}"
TOOLS="/opt/ncache/bin/tools"

: "${NCACHE_REGISTRATION_KEY:?NCACHE_REGISTRATION_KEY is not set}"

docker pull "$IMAGE"

# Host networking, not a published port. NCache binds to one address and hands that
# same address back to connecting clients, so a client that reached the server on a
# forwarded port is still redirected to the container's own address and fails.
docker run -d --name "$CONTAINER" --network host "$IMAGE"

echo "Waiting for the NCache management service..."
for attempt in $(seq 1 30); do
  if docker exec "$CONTAINER" "$TOOLS/get-caches" >/dev/null 2>&1; then
    break
  fi

  if [ "$attempt" -eq 30 ]; then
    echo "::error::NCache management service did not come up in time."
    docker logs "$CONTAINER"
    exit 1
  fi

  sleep 2
done

register_args=(-Key "$NCACHE_REGISTRATION_KEY")
if [ -n "${NCACHE_REGISTRATION_EMAIL:-}" ]; then
  register_args+=(-Email "$NCACHE_REGISTRATION_EMAIL")
fi

# The tools report failures on stdout and do not always exit non-zero, so match on
# the success message rather than trusting the exit code.
echo "Registering NCache Open Source..."
registration=$(docker exec "$CONTAINER" "$TOOLS/register-ncacheopensource" "${register_args[@]}" 2>&1 || true)
echo "$registration"

if ! printf '%s' "$registration" | grep -qi "has been registered"; then
  echo "::error::NCache registration failed. Check the NCACHE_REGISTRATION_KEY secret."
  exit 1
fi

# The installation creates '$CACHE_NAME' but does not start it.
echo "Starting cache '$CACHE_NAME'..."
start=$(docker exec "$CONTAINER" "$TOOLS/start-cache" -Name "$CACHE_NAME" 2>&1 || true)
echo "$start"

if ! printf '%s' "$start" | grep -qi "successfully started"; then
  echo "::error::Failed to start cache '$CACHE_NAME'."
  docker logs "$CONTAINER"
  exit 1
fi

docker exec "$CONTAINER" "$TOOLS/get-caches"

# The container's startup script rewrites the .ncconf files with whichever address
# NCache bound to, and that is the address a client has to use. Read it back instead
# of assuming localhost.
server=$(docker exec "$CONTAINER" \
  sed -n 's/.*local-server-ip="\([^"]*\)".*/\1/p' /opt/ncache/config/client.ncconf | head -n 1 | tr -d '\r')

if [ -z "$server" ]; then
  echo "::error::Could not read local-server-ip from client.ncconf."
  exit 1
fi

echo "NCache is listening on ${server}:9800"

{
  echo "NCACHE_SERVER=${server}"
  echo "NCACHE_CACHE_NAME=${CACHE_NAME}"
} >>"$GITHUB_ENV"
