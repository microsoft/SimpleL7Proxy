#!/usr/bin/env bash

set -euo pipefail

readonly default_make_uniq=false
readonly usage='Usage: bash deploy.sh SUBSCRIPTION_ID [validate|what-if|create] [--MakeUniq]'

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
		'  --MakeUniq  Add a generated four-digit suffix to deployment-created resources.' \
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
make_uniq="$default_make_uniq"

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
		--MakeUniq)
			make_uniq=true
			shift
			;;
		*)
			printf 'Error: unsupported argument "%s".\n%s\n' "$1" "$usage" >&2
			exit 1
			;;
	esac
done

readonly subscription operation make_uniq

if ! command -v az >/dev/null 2>&1; then
	printf '%s\n' 'Error: Azure CLI with Bicep support is required. Install it and sign in before continuing.' >&2
	exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
	printf '%s\n' 'Error: jq is required to read and update parameters.json.' >&2
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
deployment_parameters_file="$parameters_file"

if [[ "$make_uniq" == true ]]; then
	stored_unique_suffix="$(jq -r '.parameters.settings.value.MAKE_UNIQ_SUFFIX // empty' "$parameters_file")"
	readonly stored_unique_suffix
	if [[ -n "$stored_unique_suffix" ]]; then
		if [[ ! "$stored_unique_suffix" =~ ^[0-9]{4}$ ]]; then
			printf '%s\n' 'Error: parameters.json contains an invalid MAKE_UNIQ_SUFFIX.' >&2
			exit 1
		fi
		if ! jq -e --arg suffix "$stored_unique_suffix" '
			.parameters.settings.value as $settings
			| [$settings.CONTAINER_APP_RESOURCE_GROUP, $settings.ACR_NAME, $settings.CONTAINER_APP_NAME]
			| all(.[]; type == "string" and endswith($suffix))
		' "$parameters_file" >/dev/null; then
			printf '%s\n' 'Error: parameters.json resource names do not match MAKE_UNIQ_SUFFIX.' >&2
			exit 1
		fi
		printf 'Reusing four-digit deployment suffix: %s\n' "$stored_unique_suffix"
	else
		printf -v unique_suffix '%04d' "$((RANDOM % 10000))"
		readonly unique_suffix
		temporary_parameters="$(mktemp "${TMPDIR:-/tmp}/simplel7proxy-parameters.XXXXXX")"
		readonly temporary_parameters
		jq --arg placeholder '<UNIQ>' --arg suffix "$unique_suffix" '
		def with_hyphen($max_length):
			if type == "string" and length > 0 then
				if contains($placeholder) then gsub($placeholder; $suffix)
				else (.[0:($max_length - 5)] | rtrimstr("-")) + "-" + $suffix end
			else . end;
		def compact($max_length):
			if type == "string" and length > 0 then
				if contains($placeholder) then gsub($placeholder; $suffix)
				else .[0:($max_length - 4)] + $suffix end
			else . end;
		.parameters.settings.value |= (
			.RESOURCE_GROUPS |= map(with_hyphen(90))
			| .NETWORK_RESOURCE_GROUP |= with_hyphen(90)
			| .CONTAINER_APP_RESOURCE_GROUP |= with_hyphen(90)
			| .STORAGE_RESOURCE_GROUP |= with_hyphen(90)
			| .APPCONFIG_RESOURCE_GROUP |= with_hyphen(90)
			| .REQUESTAPI_RESOURCE_GROUP |= with_hyphen(90)
			| .COMPANION_APP_RESOURCE_GROUP |= with_hyphen(90)
			| .SERVICEBUS_RESOURCE_GROUP |= (if contains($placeholder) then gsub($placeholder; $suffix) else . end)
			| .COSMOS_RESOURCE_GROUP |= (if contains($placeholder) then gsub($placeholder; $suffix) else . end)
			| .ACR_NAME |= compact(50)
			| .CONTAINER_APP_NAME |= with_hyphen(32)
			| .COMPANION_APP_NAME |= with_hyphen(32)
			| .LOG_ANALYTICS_WORKSPACE_NAME |= with_hyphen(63)
			| .ENVIRONMENT_NAME |= with_hyphen(60)
			| .APPCONFIG_NAME |= with_hyphen(50)
			| .VNET_NAME |= with_hyphen(64)
			| .ACA_RECORD_NAME |= with_hyphen(32)
			| .STORAGE_ACCOUNT_NAME |= compact(24)
			| .REQUESTAPI_FUNCTION_APP |= with_hyphen(60)
			| .REQUESTAPI_STORAGE_ACCOUNT |= compact(24)
			| .REQUESTAPI_APPINSIGHTS_NAME |= with_hyphen(260)
			| .MAKE_UNIQ_SUFFIX = $suffix
		)' "$parameters_file" > "$temporary_parameters"
		deployment_parameters_file="$temporary_parameters"
		printf 'Generated four-digit deployment suffix: %s\n' "$unique_suffix"
		printf 'Generated parameters file: %s\n' "$temporary_parameters"
	fi
