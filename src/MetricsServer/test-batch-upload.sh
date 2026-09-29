#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BATCH_TEMPLATE="$SCRIPT_DIR/batch.txt"
BASE_URL="${METRICS_SERVER_URL:-http://localhost:9100}"
BASE_URL="${BASE_URL%/}"

for command in curl python3; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "FAIL: Required command '$command' was not found." >&2
        exit 1
    fi
done

if [[ ! -f "$BATCH_TEMPLATE" ]]; then
    echo "FAIL: Batch fixture was not found at $BATCH_TEMPLATE." >&2
    exit 1
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

run_id="$(
    python3 - <<'PY'
import uuid

print(uuid.uuid4().hex[:12])
PY
)"
upload_file="$temp_dir/batch-current-day.txt"
lookup_user="alice-$run_id"
lookup_model="gpt-4o-$run_id"
other_model="o3-$run_id"

python3 - "$BATCH_TEMPLATE" "$upload_file" "$run_id" <<'PY'
import csv
import datetime
import io
import pathlib
import sys

template_path, output_path, run_id = sys.argv[1:]
now = datetime.datetime.now(datetime.timezone.utc)
today = now.date()
timestamp = now.isoformat(timespec="milliseconds").replace("+00:00", "Z")
stale_timestamp = (now - datetime.timedelta(days=1)).isoformat(timespec="milliseconds").replace("+00:00", "Z")
output_lines = []


def format_row(row):
    output = io.StringIO()
    csv.writer(output, lineterminator="").writerow(row)
    return output.getvalue()


for raw_line in pathlib.Path(template_path).read_text(encoding="utf-8-sig").splitlines():
    line = raw_line.lstrip("\ufeff")

    if line.lower().startswith("replicaid:"):
        replica_id = line.split(":", 1)[1].strip()
        output_lines.append(f"ReplicaId: {replica_id}-{run_id}")
        continue

    if line.lower().startswith("batchid:"):
        batch_id = line.split(":", 1)[1].strip()
        output_lines.append(f"BatchId: {batch_id}-{run_id}")
        continue

    if not line or line.lower().startswith("userid,"):
        output_lines.append(line)
        continue

    row = next(csv.reader([line]))
    if len(row) != 11:
        raise SystemExit(f"Expected 11 CSV columns, found {len(row)}: {line}")

    original_user = row[0]
    original_model = row[1]
    row[0] = f"{original_user}-{run_id}"
    row[1] = f"{original_model}-{run_id}"
    row[2] = today.isoformat()
    row[10] = timestamp
    output_lines.append(format_row(row))

    if original_user.casefold() == "alice" and original_model.casefold() == "gpt-4o":
        stale_row = row.copy()
        stale_row[2] = (today - datetime.timedelta(days=1)).isoformat()
        stale_row[3:10] = ["999999", "999999", "999999", "true", "true", "429", "999999"]
        stale_row[10] = stale_timestamp
        output_lines.append(format_row(stale_row))

pathlib.Path(output_path).write_text("\n".join(output_lines) + "\n", encoding="utf-8")
PY

