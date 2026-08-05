#!/usr/bin/env bash

_regression_script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
_regression_project="$_regression_script_dir/SimpleL7Proxy.Test.csproj"
_regression_renderer_project="$_regression_script_dir/Framework/ReportRenderer/RegressionReportRenderer.csproj"
_regression_renderer_dll="$_regression_script_dir/Framework/ReportRenderer/bin/Debug/net10.0/RegressionReportRenderer.dll"
_regression_test_assembly="$_regression_script_dir/bin/Debug/net10.0/SimpleL7Proxy.Test.dll"

regression_usage() {
    cat <<'EOF'
SimpleL7Proxy test runner

Usage:
  ./run-regression-master.sh [SUITE] [OPTIONS]
  ./run-regression-master.sh test TEST_NAME [OPTIONS]
  ./run-regression-master.sh category CATEGORY [OPTIONS]
  ./run-regression-master.sh list [SUITE] [OPTIONS]

Suites:
  fast          Quick local checks; skips integration, load, and performance tests.
  regression    Standard regression run without load tests. This is the default.
  integration   Integration tests without load tests.
    load          Load and stress tests, excluding opt-in long-running tests.
    longrun       Opt-in long-running tests, including the 24-hour token test.
    all           Every test except opt-in long-running tests.

Commands:
  test NAME          Run one test method by name.
  category NAME      Run one MSTest category, such as CircuitBreaker or Iterator.
  list [SUITE]       Build and list matching tests without running them. Defaults to all.

Options:
  -l, --list             List matching tests instead of running them.
  --filter EXPRESSION    Override the selection with an MSTest filter.
  --label TEXT           Set the name shown in the HTML report.
  --run-id ID            Append to a master run using yyyyMMdd-HH:mm:ss.
  --results-root PATH    Store reports under PATH.
  -h, --help             Show this help.
  --                     Pass remaining options to MSTest.

Examples:
  ./run-regression-master.sh
  ./run-regression-master.sh fast
  ./run-regression-master.sh integration
  ./run-regression-master.sh load
    ./run-regression-master.sh longrun
  ./run-regression-master.sh test Reset_AllowsReIteration
  ./run-regression-master.sh category CircuitBreaker
  ./run-regression-master.sh list load
EOF
}

regression_usage_error() {
    printf 'Error: %s\n' "$1" >&2
    printf 'Run ./run-regression-master.sh --help for usage.\n' >&2
}

regression_resolve_suite() {
    local suite=$1
    local -n target_label=$2
    local -n target_filter=$3

    case "$suite" in
        fast)
            target_label="Fast checks"
            target_filter="TestCategory!=Integration&TestCategory!=Load&TestCategory!=Performance"
            ;;
        regression)
            target_label="Regression"
            target_filter="TestCategory!=Load"
            ;;
        integration)
            target_label="Integration"
            target_filter="TestCategory=Integration&TestCategory!=Load"
            ;;
        load)
            target_label="Load and stress"
            target_filter="TestCategory=Load&TestCategory!=LongRunning"
            ;;
        longrun)
            target_label="Long-running"
            target_filter="TestCategory=LongRunning"
            ;;
        all)
            target_label="All non-long-running tests"
            target_filter="TestCategory!=LongRunning"
            ;;
        *)
            return 2
            ;;
    esac
}

regression_list_tests() {
    local label=$1
    local filter=${2:-}
    shift 2

    printf '\nSimpleL7Proxy test catalog\n'
    printf '  Selection: %s\n' "$label"
    if [[ -n "$filter" ]]; then
        printf '  Filter: %s\n' "$filter"
    fi
    printf '\n'

    dotnet build "$_regression_project" --nologo --verbosity quiet

    local -a command=(dotnet "$_regression_test_assembly" --list-tests)
    if [[ -n "$filter" ]]; then
        command+=(--filter "$filter")
    fi
    command+=("$@")
    "${command[@]}"
}

regression_slug() {
    local value=${1:-execution}
    value=$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+|-+$//g')
    printf '%s' "${value:-execution}"
}

