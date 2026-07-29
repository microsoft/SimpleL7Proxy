#!/usr/bin/env bash

_regression_script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
_regression_project="$_regression_script_dir/SimpleL7Proxy.Test.csproj"
_regression_renderer="$_regression_script_dir/render-regression-results.py"

regression_usage() {
    cat <<'EOF'
Usage: run-regression-master.sh [options] [-- <MSTest options>]

Runs the regression project and appends its TRX results to one master HTML page.

Options:
  --filter <expression>    MSTest filter expression.
  --label <text>           Label displayed for this execution.
  --run-id <id>            Master execution ID. Defaults to a UTC timestamp and PID.
  --results-root <path>    Report root. Defaults to test/RegressionTests/results.
  -h, --help               Show this help.

To append separate scripts to one page:
  export REGRESSION_MASTER_RUN_ID=my-master-run
    bash ./run-policy-scenarios.sh
    bash ./run-priority-load.sh
EOF
}

regression_slug() {
    local value=${1:-execution}
    value=$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+|-+$//g')
    printf '%s' "${value:-execution}"
}

regression_initialize() {
    local default_label=${1:-Regression suite}
    local generated_id
    generated_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"

    REGRESSION_MASTER_RUN_ID=${REGRESSION_MASTER_RUN_ID:-$generated_id}
    REGRESSION_RESULTS_ROOT=${REGRESSION_RESULTS_ROOT:-$_regression_script_dir/results}
    REGRESSION_MASTER_RESULTS_DIR=${REGRESSION_MASTER_RESULTS_DIR:-$REGRESSION_RESULTS_ROOT/$REGRESSION_MASTER_RUN_ID}
    REGRESSION_MASTER_HTML=${REGRESSION_MASTER_HTML:-$REGRESSION_MASTER_RESULTS_DIR/index.html}
    REGRESSION_MASTER_MANIFEST=${REGRESSION_MASTER_MANIFEST:-$REGRESSION_MASTER_RESULTS_DIR/results.json}
    REGRESSION_EXECUTION_LABEL=${REGRESSION_EXECUTION_LABEL:-$default_label}

    mkdir -p -- "$REGRESSION_MASTER_RESULTS_DIR/trx" "$REGRESSION_MASTER_RESULTS_DIR/console"

    export REGRESSION_MASTER_RUN_ID
    export REGRESSION_RESULTS_ROOT
    export REGRESSION_MASTER_RESULTS_DIR
    export REGRESSION_MASTER_HTML
    export REGRESSION_MASTER_MANIFEST
}

regression_prepare_execution() {
    local label=${1:-Regression execution}
    local slug
    slug=$(regression_slug "$label")

    REGRESSION_EXECUTION_LABEL=$label
    REGRESSION_EXECUTION_ID="$(date -u +%Y%m%dT%H%M%S)-${slug}-$$-${RANDOM}"
    REGRESSION_TRX_FILENAME="$REGRESSION_EXECUTION_ID.trx"
    REGRESSION_TRX_DIR="$REGRESSION_MASTER_RESULTS_DIR/trx/$REGRESSION_EXECUTION_ID"
    REGRESSION_TRX_PATH="$REGRESSION_TRX_DIR/$REGRESSION_TRX_FILENAME"
    REGRESSION_CONSOLE_LOG="$REGRESSION_MASTER_RESULTS_DIR/console/$REGRESSION_EXECUTION_ID.log"
    mkdir -p -- "$REGRESSION_TRX_DIR"

    export REGRESSION_EXECUTION_LABEL
    export REGRESSION_EXECUTION_ID
    export REGRESSION_TRX_FILENAME
    export REGRESSION_TRX_DIR
    export REGRESSION_TRX_PATH
    export REGRESSION_CONSOLE_LOG
}

regression_build_command() {
    local target_name=$1
    local filter=${2:-}
    shift 2
    local -n target=$target_name

    target=(
        dotnet test "$_regression_project" --
        --report-trx
        --report-trx-filename "$REGRESSION_TRX_FILENAME"
        --results-directory "$REGRESSION_TRX_DIR"
    )
    if [[ -n "$filter" ]]; then
        target+=(--filter "$filter")
    fi
    target+=("$@")
}

regression_format_command() {
    local formatted
    printf -v formatted '%q ' "$@"
    printf '%s' "${formatted% }"
}

regression_finalize_execution() {
    local exit_code=$1
    local command_text=$2
    local started_utc=$3
    local completed_utc=$4
    local report_python=${REGRESSION_REPORT_PYTHON:-python3}

    "$report_python" "$_regression_renderer" \
        --manifest "$REGRESSION_MASTER_MANIFEST" \
        --html "$REGRESSION_MASTER_HTML" \
        --master-run-id "$REGRESSION_MASTER_RUN_ID" \
        --execution-id "$REGRESSION_EXECUTION_ID" \
        --label "$REGRESSION_EXECUTION_LABEL" \
        --trx "$REGRESSION_TRX_PATH" \
        --console-log "$REGRESSION_CONSOLE_LOG" \
        --exit-code "$exit_code" \
        --command "$command_text" \
        --started-utc "$started_utc" \
        --completed-utc "$completed_utc" >/dev/null

    printf '\nRegression HTML report: %s\n' "$REGRESSION_MASTER_HTML"
}

regression_run() {
    local label=$1
    local filter=${2:-}
    shift 2

    regression_initialize "$label"
    regression_prepare_execution "$label"

    local -a command
    regression_build_command command "$filter" "$@"
    local command_text
    command_text=$(regression_format_command "${command[@]}")
    local started_utc
    started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)

    local restore_errexit=false
    if [[ $- == *e* ]]; then
        restore_errexit=true
    fi
    set +e
    "${command[@]}" 2>&1 | tee "$REGRESSION_CONSOLE_LOG"
    local test_status=${PIPESTATUS[0]}
    if [[ "$restore_errexit" == true ]]; then
        set -e
    fi

    if [[ -n "${REGRESSION_POST_TEST_OUTPUT_FILE:-}" && -s "$REGRESSION_POST_TEST_OUTPUT_FILE" ]]; then
        {
            printf '\n%s\n\n' "${REGRESSION_POST_TEST_OUTPUT_TITLE:-Additional test output}"
            cat -- "$REGRESSION_POST_TEST_OUTPUT_FILE"
        } | tee -a "$REGRESSION_CONSOLE_LOG"
    fi

    local completed_utc
    completed_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    regression_finalize_execution "$test_status" "$command_text" "$started_utc" "$completed_utc"
    return "$test_status"
}

regression_main() {
    local label=${REGRESSION_RUN_LABEL:-Full regression suite}
    local filter=${REGRESSION_TEST_FILTER:-}
    local -a extra_args=()

    while (($#)); do
        case "$1" in
            --filter)
                filter=$2
                shift 2
                ;;
            --label)
                label=$2
                shift 2
                ;;
            --run-id)
                REGRESSION_MASTER_RUN_ID=$2
                shift 2
                ;;
            --results-root)
                REGRESSION_RESULTS_ROOT=$2
                shift 2
                ;;
            -h|--help)
                regression_usage
                return 0
                ;;
            --)
                shift
                extra_args+=("$@")
                break
                ;;
            *)
                printf 'Unknown option: %s\n' "$1" >&2
                regression_usage >&2
                return 2
                ;;
        esac
    done

    regression_run "$label" "$filter" "${extra_args[@]}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    set -euo pipefail
    regression_main "$@"
fi