mapfile -t payload_metadata < <(
    python3 - "$upload_file" <<'PY'
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
        --data-binary "@$upload_file" \
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

echo "PASS: Current-day batch upload returned HTTP 202 with a valid response."

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
    "$upload_file" \
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
import math
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
rows = []

for line in pathlib.Path(batch_path).read_text(encoding="utf-8-sig").splitlines():
    if (
        not line
        or line.lower().startswith("replicaid:")
        or line.lower().startswith("batchid:")
        or line.lower().startswith("userid,")
    ):
        continue

    row = next(csv.reader([line]))
    if datetime.date.fromisoformat(row[2]) != today:
        continue

    rows.append(
        {
            "user": row[0],
            "model": row[1],
            "input": int(row[3]),
            "output": int(row[4]),
            "cached": int(row[5]),
            "jailbreak": row[6].casefold() == "true",
            "filtered": row[7].casefold() == "true",
            "status": int(row[8]) if row[8] else None,
            "latency": float(row[9]) if row[9] else None,
        }
    )

lookup_before = json.loads(pathlib.Path(lookup_before_path).read_text(encoding="utf-8"))
lookup_after = json.loads(pathlib.Path(lookup_after_path).read_text(encoding="utf-8"))
other_before = json.loads(pathlib.Path(other_before_path).read_text(encoding="utf-8"))
other_after = json.loads(pathlib.Path(other_after_path).read_text(encoding="utf-8"))

def expected_response(expected_user, expected_model, all_rows):
    pair_rows = [
        row
        for row in all_rows
        if row["user"].casefold() == expected_user.casefold()
        and row["model"].casefold() == expected_model.casefold()
    ]
    model_rows = [
        row
        for row in all_rows
        if row["model"].casefold() == expected_model.casefold()
    ]
    pair_statuses = [row["status"] for row in pair_rows if row["status"] is not None]
    model_statuses = [row["status"] for row in model_rows if row["status"] is not None]
    latencies = [row["latency"] for row in pair_rows if row["latency"] is not None]
    pair_429 = sum(status == 429 for status in pair_statuses) if pair_statuses else None
    model_429 = sum(status == 429 for status in model_statuses) if model_statuses else None
    average_latency = sum(latencies) / len(latencies) if latencies else None

    expected = {
        "UserId": expected_user,
        "Model": expected_model,
        "DailyInputTokens": sum(row["input"] for row in pair_rows),
        "DailyOutputTokens": sum(row["output"] for row in pair_rows),
        "DailyCachedTokens": sum(row["cached"] for row in pair_rows),
        "IsDailyJailbreakDetected": any(row["jailbreak"] for row in pair_rows),
        "IsDailyContentFiltered": any(row["filtered"] for row in pair_rows),
        "DailyModel429": model_429,
        "DailyUser429": pair_429,
        "MonthlyInputTokens": sum(row["input"] for row in pair_rows),
        "MonthlyOutputTokens": sum(row["output"] for row in pair_rows),
        "MonthlyCachedTokens": sum(row["cached"] for row in pair_rows),
        "IsMonthlyJailbreakDetected": any(row["jailbreak"] for row in pair_rows),
        "IsMonthlyContentFiltered": any(row["filtered"] for row in pair_rows),
        "MonthlyModel429": model_429,
        "MonthlyUser429": pair_429,
        "DailyAvgLatencyMs": average_latency,
        "MonthlyAvgLatencyMs": average_latency,
    }
    return expected


def assert_response(actual, expected, label):
    for property_name, expected_value in expected.items():
        actual_value = actual.get(property_name)
        if isinstance(expected_value, float):
            if actual_value is None or not math.isclose(actual_value, expected_value, rel_tol=1e-9):
                raise SystemExit(
                    f"{label} {property_name} was {actual_value!r}; expected {expected_value!r}"
                )
        elif actual_value != expected_value:
            raise SystemExit(
                f"{label} {property_name} was {actual_value!r}; expected {expected_value!r}"
            )

    response_time = actual.get("ResponseTimeUtc")
    if not isinstance(response_time, str) or not response_time.endswith("Z"):
        raise SystemExit(f"{label} ResponseTimeUtc must be a timestamp string")

    timestamp_body = response_time[:-1]
    if "." in timestamp_body:
        timestamp_prefix, fractional_seconds = timestamp_body.split(".", 1)
        if not fractional_seconds.isdigit() or not 1 <= len(fractional_seconds) <= 7:
            raise SystemExit(f"{label} ResponseTimeUtc has invalid fractional seconds")
        timestamp_body = f"{timestamp_prefix}.{fractional_seconds[:6]}"

    datetime.datetime.fromisoformat(f"{timestamp_body}+00:00")


assert_response(lookup_before, expected_response(user_id, model, []), "initial user/model")
assert_response(other_before, expected_response(user_id, other_model, []), "initial model-only")
assert_response(lookup_after, expected_response(user_id, model, rows), "updated user/model")
assert_response(other_after, expected_response(user_id, other_model, rows), "updated model-only")
PY

echo "PASS: Daily and monthly user/model and model aggregates match the uploaded current-day rows."
echo "PASS: The stale row was excluded from daily and monthly aggregates."
