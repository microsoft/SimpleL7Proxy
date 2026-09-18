#!/usr/bin/env bash
set -euo pipefail

readonly usage='Usage: bash deploy.sh IMAGE_TAG [--build-only]

Build and push the CompanionApp container image to an existing Azure Container Registry.
Use --build-only to build a local companionapp:IMAGE_TAG image without Azure access.
This script publishes an image; it does not create a registry or deploy a running app.

Prerequisites:
  Docker with a running Linux container engine.
  Azure CLI, an existing az login session, and registry push access when publishing.

Environment variables:
  ACR               Registry resource name. Required when publishing.
  SUBSCRIPTION_ID   Subscription containing the registry. Required when publishing.
  ACRGROUP          Registry resource group. Optional.
  PLATFORM          Container platform. Default: linux/amd64.

Examples:
  bash deploy.sh v1.0.0 --build-only
  ACR=myregistry SUBSCRIPTION_ID=my-subscription-id bash deploy.sh v1.0.0'

if [[ $# -eq 1 && ( "$1" == '-h' || "$1" == '--help' ) ]]; then
    printf '%s\n' "$usage"
    exit 0
fi

if [[ $# -lt 1 || $# -gt 2 ]]; then
    printf '%s\n' "$usage" >&2
    exit 2
fi

readonly image_tag="$1"
readonly build_only="${2:-}"
readonly platform="${PLATFORM:-linux/amd64}"

if [[ ! "$image_tag" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$ ]]; then
    printf '%s\n' 'Error: IMAGE_TAG must be 1-128 letters, digits, underscores, periods, or hyphens, and cannot start with a period or hyphen.' >&2
    exit 2
fi

if [[ $# -eq 2 && "$build_only" != '--build-only' ]]; then
    printf 'Error: unsupported option: %s\n%s\n' "$build_only" "$usage" >&2
    exit 2
fi

if [[ "$build_only" != '--build-only' && ( -z "${ACR:-}" || -z "${SUBSCRIPTION_ID:-}" ) ]]; then
    printf '%s\n' 'Error: set ACR and SUBSCRIPTION_ID before publishing, or use --build-only for a local image.' >&2
    exit 2
fi

if ! command -v docker >/dev/null 2>&1; then
    printf '%s\n' 'Error: Docker is not installed or is not on PATH.' >&2
    exit 1
fi

if [[ "$build_only" != '--build-only' ]]; then
    if ! command -v az >/dev/null 2>&1; then
        printf '%s\n' 'Error: Azure CLI is not installed or is not on PATH. Install it and run az login before publishing.' >&2
        exit 1
    fi
fi

if ! docker info >/dev/null; then
    printf '%s\n' 'Error: the Docker engine is unavailable. Start Docker and check your access to the engine.' >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly script_dir
repo_root="$(cd -- "$script_dir/../.." && pwd)"
readonly repo_root

if [[ "$build_only" == '--build-only' ]]; then
    image_name="companionapp:$image_tag"
else
    registry_args=(--name "$ACR" --subscription "$SUBSCRIPTION_ID")
    if [[ -n "${ACRGROUP:-}" ]]; then
        registry_args+=(--resource-group "$ACRGROUP")
    fi
    readonly -a registry_args

    registry_server="$(az acr show "${registry_args[@]}" --query loginServer --output tsv --only-show-errors)"
    readonly registry_server
    if [[ -z "$registry_server" ]]; then
        printf '%s\n' 'Error: Azure did not return a login server for the registry.' >&2
        exit 1
    fi
    image_name="$registry_server/companionapp:$image_tag"
fi
readonly image_name

printf 'Building %s for %s...\n' "$image_name" "$platform"
docker build \
    --pull \
    --platform "$platform" \
    --tag "$image_name" \
    --file "$script_dir/Dockerfile" \
    "$repo_root"

if [[ "$build_only" == '--build-only' ]]; then
    printf 'Local image ready: %s\n' "$image_name"
    exit 0
fi

printf 'Logging into %s...\n' "$registry_server"
az acr login "${registry_args[@]}"

printf 'Pushing %s...\n' "$image_name"
docker push "$image_name"

printf 'Published image: %s\n' "$image_name"
