#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BATCH_FILE="$SCRIPT_DIR/batch.txt"
BASE_URL="${METRICS_SERVER_URL:-http://localhost:9100}"
BASE_URL="${BASE_URL%/}"

for command in curl python3; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "FAIL: Required command '$command' was not found." >&2
        exit 1
    fi
done

if [[ ! -f "$BATCH_FILE" ]]; then
    echo "FAIL: Batch fixture was not found at $BATCH_FILE." >&2
    exit 1
fi

mapfile -t payload_metadata < <(
    python3 - "$BATCH_FILE" <<'PY'
import pathlib
import sys

lines = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8-sig").splitlines()
replica_ids = [line.split(":", 1)[1].strip() for line in lines if line.lower().startswith("replicaid:")]
batch_ids = [line.split(":", 1)[1].strip() for line in lines if line.lower().startswith("batchid:")]

if len(replica_ids) != 1 or not replica_ids[0]:
    raise SystemExit("batch.txt must contain exactly one non-empty ReplicaId line")
if not batch_ids or any(not batch_id for batch_id in batch_ids):
    raise SystemExit("batch.txt must contain at least one non-empty BatchId line")
if len(batch_ids) != len(set(batch_ids)):
    raise SystemExit("batch.txt contains duplicate BatchId values")

print(replica_ids[0])
print(*batch_ids, sep="\n")
PY
)

replica_id="${payload_metadata[0]}"
expected_batch_ids=("${payload_metadata[@]:1}")

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

health_status="$(
    curl --silent --show-error \
        --output "$temp_dir/health.txt" \
        --write-out "%{http_code}" \
        "$BASE_URL/health"
)"

if [[ "$health_status" != "200" ]]; then
    echo "FAIL: GET $BASE_URL/health returned HTTP $health_status." >&2
    cat "$temp_dir/health.txt" >&2
    exit 1
fi

echo "PASS: MetricsServer health check returned HTTP 200."

lookup_user="alice"
lookup_model="gpt-4o"
other_model="gpt-4.1"

retired_lookup_paths=(
    "/tokenomics/metrics/tokens/daily/users"
    "/tokenomics/metrics/tokens/monthly/users"
    "/tokenomics/metrics/budgets/daily/users"
    "/tokenomics/metrics/budgets/monthly/users"
    "/tokenomics/metrics/abuse-detected/users"
    "/tokenomics/metrics/approved-exception/users"
    "/tokenomics/metrics/administrator-override/users"
)

for retired_lookup_path in "${retired_lookup_paths[@]}"; do
    retired_status="$(
        curl --silent --show-error \
            --get \
            --data-urlencode "u=$lookup_user" \
            --output "$temp_dir/retired-response.txt" \
            --write-out "%{http_code}" \
            "$BASE_URL$retired_lookup_path"
    )"

    if [[ "$retired_status" != "404" ]]; then
        echo "FAIL: Retired route $retired_lookup_path returned HTTP $retired_status instead of HTTP 404." >&2
        cat "$temp_dir/retired-response.txt" >&2
        exit 1
    fi
done

echo "PASS: Legacy tokenomics GET routes return HTTP 404."

lookup_status="$(
    curl --silent --show-error \
        --get \
        --data-urlencode "u=$lookup_user" \
        --data-urlencode "m=$lookup_model" \
        --output "$temp_dir/lookup-before.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/lookup"
)"

if [[ "$lookup_status" != "200" ]]; then
    echo "FAIL: Initial combined metrics lookup returned HTTP $lookup_status instead of HTTP 200." >&2
    cat "$temp_dir/lookup-before.json" >&2
    exit 1
fi

other_lookup_status="$(
    curl --silent --show-error \
        --get \
        --data-urlencode "u=$lookup_user" \
        --data-urlencode "m=$other_model" \
        --output "$temp_dir/other-lookup-before.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/lookup"
)"

