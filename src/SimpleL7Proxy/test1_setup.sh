#!/bin/bash
# Priority-group test: Host1 (prioritygroup=1) is tried before Host2 (prioritygroup=2).
# Unset leftover named-route vars from earlier sessions so Host1/Host2 stay in the normal host pool.
unset Path_api
unset Path_api2

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1:2:3"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3"
export LoadBalanceMode=prioritygroup