fi
readonly deployment_parameters_file

deployment_values="$(jq -er '
	.parameters.settings.value as $settings
	| [$settings.LOCATION, $settings.CONTAINER_APP_RESOURCE_GROUP, $settings.ACR_NAME, $settings.CONTAINER_APP_NAME]
	| if all(.[]; type == "string" and length > 0) then @tsv else error("missing deployment value") end
' "$deployment_parameters_file")"
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
		--parameters @"$deployment_parameters_file"

	az acr import \
		--subscription "$subscription" \
		--resource-group "$acr_resource_group" \
		--name "$acr_name" \
		--source 'publicnvmacr.azurecr.io/simplel7proxy@sha256:2ebaff3e90fc9162421f08f8095a030627c4aea7046b3da59a8ea7720e4c530f' \
		--image 'simple-l7-proxy:v2.3.0' \
		--force

	az acr import --subscription "$subscription" --resource-group "$acr_resource_group" --name "$acr_name" --source 'publicnvmacr.azurecr.io/healthprobe@sha256:e28a0bd8555d97ec800f80cb37201785e4ceb9c783fbedf679de5145e3c689d1' --image 'healthprobe:v2.0.1' --force

	az acr import --subscription "$subscription" --resource-group "$acr_resource_group" --name "$acr_name" --source 'publicnvmacr.azurecr.io/companionapp:v2.3.0' --image 'companionapp:v2.3.0' --force
fi

az deployment sub "$operation" \
	--subscription "$subscription" \
	--name "$deployment_name" \
	--location "$location" \
	--template-file "$script_dir/main.bicep" \
	--parameters @"$deployment_parameters_file"

if [[ "$operation" == 'create' ]]; then
	deployment_outputs="$(az deployment sub show \
		--subscription "$subscription" \
		--name "$deployment_name" \
		--query 'properties.outputs.{proxyUrl:proxyUrl.value,companionAppUrl:companionAppUrl.value}' \
		--output json)"
	proxy_url="$(jq -r '.proxyUrl // empty' <<< "$deployment_outputs")"
	companion_app_url="$(jq -r '.companionAppUrl // empty' <<< "$deployment_outputs")"
	readonly deployment_outputs proxy_url companion_app_url
	if [[ -z "$proxy_url" ]]; then
		printf '%s\n' 'Error: the deployment did not return a proxy URL.' >&2
		exit 1
	fi
	if jq -e '.parameters.settings.value.DEPLOY_COMPANION_APP == true' "$deployment_parameters_file" >/dev/null && [[ -z "$companion_app_url" ]]; then
		printf '%s\n' 'Error: the deployment did not return a Companion App URL.' >&2
		exit 1
	fi
	printf '\nProxy URL: %s\n' "$proxy_url"
	if [[ -n "$companion_app_url" ]]; then
		printf 'Companion App URL: %s\n' "$companion_app_url"
	else
		printf '%s\n' 'Companion App URL: not deployed'
	fi
fi
