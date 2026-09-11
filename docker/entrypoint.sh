#!/bin/sh
# Entrypoint for the ZeroMcp.Relay container.
#
# MCPRELAY_MODE selects the runtime profile:
#   dev  | development -> config UI enabled at /ui (mcprelay run --enable-ui)
#   prod | production  -> config UI disabled (default)
#
# Any arguments passed to `docker run <image> ...` are appended to the
# `mcprelay run` invocation (e.g. --lazy).
set -eu

MODE=$(printf '%s' "${MCPRELAY_MODE:-prod}" | tr '[:upper:]' '[:lower:]')

set -- run \
    --host "${MCPRELAY_HOST:-0.0.0.0}" \
    --port "${MCPRELAY_PORT:-8080}" \
    --config "${MCPRELAY_CONFIG:-/config/relay.config.json}" \
    "$@"

case "$MODE" in
    dev|development)
        echo "[mcprelay] MCPRELAY_MODE=${MODE}: config UI ENABLED at /ui" >&2
        export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
        set -- "$@" --enable-ui
        ;;
    prod|production)
        echo "[mcprelay] MCPRELAY_MODE=${MODE}: config UI disabled" >&2
        export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
        ;;
    *)
        echo "[mcprelay] Unknown MCPRELAY_MODE '${MODE}' (expected 'dev' or 'prod'); falling back to prod, config UI disabled" >&2
        export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
        ;;
esac

exec dotnet /app/ZeroMcp.Relay.dll "$@"
