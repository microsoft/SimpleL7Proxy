#!/bin/bash

# filepath: /c:/Users/nmishr/OneDrive - Microsoft/repos/microsoft/SimpleL7Proxy/extract_version.sh

# Extract the version from Constants.cs
ver=$(grep -oP 'VERSION = "\K[^"]+' ../src/SimpleL7Proxy/Constants.cs)

# add v if it doesnt start with it already
if [[ ! $ver == v* ]]; then
    ver="v$ver"
fi

# Output the version
echo "Extracted Version: $ver"

# Build from src directory to include Shared project
cd ../src
docker build -t $ACR.azurecr.io/myproxy:$ver -f SimpleL7Proxy/Dockerfile .
docker push $ACR.azurecr.io/myproxy:$ver
