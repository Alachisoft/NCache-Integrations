#!/usr/bin/env bash
#
# Builds and runs every xunit project in the repo that targets a modern .NET
# framework against the NCache server started by start-ncache.sh. Keeps going when a
# project fails so one run reports every broken integration, then exits non-zero if
# any of them failed.
#
# The .NET Framework test projects (net48) need Windows and cannot reach the Linux
# container, so only one modern .NET target of each project is built and run here.

set -uo pipefail

: "${NCACHE_SERVER:?NCACHE_SERVER is not set}"
: "${NCACHE_CACHE_NAME:?NCACHE_CACHE_NAME is not set}"

CONFIGURATION="${CONFIGURATION:-Release}"

# Frameworks the workflow installs an SDK for, in order of preference. A project that
# targets several of them is run once, on the first one listed here.
FRAMEWORKS=(net8.0 net10.0)

# Prints the first framework from FRAMEWORKS that the project targets, or nothing.
pick_framework() {
  local targets
  targets=$(sed -n 's/.*<TargetFrameworks\{0,1\}>\([^<]*\)<.*/\1/p' "$1" | head -n 1 | tr -d '[:space:]')
  for framework in "${FRAMEWORKS[@]}"; do
    if [[ ";$targets;" == *";$framework;"* ]]; then
      echo "$framework"
      return
    fi
  done
}

# Tests connect by cache name only, so each output folder gets a client.ncconf that
# points at the container.
write_client_config() {
  cat >"$1/client.ncconf" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
  <ncache-server connection-retries="5" retry-connection-delay="0" retry-interval="1" client-request-timeout="90" connection-timeout="5" port="9800" />
  <cache id="${NCACHE_CACHE_NAME}" enable-client-logs="False" log-level="error">
    <server name="${NCACHE_SERVER}" />
  </cache>
</configuration>
EOF
}

mapfile -t projects < <(grep -rl --include=*.csproj "Microsoft.NET.Test.Sdk" . | sort)

passed=()
failed=()

for project in "${projects[@]}"; do
  FRAMEWORK=$(pick_framework "$project")
  if [ -z "$FRAMEWORK" ]; then
    echo "::notice::Skipping $project (no ${FRAMEWORKS[*]} target)"
    continue
  fi

  dir=$(dirname "$project")
  name=$(basename "$dir")
  echo "::group::$name ($FRAMEWORK)"

  if dotnet build "$project" --configuration "$CONFIGURATION" --framework "$FRAMEWORK"; then
    output="$dir/bin/$CONFIGURATION/$FRAMEWORK"
    write_client_config "$output"

    if dotnet test "$project" --configuration "$CONFIGURATION" --framework "$FRAMEWORK" --no-build \
        --logger "trx;LogFileName=$name.trx" --results-directory "$PWD/TestResults"; then
      passed+=("$name")
    else
      failed+=("$name")
    fi
  else
    failed+=("$name (build)")
  fi

  echo "::endgroup::"
done

{
  echo "### Integration test results"
  echo
  for name in "${passed[@]}"; do echo "- :white_check_mark: $name"; done
  for name in "${failed[@]}"; do echo "- :x: $name"; done
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"

if [ "${#failed[@]}" -gt 0 ]; then
  echo "::error::Failed: ${failed[*]}"
  exit 1
fi

echo "All ${#passed[@]} test projects passed."