regression_initialize() {
    local default_label=${1:-Regression suite}
    local generated_id
    generated_id="$(date -u +%Y%m%d-%H:%M:%S)"

    REGRESSION_MASTER_RUN_ID=${REGRESSION_MASTER_RUN_ID:-$generated_id}
    if [[ ! "$REGRESSION_MASTER_RUN_ID" =~ ^[0-9]{8}-[0-9]{2}:[0-9]{2}:[0-9]{2}$ ]]; then
        printf 'REGRESSION_MASTER_RUN_ID must use yyyyMMdd-HH:mm:ss format: %s\n' "$REGRESSION_MASTER_RUN_ID" >&2
        return 2
    fi
    REGRESSION_RESULTS_ROOT=${REGRESSION_RESULTS_ROOT:-$_regression_script_dir/results}
    REGRESSION_HISTORY_ROOT=${REGRESSION_HISTORY_ROOT:-$REGRESSION_RESULTS_ROOT/history}
    REGRESSION_MASTER_RESULTS_DIR=${REGRESSION_MASTER_RESULTS_DIR:-$REGRESSION_HISTORY_ROOT/$REGRESSION_MASTER_RUN_ID}
    REGRESSION_LANDING_HTML=${REGRESSION_LANDING_HTML:-$REGRESSION_RESULTS_ROOT/index.html}
    REGRESSION_MASTER_HTML=${REGRESSION_MASTER_HTML:-$REGRESSION_MASTER_RESULTS_DIR/index.html}
    REGRESSION_MASTER_MANIFEST=${REGRESSION_MASTER_MANIFEST:-$REGRESSION_MASTER_RESULTS_DIR/results.json}
    REGRESSION_EXECUTION_LABEL=${REGRESSION_EXECUTION_LABEL:-$default_label}

    mkdir -p -- "$REGRESSION_MASTER_RESULTS_DIR/runs"

    export REGRESSION_MASTER_RUN_ID
    export REGRESSION_RESULTS_ROOT
    export REGRESSION_HISTORY_ROOT
    export REGRESSION_MASTER_RESULTS_DIR
    export REGRESSION_LANDING_HTML
    export REGRESSION_MASTER_HTML
    export REGRESSION_MASTER_MANIFEST
}

regression_prepare_execution() {
    local label=${1:-Regression execution}
    local slug
    slug=$(regression_slug "$label")

    REGRESSION_EXECUTION_LABEL=$label
    REGRESSION_EXECUTION_ID="$(date -u +%Y%m%d-%H:%M:%S)-${slug}-${RANDOM}"
    REGRESSION_EXECUTION_DIR="$REGRESSION_MASTER_RESULTS_DIR/runs/$REGRESSION_EXECUTION_ID"
    REGRESSION_TRX_FILENAME="result.trx"
    REGRESSION_TRX_DIR="$REGRESSION_EXECUTION_DIR"
    REGRESSION_TRX_PATH="$REGRESSION_EXECUTION_DIR/$REGRESSION_TRX_FILENAME"
    REGRESSION_CONSOLE_LOG="$REGRESSION_EXECUTION_DIR/console.log"
    mkdir -p -- "$REGRESSION_TRX_DIR"

    export REGRESSION_EXECUTION_LABEL
    export REGRESSION_EXECUTION_ID
    export REGRESSION_EXECUTION_DIR
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
    if [[ ! -f "$_regression_renderer_dll" ]]; then
        dotnet build "$_regression_renderer_project" >/dev/null
    fi

    dotnet "$_regression_renderer_dll" \
        --manifest "$REGRESSION_MASTER_MANIFEST" \
        --html "$REGRESSION_MASTER_HTML" \
        --landing "$REGRESSION_LANDING_HTML" \
        --history-root "$REGRESSION_HISTORY_ROOT" \
        --master-run-id "$REGRESSION_MASTER_RUN_ID" \
        --execution-id "$REGRESSION_EXECUTION_ID" \
        --label "$REGRESSION_EXECUTION_LABEL" \
        --trx "$REGRESSION_TRX_PATH" \
        --console-log "$REGRESSION_CONSOLE_LOG" \
        --test-assembly "$_regression_test_assembly" \
        --exit-code "$exit_code" \
        --command "$command_text" \
        --started-utc "$started_utc" \
        --completed-utc "$completed_utc" >/dev/null

    if ((exit_code == 0)); then
        printf '\nResult: PASS\n'
    else
        printf '\nResult: FAIL (exit code %s)\n' "$exit_code"
    fi
    printf 'Regression HTML report: %s\n' "$REGRESSION_MASTER_HTML"
}

