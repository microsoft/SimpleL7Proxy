#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

dotnet test "$script_dir/SimpleL7Proxy.Test.csproj" -- \
    --filter "FullyQualifiedName~PolicyScenarioIntegrationTests.V31Policy_AllConfiguredScenariosMatchExpectedBehavior"
