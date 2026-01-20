#!/bin/bash

# filepath: /c:/Users/nmishr/OneDrive - Microsoft/repos/microsoft/SimpleL7Proxy/extract_version.sh

# Extract the version from Constants.cs
ver=$(grep -oP 'VERSION = "\K[^"]+' Constants.cs)

# add v if it doesnt start with it already
if [[ ! $ver == v* ]]; then
    ver="v$ver"
fi

# Output the version
echo "Extracted Version: $ver"

# Build from parent directory (src) to include Shared project
cd ..
docker build -t $ACR.azurecr.io/myproxy:$ver -f SimpleL7Proxy/Dockerfile .
docker push $ACR.azurecr.io/myproxy:$ver
