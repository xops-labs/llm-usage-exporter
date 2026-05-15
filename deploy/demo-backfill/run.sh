#!/bin/sh
# Idempotent init: seed Prometheus with 7 days of synthetic demo data the first
# time the stack starts. Re-running is a no-op because the existing blocks are
# detected. Skipped entirely when DEMO_MODE_ENABLED is not "true" so production
# stacks aren't polluted with fake telemetry.
set -eu

PROM_DIR="${PROM_DIR:-/prometheus}"
BACKFILL_FILE="${BACKFILL_FILE:-/tmp/backfill.openmetrics}"

if [ "${DEMO_MODE_ENABLED:-false}" != "true" ]; then
    echo "[demo-backfill] DEMO_MODE_ENABLED is not 'true' — skipping backfill."
    exit 0
fi

# Block directories are ULID-named, so any directory starting with '01' is an
# existing block. If we already have one, we've run before — don't double-write.
if ls -d "${PROM_DIR}"/01* >/dev/null 2>&1; then
    echo "[demo-backfill] Prometheus already has blocks under ${PROM_DIR} — skipping."
    exit 0
fi

echo "[demo-backfill] DEMO_MODE_ENABLED=true and ${PROM_DIR} is empty — generating 7 days of synthetic data..."
python3 /usr/local/bin/generate.py "${BACKFILL_FILE}"

echo "[demo-backfill] ingesting via promtool into ${PROM_DIR}..."
promtool tsdb create-blocks-from openmetrics "${BACKFILL_FILE}" "${PROM_DIR}" >/tmp/promtool.log 2>&1 \
    && echo "[demo-backfill] ingested $(grep -c '^01' /tmp/promtool.log || echo 0) blocks." \
    || { echo "[demo-backfill] promtool failed:"; cat /tmp/promtool.log; exit 1; }

rm -f "${BACKFILL_FILE}"
echo "[demo-backfill] done."
