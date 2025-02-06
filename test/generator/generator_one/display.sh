#!/bin/bash

while true; do
  # Get the revision name
  revision=$(az containerapp revision list --name $CONTAINER --resource-group $GROUP --query "[].name" -o tsv)

  # Get the list of replicas
  replicas=$(az containerapp replica list --name $CONTAINER --resource-group $GROUP --query "[].name" -o tsv)

  # Count the number of replicas
  replica_count=$(echo "$replicas" | wc -l)

  # Clear the screen
  clear

  # Output the revision name
  echo "Revision name: $revision"

  # Output the number of replicas
  echo "Number of replicas: $replica_count"

  # Output the names of the replicas
  echo "Replica names:"
  echo "$replicas"

  # Wait for 1 second
  sleep 1
done
