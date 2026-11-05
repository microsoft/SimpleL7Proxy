#!/usr/bin/env bash
set -euo pipefail

readonly requestCount=1000
readonly concurrency=10
readonly url1="${1:-http://localhost:7071/api/process?url=http://localhost:3000/file-stream/openAI.txt&sourceModel=gpt-4o.Completions&targetModel=claude-sonnet-4-5.Messages}"
readonly url2="${2:-http://localhost:7071/api/process?url=http://localhost:3000/file-stream/openAI.txt&sourceModel=gpt-4o.Completions&targetModel=gpt-4o.Completions}"

if ! command -v ab >/dev/null 2>&1; then
    printf 'ApacheBench (ab) is required. On Ubuntu, install apache2-utils.\n' >&2
    exit 127
fi

exitCode=0
results=()
for url in "$url1" "$url2"; do
    if output=$(ab -n "$requestCount" -c "$concurrency" "$url" 2>&1); then
        results+=("$output")
    else
        printf 'ApacheBench failed for %s:\n%s\n' "$url" "$output" >&2
        results+=("")
        exitCode=1
    fi
done

printf '%s\n' 'RESULT 1' "${results[0]}" 'RESULT 2' "${results[1]}" | awk -v requested="$requestCount" -v commandFailed="$exitCode" -v firstUrl="$url1" -v secondUrl="$url2" '
    function modelLabel(url, parameters, parameterCount, parameterIndex, value, sourceModel, targetModel) {
        parameterCount = split(url, parameters, /[?&]/)
        for (parameterIndex = 2; parameterIndex <= parameterCount; parameterIndex++) {
            value = parameters[parameterIndex]
            if (sub(/^sourceModel=/, "", value)) sourceModel = value
            else if (sub(/^targetModel=/, "", value)) targetModel = value
        }
        return sourceModel != "" && targetModel != "" ? sourceModel " -> " targetModel : url
    }
    /^RESULT [12]$/ { run = $2; next }
    /^Time taken for tests:/ { total[run] = $5 }
    /^Time per request:/ && !/across all concurrent/ { mean[run] = $4 }
    /^Complete requests:/ { complete[run] = $3 }
    /^Failed requests:/ { failed[run] = $3 }
    /^Non-2xx responses:/ { non2xx[run] = $3 }
    /^Write errors:/ { writeErrors[run] = $3 }
    /^[[:space:]]*[0-9]+%/ { latency[run, $1] = $2 }
    END {
        names[1] = modelLabel(firstUrl)
        names[2] = modelLabel(secondUrl)
        invalid = commandFailed
        for (run = 1; run <= 2; run++) {
            if (!(run in total) || !(run in mean) || !(run in failed) ||
                !((run SUBSEP "50%") in latency) || !((run SUBSEP "90%") in latency) ||
                !((run SUBSEP "95%") in latency) || !((run SUBSEP "99%") in latency) ||
                !((run SUBSEP "100%") in latency) || complete[run] != requested) {
                printf "%s: benchmark failed or incomplete\n", names[run]
                invalid = 1
                continue
            }
            printf "%s: total=%.3fs mean=%.3fms p50=%sms p90=%sms p95=%sms p99=%sms max=%sms failed=%d non2xx=%d writes=%d\n", names[run], total[run], mean[run], latency[run, "50%"], latency[run, "90%"], latency[run, "95%"], latency[run, "99%"], latency[run, "100%"], failed[run], non2xx[run], writeErrors[run]
            if (failed[run] || non2xx[run] || writeErrors[run] || total[run] <= 0) invalid = 1
        }
        if (invalid) {
            print "Estimated added latency: unavailable (errors or incomplete results)"
            exit 1
        }
        if (mean[1] > mean[2]) {
            printf "URL1 > URL2: URL1 is slower by %.4f ms/call (mean response time)\n", mean[1] - mean[2]
        } else if (mean[1] < mean[2]) {
            printf "URL1 < URL2: URL2 is slower by %.4f ms/call (mean response time)\n", mean[2] - mean[1]
        } else {
            print "URL1 = URL2: neither is slower; difference 0.0000 ms/call (mean response time)"
        }
    }
'