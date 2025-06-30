#!/bin/bash

# Run SimpleL7Proxy container with internet access
docker run \
  -p 8000:443 \
  -p 8001:8000 \
  --env-file docker.env \
  --network bridge \
  --dns 8.8.8.8 \
  --dns 8.8.4.4 \
  --add-host host.docker.internal:host-gateway \
  simplel7proxy:latest