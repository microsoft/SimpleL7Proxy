#!/bin/bash
# Per-path ordered host sets:
#   /api  -> port 3000 first, then port 3001
#   /api2 -> port 3001 first, then port 3000
# Named Path_* routes are NOT wired into actual host selection (PathRoute.GetCandidateHosts
# is unused), so this uses the legacy per-host "path=" prefix matching instead, with
# prioritygroup controlling the order within each path's matched host set.
unset Host1 Host2 Path_api Path_api2

export Host_api_A="host=http://localhost:3000;mode=direct;path=api;prioritygroup=1"
export Host_api_B="host=http://localhost:3001;mode=direct;path=api;prioritygroup=2"
export Host_api2_A="host=http://localhost:3001;mode=direct;path=api2;prioritygroup=1"
export Host_api2_B="host=http://localhost:3000;mode=direct;path=api2;prioritygroup=2"

export LoadBalanceMode=prioritygroup
