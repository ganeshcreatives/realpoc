#!/bin/bash
set -euo pipefail
: "${PublicOrigin:?Set the public HTTPS origin}"
: "${Database__Connection:?Set the PostgreSQL connection string}"
export Database__Provider=Postgres
export ApiOrigin=http://127.0.0.1:5081
api_pid=''; bff_pid=''
cleanup() {
  [ -z "$api_pid" ] || kill "$api_pid" 2>/dev/null || true
  [ -z "$bff_pid" ] || kill "$bff_pid" 2>/dev/null || true
  wait || true
}
trap cleanup EXIT
trap 'exit 143' TERM INT
# Initial schema creation is deliberately an explicit one-time operator action.
if [ "${INITIALIZE_DATABASE:-false}" = true ]; then
  (cd /app/api && dotnet School.Api.dll --migrate-db)
fi
(cd /app/api && ASPNETCORE_URLS=http://127.0.0.1:5081 exec dotnet School.Api.dll) & api_pid=$!
(cd /app/bff && ASPNETCORE_URLS="http://0.0.0.0:${PORT:-8080}" exec dotnet School.Bff.dll) & bff_pid=$!
# End the container if either service fails, allowing the host to restart both.
set +e
wait -n "$api_pid" "$bff_pid"
exit_code=$?
set -e
if [ "$exit_code" -eq 0 ]; then exit_code=1; fi
exit "$exit_code"
