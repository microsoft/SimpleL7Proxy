#!/usr/bin/env bash

set -euo pipefail

readonly usage='Usage: bash deploy.sh SUBSCRIPTION_ID [validate|what-if|create]'

if (( $# == 1 )) && [[ "$1" == '-h' || "$1" == '--help' ]]; then
	printf '%s\n' \
		"$usage" \
		'' \
		'Operations:' \
		'  validate  Validate the deployment without creating resources (default).' \
		'  what-if   Preview the resource changes without deploying them.' \
		'  create    Deploy the selected infrastructure; resources can incur charges.' \
		'' \
		'Run from the extracted ZIP with Azure CLI and Bicep support installed.' \
		'Sign in to Azure before running a deployment operation.'
	exit 0
fi

if (( $# < 1 || $# > 2 )); then
	printf '%s\n' 'Error: expected a subscription ID and an optional operation.' "$usage" >&2
	exit 1
fi

readonly subscription="$1"
readonly operation="${2:-validate}"

if [[ -z "$subscription" || "$subscription" == -* ]]; then
	printf '%s\n' 'Error: provide a subscription ID as the first argument.' "$usage" >&2
	exit 1
fi

case "$operation" in
	validate|what-if|create)
		;;
	*)
		printf 'Error: unsupported operation "%s". Expected validate, what-if, or create.\n' "$operation" >&2
		exit 1
		;;
esac

if ! command -v az >/dev/null 2>&1; then
	printf '%s\n' 'Error: Azure CLI with Bicep support is required. Install it and sign in before continuing.' >&2
	exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly script_dir

if [[ ! -f "$script_dir/main.bicep" || ! -f "$script_dir/bootstrap.bicep" || ! -f "$script_dir/parameters.json" ]]; then
	printf '%s\n' 'Error: main.bicep, bootstrap.bicep, or parameters.json is missing.' >&2
	printf '%s\n' 'Extract the complete ZIP and keep the Bicep templates, modules, and parameters file beside deploy.sh.' >&2
	exit 1
fi

if [[ "$operation" == 'create' ]]; then
	az deployment sub create \
		--subscription "$subscription" \
		--name {{BOOTSTRAP_DEPLOYMENT_NAME}} \
		--location {{LOCATION}} \
		--template-file "$script_dir/bootstrap.bicep" \
		--parameters @"$script_dir/parameters.json"

	az acr import \
		--subscription "$subscription" \
		--resource-group {{ACR_RESOURCE_GROUP}} \
		--name {{ACR_NAME}} \
		--source {{PROXY_SOURCE_IMAGE}} \
		--image {{PROXY_TARGET_IMAGE}} \
		--force

	{{HEALTH_IMAGE_IMPORT}}
fi

az deployment sub "$operation" \
	--subscription "$subscription" \
	--name {{DEPLOYMENT_NAME}} \
	--location {{LOCATION}} \
	--template-file "$script_dir/main.bicep" \
	--parameters @"$script_dir/parameters.json"
