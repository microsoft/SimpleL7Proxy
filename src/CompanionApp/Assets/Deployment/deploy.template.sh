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
			.ENVIRONMENT_RESOURCE_GROUP as $environment_group
			| .USE_EXISTING_ENVIRONMENT as $use_existing_environment
			| .RESOURCE_GROUPS |= map(if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .NETWORK_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .CONTAINER_APP_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .STORAGE_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .APPCONFIG_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .REQUESTAPI_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .COMPANION_APP_RESOURCE_GROUP |= (if $use_existing_environment and . == $environment_group then . else with_hyphen(90) end)
			| .COMPANION_EVENTHUB_NAMESPACE |= with_hyphen(50)
			| .SERVICEBUS_RESOURCE_GROUP |= (if contains($placeholder) then gsub($placeholder; $suffix) else . end)
			| .COSMOS_RESOURCE_GROUP |= (if contains($placeholder) then gsub($placeholder; $suffix) else . end)
			| .ACR_NAME |= compact(50)
			| .CONTAINER_APP_NAME |= with_hyphen(32)
			| .COMPANION_APP_NAME |= with_hyphen(32)
			| .METRICS_SERVER_NAME |= with_hyphen(32)
			| .LOG_ANALYTICS_WORKSPACE_NAME |= with_hyphen(63)
			| .ENVIRONMENT_NAME |= (if $use_existing_environment then . else with_hyphen(60) end)
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

	{{PROXY_IMAGE_IMPORT}}

	{{HEALTH_IMAGE_IMPORT}}

	{{COMPANION_IMAGE_IMPORT}}

	{{METRICS_IMAGE_IMPORT}}
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
		--query 'properties.outputs.{proxyUrl:proxyUrl.value,companionAppUrl:companionAppUrl.value,metricsServerUrl:metricsServerUrl.value,profileSeedJobName:profileSeedJobName.value,profileSeedJobResourceGroup:profileSeedJobResourceGroup.value}' \
		--output json)"
	proxy_url="$(jq -r '.proxyUrl // empty' <<< "$deployment_outputs")"
	companion_app_url="$(jq -r '.companionAppUrl // empty' <<< "$deployment_outputs")"
	metrics_server_url="$(jq -r '.metricsServerUrl // empty' <<< "$deployment_outputs")"
	readonly deployment_outputs proxy_url companion_app_url metrics_server_url
	if jq -e '.parameters.settings.value.DEPLOY_STORAGE_NETWORK == true' "$deployment_parameters_file" >/dev/null; then
		profile_job="$(jq -er '.profileSeedJobName | select(type == "string" and length > 0)' <<< "$deployment_outputs")"
		profile_group="$(jq -er '.profileSeedJobResourceGroup | select(type == "string" and length > 0)' <<< "$deployment_outputs")"
		arm_endpoint="$(az cloud show --query endpoints.resourceManager --output tsv)"
		profile_job_url="${arm_endpoint%/}/subscriptions/$subscription/resourceGroups/$profile_group/providers/Microsoft.App/jobs/$profile_job"
		if ! command -v curl >/dev/null 2>&1; then
			printf '%s\n' 'Error: curl is required to start the profile seed job and read its asynchronous operation headers.' >&2
			exit 1
		fi
		arm_audience="$(az cloud show --query endpoints.activeDirectoryResourceId --output tsv)"
		profile_token="$(az account get-access-token --subscription "$subscription" --resource "$arm_audience" --query accessToken --output tsv)"
		# az rest and the CLI job-start command do not follow this API's 202 Location response.
		profile_start_response="$(printf '%s: %s %s\n' 'Authorization' 'Bearer' "$profile_token" | curl \
			--silent --show-error --fail --include --max-time 120 \
			--request POST --header 'Content-Length: 0' --header @- --url "$profile_job_url/start?api-version=2024-03-01")"
		unset profile_token
		profile_start_response+=$'\n'
		profile_start_body="${profile_start_response##*$'\r\n\r\n'}"
		profile_start_code="$(awk '/^HTTP\// { code = $2; sub(/\r$/, "", code) } END { print code }' <<< "$profile_start_response")"
		if [[ "$profile_start_code" == '202' ]]; then
			profile_start_location="$(awk 'tolower($1) == "location:" { sub(/\r$/, ""); print substr($0, index($0, ":") + 2) }' <<< "$profile_start_response")"
			if [[ "$profile_start_location" == /* && "$profile_start_location" != //* ]]; then
				profile_start_location="${arm_endpoint%/}$profile_start_location"
			fi
			if [[ "$profile_start_location" != "${arm_endpoint%/}/"* ]]; then
				printf '%s\n' 'Error: profile seed start returned a missing or untrusted polling Location.' >&2
				exit 1
			fi
			for ((profile_start_poll = 0; profile_start_poll < 150; profile_start_poll++)); do
				sleep 10
				profile_start_body="$(az rest --method get --url "$profile_start_location" --output json)"
				if [[ -z "$profile_start_body" ]]; then profile_start_body='{}'; fi
				profile_start_state="$(jq -r '.status // .properties.provisioningState // empty' <<< "$profile_start_body")"
				if [[ "$profile_start_state" == 'Failed' || "$profile_start_state" == 'Canceled' ]]; then
					printf 'Error: profile seed start operation ended with %s.\n' "$profile_start_state" >&2
					exit 1
				fi
				if jq -e '.name | type == "string" and length > 0' <<< "$profile_start_body" >/dev/null; then
					break
				fi
			done
		elif [[ "$profile_start_code" != '200' ]]; then
			printf 'Error: unexpected profile seed start HTTP status %s.\n' "$profile_start_code" >&2
			exit 1
		fi
		if [[ -z "$profile_start_body" ]]; then profile_start_body='{}'; fi
		profile_execution="$(jq -r '.name // empty' <<< "$profile_start_body")"
		if [[ -z "$profile_execution" || "$profile_execution" == 'null' ]]; then
			printf '%s\n' 'Error: the profile seed job did not return an execution name.' >&2
			exit 1
		fi
		printf 'Profile seed execution: %s (job %s in %s)\n' "$profile_execution" "$profile_job" "$profile_group"
		profile_status=''
		for ((profile_poll = 0; profile_poll < 150; profile_poll++)); do
			profile_status="$(az rest --method get --url "$profile_job_url/executions/$profile_execution?api-version=2024-03-01" --query properties.status --output tsv)"
			case "$profile_status" in
				Succeeded) break ;;
				Failed|Stopped|Degraded)
					printf 'Error: profile seed execution %s ended with %s. Inspect Container Apps job execution logs.\n' "$profile_execution" "$profile_status" >&2
					exit 1
					;;
			esac
			sleep 10
		done
		if [[ "$profile_status" != 'Succeeded' ]]; then
			printf 'Error: timed out waiting for profile seed execution %s. Inspect its status and logs before retrying.\n' "$profile_execution" >&2
			exit 1
		fi
		printf '%s\n' 'Profile seed succeeded: profiles/config.json was created or preserved.'
	fi
	if jq -e '.parameters.settings.value.DEPLOY_PROXY == true' "$deployment_parameters_file" >/dev/null && [[ -z "$proxy_url" ]]; then
		printf '%s\n' 'Error: the deployment did not return a proxy URL.' >&2
		exit 1
	fi
	if jq -e '.parameters.settings.value.DEPLOY_COMPANION_APP == true' "$deployment_parameters_file" >/dev/null && [[ -z "$companion_app_url" ]]; then
		printf '%s\n' 'Error: the deployment did not return a Companion App URL.' >&2
		exit 1
	fi
	if [[ -n "$proxy_url" ]]; then
		printf '\nProxy URL: %s\n' "$proxy_url"
	else
		printf '\nProxy: not deployed\n'
	fi
	if [[ -n "$companion_app_url" ]]; then
		printf 'Companion App URL: %s\n' "$companion_app_url"
	else
		printf '%s\n' 'Companion App URL: not deployed'
	fi
	if [[ -n "$metrics_server_url" ]]; then
		printf 'Metrics Server URL: %s\n' "$metrics_server_url"
	else
		printf '%s\n' 'Metrics Server: not deployed'
	fi
fi
