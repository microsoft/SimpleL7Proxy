#!/usr/bin/env bash

# Updates every direct NuGet dependency in SimpleL7Proxy.csproj to its latest
# stable version, except Microsoft.ApplicationInsights* packages. Application
# Insights remains pinned to the existing 2.23.x versions.
#
# Usage:
#   ./update-packages.sh             # update, restore, and build
#   ./update-packages.sh --dry-run   # show eligible outdated packages only

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/SimpleL7Proxy.csproj"
APP_INSIGHTS_PATTERN='^Microsoft\.ApplicationInsights($|\.)'
DRY_RUN=false

usage() {
    cat <<'EOF'
Usage: update-packages.sh [--dry-run]

Updates all direct NuGet dependencies in SimpleL7Proxy.csproj to their latest
stable versions. Microsoft.ApplicationInsights* references remain pinned at
their current 2.23.x versions.

Options:
  --dry-run  List eligible outdated packages without changing the project.
  -h, --help Show this help text.
EOF
}

case "${1:-}" in
    "") ;;
    --dry-run) DRY_RUN=true ;;
    -h|--help)
        usage
        exit 0
        ;;
    *)
        echo "Unknown argument: $1" >&2
        usage >&2
        exit 2
        ;;
esac

command -v dotnet >/dev/null 2>&1 || {
    echo "Error: dotnet is required." >&2
    exit 1
}

command -v jq >/dev/null 2>&1 || {
    echo "Error: jq is required." >&2
    exit 1
}

if ! dotnet package update --help >/dev/null 2>&1; then
    echo "Error: this script requires a .NET SDK with 'dotnet package update' support." >&2
    exit 1
fi

package_json="$(dotnet package list --project "$PROJECT" --format json)"

mapfile -t all_packages < <(
    jq -r '.projects[].frameworks[].topLevelPackages[]?.id' <<<"$package_json" |
        sort -u
)
mapfile -t app_insights_packages < <(
    printf '%s\n' "${all_packages[@]}" | grep -E "$APP_INSIGHTS_PATTERN" || true
)
mapfile -t updatable_packages < <(
    printf '%s\n' "${all_packages[@]}" | grep -Ev "$APP_INSIGHTS_PATTERN" || true
)

if ((${#updatable_packages[@]} == 0)); then
    echo "No eligible direct package references found in $PROJECT."
    exit 0
fi

for package in "${app_insights_packages[@]}"; do
    version="$(
        jq -r --arg id "$package" '
            .projects[].frameworks[].topLevelPackages[]
            | select(.id == $id)
            | .requestedVersion' <<<"$package_json" |
            head -n 1
    )"
    if [[ "$version" != 2.23.* ]]; then
        echo "Error: $package is expected to remain on 2.23.x, but is $version." >&2
        exit 1
    fi
done

echo "Project: $PROJECT"
echo "Preserving Application Insights packages:"
for package in "${app_insights_packages[@]}"; do
    echo "  - $package"
done

if [[ "$DRY_RUN" == true ]]; then
    outdated_json="$(dotnet package list --project "$PROJECT" --outdated --format json)"
    eligible_outdated="$(
        jq -r --arg pattern "$APP_INSIGHTS_PATTERN" '
            .projects[].frameworks[].topLevelPackages[]?
            | select(.id | test($pattern) | not)
            | "  \(.id): \(.resolvedVersion) -> \(.latestVersion)"' <<<"$outdated_json"
    )"

    if [[ -n "$eligible_outdated" ]]; then
        echo "Eligible stable updates:"
        printf '%s\n' "$eligible_outdated"
    else
        echo "All eligible packages are already at their latest stable versions."
    fi
    exit 0
fi

echo "Updating ${#updatable_packages[@]} package(s) to latest stable versions..."
dotnet package update "${updatable_packages[@]}" \
    --project "$PROJECT" \
    --verbosity minimal

updated_json="$(dotnet package list --project "$PROJECT" --format json)"
for package in "${app_insights_packages[@]}"; do
    before="$(
        jq -r --arg id "$package" '
            .projects[].frameworks[].topLevelPackages[]
            | select(.id == $id)
            | .requestedVersion' <<<"$package_json" |
            head -n 1
    )"
    after="$(
        jq -r --arg id "$package" '
            .projects[].frameworks[].topLevelPackages[]
            | select(.id == $id)
            | .requestedVersion' <<<"$updated_json" |
            head -n 1
    )"
    if [[ "$after" != "$before" ]]; then
        echo "Error: $package changed from $before to $after." >&2
        exit 1
    fi
done

dotnet restore "$PROJECT"
dotnet build "$PROJECT" --no-restore \
    /property:GenerateFullPaths=true \
    /consoleloggerparameters:NoSummary

echo "Package update complete. Application Insights remains pinned to 2.23.x."