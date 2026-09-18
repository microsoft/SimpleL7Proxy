#!/usr/bin/env bash

set -euo pipefail

readonly usage='Usage: bash deploy.sh SUBSCRIPTION_ID [validate|what-if|create] [--unique-id ID]'

if (( $# == 1 )) && [[ "$1" == '-h' || "$1" == '--help' ]]; then
	printf '%s\n' \
		"$usage" \
		'' \
		'Operations:' \
		'  validate  Validate the deployment without creating resources (default).' \
		'  what-if   Preview the resource changes without deploying them.' \
		'  create    Deploy the selected infrastructure; resources can incur charges.' \
		'' \
		'Options:' \
		'  --unique-id ID  Use a five-character lowercase alphanumeric deployment ID.' \
		'' \
		'Run from the extracted ZIP with Azure CLI and Bicep support installed.' \
		'Sign in to Azure before running a deployment operation.'
	exit 0
fi

if (( $# < 1 )); then
	printf '%s\n' 'Error: expected a subscription ID.' "$usage" >&2
	exit 1
fi

subscription="$1"
shift

if [[ -z "$subscription" || "$subscription" == -* ]]; then
	printf '%s\n' 'Error: provide a subscription ID as the first argument.' "$usage" >&2
	exit 1
fi

operation='validate'
operation_set=false
requested_unique_id=''

while (( $# > 0 )); do
	case "$1" in
		validate|what-if|create)
			if [[ "$operation_set" == true ]]; then
				printf '%s\n' 'Error: specify only one deployment operation.' "$usage" >&2
				exit 1
			fi
			operation="$1"
			operation_set=true
			shift
			;;
		--unique-id)
			if (( $# < 2 )); then
				printf '%s\n' 'Error: --unique-id requires a value.' "$usage" >&2
				exit 1
			fi
			if [[ -n "$requested_unique_id" ]]; then
				printf '%s\n' 'Error: specify --unique-id only once.' "$usage" >&2
				exit 1
			fi
			requested_unique_id="$2"
			shift 2
			;;
		*)
			printf 'Error: unsupported argument "%s".\n%s\n' "$1" "$usage" >&2
			exit 1
			;;
	esac
done

readonly subscription operation requested_unique_id

if [[ -n "$requested_unique_id" && ! "$requested_unique_id" =~ ^[a-z0-9]{5}$ ]]; then
	printf '%s\n' 'Error: --unique-id must be exactly five lowercase letters or digits.' "$usage" >&2
	exit 1
fi

if ! command -v az >/dev/null 2>&1; then
	printf '%s\n' 'Error: Azure CLI with Bicep support is required. Install it and sign in before continuing.' >&2
	exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
	printf '%s\n' 'Error: jq is required to resolve and read parameters.json.' >&2
	exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly script_dir

if [[ ! -f "$script_dir/main.bicep" || ! -f "$script_dir/bootstrap.bicep" || ! -f "$script_dir/parameters.json" ]]; then
	printf '%s\n' 'Error: main.bicep, bootstrap.bicep, or parameters.json is missing.' >&2
	printf '%s\n' 'Extract the complete ZIP and keep the Bicep templates, modules, and parameters file beside deploy.sh.' >&2
	exit 1
fi

readonly parameters_file="$script_dir/parameters.json"
readonly unique_placeholder='<UNIQ>'

if grep -qF "$unique_placeholder" "$parameters_file"; then
	unique_suffix="$requested_unique_id"
	if [[ -z "$unique_suffix" ]]; then
		printf -v unique_suffix '%05x' "$(((RANDOM << 5) | (RANDOM & 31)))"
	fi
	readonly unique_suffix

	temporary_parameters="$(mktemp "${parameters_file}.XXXXXX")"
	readonly temporary_parameters
	trap 'rm -f "$temporary_parameters"' EXIT
	cp -p "$parameters_file" "$temporary_parameters"
	if ! jq --arg placeholder "$unique_placeholder" --arg suffix "$unique_suffix" \
		'walk(if type == "string" then gsub($placeholder; $suffix) else . end)' \
		"$parameters_file" > "$temporary_parameters"; then
		printf '%s\n' 'Error: unable to resolve <UNIQ> in parameters.json.' >&2
		exit 1
	fi
	mv "$temporary_parameters" "$parameters_file"
	trap - EXIT
	if [[ -n "$requested_unique_id" ]]; then
		printf 'Using requested deployment suffix: %s\n' "$unique_suffix"
	else
		printf 'Generated deployment suffix: %s\n' "$unique_suffix"
	fi
elif [[ -n "$requested_unique_id" ]]; then
	if ! jq -e --arg suffix "$requested_unique_id" '
		.parameters.settings.value as $settings
		| [$settings.CONTAINER_APP_RESOURCE_GROUP, $settings.ACR_NAME, $settings.CONTAINER_APP_NAME]
		| all(.[]; type == "string" and endswith($suffix))
	' "$parameters_file" >/dev/null; then
		printf 'Error: parameters.json is already resolved with a different deployment ID; cannot apply --unique-id "%s".\n' "$requested_unique_id" >&2
		exit 1
	fi
	printf 'Reusing requested deployment suffix: %s\n' "$requested_unique_id"
fi

if grep -qF "$unique_placeholder" "$parameters_file"; then
	printf '%s\n' 'Error: parameters.json still contains an unresolved <UNIQ> placeholder.' >&2
	exit 1
fi

deployment_values=''
if ! deployment_values="$(jq -er '
	.parameters.settings.value as $settings
	| [$settings.LOCATION, $settings.CONTAINER_APP_RESOURCE_GROUP, $settings.ACR_NAME, $settings.CONTAINER_APP_NAME]
	| if all(.[]; type == "string" and length > 0) then @tsv else error("missing deployment value") end
' "$parameters_file")"; then
	printf '%s\n' 'Error: parameters.json is missing a required deployment value.' >&2
	exit 1
fi
readonly deployment_values

IFS=$'\t' read -r location acr_resource_group acr_name container_app_name <<< "$deployment_values"
readonly location acr_resource_group acr_name container_app_name
deployment_name="${container_app_name}-bicep"
bootstrap_deployment_name="${deployment_name}-bootstrap"
readonly deployment_name bootstrap_deployment_name

printf 'Deployment name: %s\n' "$deployment_name"

if [[ "$operation" == 'create' ]]; then
	az deployment sub create \
		--subscription "$subscription" \
		--name "$bootstrap_deployment_name" \
		--location "$location" \
		--template-file "$script_dir/bootstrap.bicep" \
		--parameters @"$parameters_file"

	az acr import \
		--subscription "$subscription" \
		--resource-group "$acr_resource_group" \
		--name "$acr_name" \
		--source 'publicnvmacr.azurecr.io/simplel7proxy@sha256:2ebaff3e90fc9162421f08f8095a030627c4aea7046b3da59a8ea7720e4c530f' \
		--image 'simple-l7-proxy:v2.3.0' \
		--force

	az acr import --subscription "$subscription" --resource-group "$acr_resource_group" --name "$acr_name" --source 'publicnvmacr.azurecr.io/healthprobe@sha256:e28a0bd8555d97ec800f80cb37201785e4ceb9c783fbedf679de5145e3c689d1' --image 'healthprobe:v2.0.1' --force
fi

az deployment sub "$operation" \
	--subscription "$subscription" \
	--name "$deployment_name" \
	--location "$location" \
	--template-file "$script_dir/main.bicep" \
	--parameters @"$parameters_file"