if [[ "$other_lookup_status" != "200" ]]; then
    echo "FAIL: Initial model-isolation lookup returned HTTP $other_lookup_status instead of HTTP 200." >&2
    cat "$temp_dir/other-lookup-before.json" >&2
    exit 1
fi

missing_model_status="$(
    curl --silent --show-error \
        --get \
        --data-urlencode "u=$lookup_user" \
        --output "$temp_dir/missing-model-response.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/lookup"
)"

if [[ "$missing_model_status" != "400" ]]; then
    echo "FAIL: Combined metrics lookup without 'm' returned HTTP $missing_model_status instead of HTTP 400." >&2
    cat "$temp_dir/missing-model-response.json" >&2
    exit 1
fi

echo "PASS: Combined metrics lookup validates required query parameters."

upload_status="$(
    curl --silent --show-error \
        --request POST \
        --header "Content-Type: text/csv" \
        --data-binary "@$BATCH_FILE" \
        --output "$temp_dir/upload-response.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/upload"
)"

if [[ "$upload_status" != "202" ]]; then
    echo "FAIL: Batch upload returned HTTP $upload_status instead of HTTP 202." >&2
    cat "$temp_dir/upload-response.json" >&2
    exit 1
fi

python3 - "$temp_dir/upload-response.json" <<'PY'
import json
import pathlib
import sys

response = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
for property_name in ("PendingBatches", "ProcessedBatches"):
    if not isinstance(response.get(property_name), list):
        raise SystemExit(f"upload response property {property_name} must be an array")
PY

echo "PASS: batch.txt upload returned HTTP 202 with a valid response."

printf 'ReplicaId: %s\n' "$replica_id" > "$temp_dir/status-probe.txt"

processed=false
for _ in $(seq 1 40); do
    probe_status="$(
        curl --silent --show-error \
            --request POST \
            --header "Content-Type: text/csv" \
            --data-binary "@$temp_dir/status-probe.txt" \
            --output "$temp_dir/status-response.json" \
            --write-out "%{http_code}" \
            "$BASE_URL/tokenomics/metrics/upload"
    )"

    if [[ "$probe_status" != "202" ]]; then
        echo "FAIL: Batch status probe returned HTTP $probe_status instead of HTTP 202." >&2
        cat "$temp_dir/status-response.json" >&2
        exit 1
    fi

    if python3 - "$temp_dir/status-response.json" "${expected_batch_ids[@]}" >/dev/null 2>&1 <<'PY'
import json
import pathlib
import sys

response = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
expected = set(sys.argv[2:])
pending = set(response.get("PendingBatches") or [])
processed = set(response.get("ProcessedBatches") or [])

if not expected.issubset(processed) or expected.intersection(pending):
    raise SystemExit(1)
PY
    then
        processed=true
        break
    fi

    sleep 0.25
done

if [[ "$processed" != "true" ]]; then
    echo "FAIL: MetricsServer did not process every batch within 10 seconds." >&2
    python3 - "$temp_dir/status-response.json" "${expected_batch_ids[@]}" <<'PY'
import json
import pathlib
import sys

response = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
expected = set(sys.argv[2:])
pending = set(response.get("PendingBatches") or [])
processed = set(response.get("ProcessedBatches") or [])

print(f"Expected:  {sorted(expected)}", file=sys.stderr)
print(f"Pending:   {sorted(pending)}", file=sys.stderr)
print(f"Processed: {sorted(processed)}", file=sys.stderr)
print(f"Missing:   {sorted(expected - processed)}", file=sys.stderr)
PY
    exit 1
fi

echo "PASS: MetricsServer processed ${#expected_batch_ids[@]} batches for replica '$replica_id'."

lookup_status="$(
    curl --silent --show-error \
        --get \
        --data-urlencode "u=$lookup_user" \
        --data-urlencode "m=$lookup_model" \
        --output "$temp_dir/lookup-after.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/lookup"
)"

