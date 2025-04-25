#!/bin/bash

# filepath: /c:/Users/nmishr/OneDrive - Microsoft/repos/microsoft/SimpleL7Proxy/extract_version.sh

# Extract the version from Program.cs
#ver=$(grep -oP 'Version: \K\d+\.\d+\.\d+' Program.cs)
ver=$(grep -oP 'VERSION = "\K[^"]+' Constants.cs)

# add v if it doesnt start with it already
if [[ ! $ver == v* ]]; then
    ver="v$ver"
fi

# Output the version
echo "Extracted Version: $ver"
docker build -t $ACR.azurecr.io/myproxy:$ver -f Dockerfile .
docker push $ACR.azurecr.io/myproxy:$ver
