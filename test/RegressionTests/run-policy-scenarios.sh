#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/run-regression-master.sh"

regression_run \
    "APIM policy scenarios" \
    "FullyQualifiedName~PolicyScenarioIntegrationTests.V31Policy_AllConfiguredScenariosMatchExpectedBehavior"
