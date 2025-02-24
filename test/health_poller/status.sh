#!/bin/bash

while true
do
  # Call wget on localhost/health
  time curl http://localhost:3000/health

  # Sleep for 1 second
  sleep 5
done