regression_run() {
    local label=$1
    local filter=${2:-}
    shift 2

    regression_initialize "$label"
    regression_prepare_execution "$label"

    printf '\nSimpleL7Proxy tests\n'
    printf '  Run: %s\n' "$label"
    if [[ -n "$filter" ]]; then
        printf '  Filter: %s\n' "$filter"
    else
        printf '  Filter: all tests\n'
    fi
    printf '  Report: %s\n\n' "$REGRESSION_MASTER_HTML"

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
    local command=regression
    local command_explicit=false
    if (($#)) && [[ "$1" != -* ]]; then
        command=$1
        command_explicit=true
        shift
    fi

    local default_label
    local filter
    local list_only=false

    case "$command" in
        fast|regression|integration|load|longrun|all)
            if ! regression_resolve_suite "$command" default_label filter; then
                regression_usage_error "Unknown suite '$command'."
                return 2
            fi
            ;;
        test)
            if (($# == 0)) || [[ "$1" == -* ]]; then
                regression_usage_error "The test command requires a test method name."
                return 2
            fi
            default_label="Test: $1"
            filter="Name=$1"
            shift
            ;;
        category)
            if (($# == 0)) || [[ "$1" == -* ]]; then
                regression_usage_error "The category command requires a category name."
                return 2
            fi
            default_label="Category: $1"
            filter="TestCategory=$1"
            shift
            ;;
        list)
            list_only=true
            local suite=all
            if (($#)) && [[ "$1" != -* ]]; then
                suite=$1
                shift
            fi
            if ! regression_resolve_suite "$suite" default_label filter; then
                regression_usage_error "Unknown suite '$suite'."
                return 2
            fi
            ;;
        help)
            regression_usage
            return 0
            ;;
        *)
            regression_usage_error "Unknown command or suite '$command'."
            return 2
            ;;
    esac

    local label=${REGRESSION_RUN_LABEL:-$default_label}
    if [[ "$command_explicit" == false ]]; then
        filter=${REGRESSION_TEST_FILTER:-$filter}
    fi
    local -a extra_args=()

    while (($#)); do
        case "$1" in
            --filter)
                if (($# < 2)); then
                    regression_usage_error "--filter requires an expression."
                    return 2
                fi
                filter=$2
                shift 2
                ;;
            --label)
                if (($# < 2)); then
                    regression_usage_error "--label requires text."
                    return 2
                fi
                label=$2
                shift 2
                ;;
            --run-id)
                if (($# < 2)); then
                    regression_usage_error "--run-id requires an ID."
                    return 2
                fi
                REGRESSION_MASTER_RUN_ID=$2
                shift 2
                ;;
            --results-root)
                if (($# < 2)); then
                    regression_usage_error "--results-root requires a path."
                    return 2
                fi
                REGRESSION_RESULTS_ROOT=$2
                shift 2
                ;;
            -l|--list)
                list_only=true
                shift
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
            -*)
                regression_usage_error "Unknown option '$1'."
                return 2
                ;;
            *)
                regression_usage_error "Unexpected argument '$1'."
                return 2
                ;;
        esac
    done

    if [[ "$list_only" == true ]]; then
        regression_list_tests "$label" "$filter" "${extra_args[@]}"
        return
    fi

    regression_run "$label" "$filter" "${extra_args[@]}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    set -euo pipefail
    regression_main "$@"
fi