if [[ "$lookup_status" != "200" ]]; then
    echo "FAIL: Combined metrics lookup returned HTTP $lookup_status instead of HTTP 200." >&2
    cat "$temp_dir/lookup-after.json" >&2
    exit 1
fi

other_lookup_status="$(
    curl --silent --show-error \
        --get \
        --data-urlencode "u=$lookup_user" \
        --data-urlencode "m=$other_model" \
        --output "$temp_dir/other-lookup-after.json" \
        --write-out "%{http_code}" \
        "$BASE_URL/tokenomics/metrics/lookup"
)"

if [[ "$other_lookup_status" != "200" ]]; then
    echo "FAIL: Model-isolation lookup returned HTTP $other_lookup_status instead of HTTP 200." >&2
    cat "$temp_dir/other-lookup-after.json" >&2
    exit 1
fi

python3 - \
    "$BATCH_FILE" \
    "$temp_dir/lookup-before.json" \
    "$temp_dir/lookup-after.json" \
    "$temp_dir/other-lookup-before.json" \
    "$temp_dir/other-lookup-after.json" \
    "$lookup_user" \
    "$lookup_model" \
    "$other_model" <<'PY'
import csv
import datetime
import json
import pathlib
import sys

(
    batch_path,
    lookup_before_path,
    lookup_after_path,
    other_before_path,
    other_after_path,
    user_id,
    model,
    other_model,
) = sys.argv[1:]

today = datetime.datetime.now(datetime.timezone.utc).date()
expected_daily_tokens = 0
expected_monthly_tokens = 0

for line in pathlib.Path(batch_path).read_text(encoding="utf-8-sig").splitlines():
    if (
        not line
        or line.lower().startswith("replicaid:")
        or line.lower().startswith("batchid:")
        or line.lower().startswith("userid,")
    ):
        continue

    row = next(csv.reader([line]))
    if row[0].casefold() != user_id.casefold() or row[1].casefold() != model.casefold():
        continue

    day = datetime.date.fromisoformat(row[2])
    tokens = int(row[3]) + int(row[4])
    if day == today:
        expected_daily_tokens += tokens
    if (day.year, day.month) == (today.year, today.month):
        expected_monthly_tokens += tokens

lookup_before = json.loads(pathlib.Path(lookup_before_path).read_text(encoding="utf-8"))
lookup_after = json.loads(pathlib.Path(lookup_after_path).read_text(encoding="utf-8"))
other_before = json.loads(pathlib.Path(other_before_path).read_text(encoding="utf-8"))
other_after = json.loads(pathlib.Path(other_after_path).read_text(encoding="utf-8"))

if lookup_after.get("UserId") != user_id or lookup_after.get("Model") != model:
    raise SystemExit("combined metrics lookup did not echo the requested user/model")

daily_delta = lookup_after.get("DailyTokenBalance", 0) - lookup_before.get("DailyTokenBalance", 0)
monthly_delta = lookup_after.get("MonthlyTokenBalance", 0) - lookup_before.get("MonthlyTokenBalance", 0)

if daily_delta != expected_daily_tokens:
    raise SystemExit(f"daily token delta was {daily_delta}; expected {expected_daily_tokens}")
if monthly_delta != expected_monthly_tokens:
    raise SystemExit(f"monthly token delta was {monthly_delta}; expected {expected_monthly_tokens}")

for property_name in ("DailyBudgetUsage", "MonthlyBudgetUsage"):
    if lookup_after.get(property_name) != 0:
        raise SystemExit(f"{property_name} must be 0 until budget data is available")

for property_name in ("IsAbuseDetected", "HasApprovedException", "HasAdministratorOverride"):
    if lookup_after.get(property_name) is not False:
        raise SystemExit(f"{property_name} must be false until its data is available")

if other_after != other_before:
    raise SystemExit(f"metrics for {user_id}/{other_model} changed when only {user_id}/{model} was uploaded")
PY

echo "PASS: Combined metrics lookup returned the expected user/model data."
