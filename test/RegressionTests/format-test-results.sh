#!/usr/bin/env bash
# Format test results from TRX file into human-readable output
# Usage: format-test-results.sh <trx-file> <max-name-width>

if [[ $# -lt 1 ]]; then
    echo "Usage: format-test-results.sh <trx-file> [max-name-width]" >&2
    exit 1
fi

TRX_FILE=$1
MAX_NAME_WIDTH=${2:-70}

if [[ ! -f "$TRX_FILE" ]]; then
    echo "Error: TRX file not found: $TRX_FILE" >&2
    exit 1
fi

# Parse TRX XML and extract test results
# Format: test name | outcome

declare -A results
declare -a test_names
passed=0
failed=0
error=0
skipped=0

while IFS= read -r line; do
    if [[ $line =~ \<UnitTestResult.*testName=\"([^\"]+)\".*outcome=\"([^\"]+)\" ]]; then
        name="${BASH_REMATCH[1]}"
        outcome="${BASH_REMATCH[2]}"
        
        # Extract just the method name (last part after .)
        method_name="${name##*.}"
        
        results["$method_name"]="$outcome"
        test_names+=("$method_name")
        
        case "$outcome" in
            Passed) ((passed++)) ;;
            Failed) ((failed++)) ;;
            Error) ((error++)) ;;
            NotExecuted) ((skipped++)) ;;
        esac
    fi
done < "$TRX_FILE"

# Print header
printf '\n'
printf '%-'"$MAX_NAME_WIDTH"'s  %-10s\n' "Test Name" "Outcome"
printf '%*s  %s\n' "$MAX_NAME_WIDTH" | sed 's/ /_/g' | sed 's/^/_/'
printf '%s\n' "$(printf '%0.s_' {1..85})"

# Print results (preserving order)
declare -A printed
for method_name in "${test_names[@]}"; do
    if [[ -z "${printed[$method_name]}" ]]; then
        outcome="${results[$method_name]}"
        
        # Color-code output if terminal supports it
        if [[ -t 1 ]]; then
            case "$outcome" in
                Passed)
                    outcome_colored="\033[32m$outcome\033[0m"  # Green
                    ;;
                Failed)
                    outcome_colored="\033[31m$outcome\033[0m"  # Red
                    ;;
                Error)
                    outcome_colored="\033[91m$outcome\033[0m"  # Bright Red
                    ;;
                NotExecuted)
                    outcome_colored="\033[33m$outcome\033[0m"  # Yellow
                    ;;
                *)
                    outcome_colored="$outcome"
                    ;;
            esac
        else
            outcome_colored="$outcome"
        fi
        
        # Format method name: convert CamelCase to "Readable Name"
        readable_name=$(printf '%s' "$method_name" | sed -E 's/([A-Z])/ \1/g' | sed 's/^ //' | sed 's/_/ /g')
        
        printf '%-'"$MAX_NAME_WIDTH"'s  %-10b\n' "$readable_name" "$outcome_colored"
        printed["$method_name"]=1
    fi
done

# Print footer summary
printf '%s\n' "$(printf '%0.s_' {1..85})"
printf '\n'
printf 'Summary:\n'
printf '  Passed:  %3d\n' "$passed"
printf '  Failed:  %3d\n' "$failed"
if ((error > 0)); then
    printf '  Error:   %3d\n' "$error"
fi
if ((skipped > 0)); then
    printf '  Skipped: %3d\n' "$skipped"
fi
printf '  Total:   %3d\n' $((passed + failed + error + skipped))
printf '\n'

# Exit with failure if any tests failed or errored
if ((failed > 0 || error > 0)); then
    exit 1
fi
exit 0
